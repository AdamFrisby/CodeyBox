using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Agents.Claude.AcpBridge;

/// <summary>
/// Main bridge orchestration. Mirrors the JS bridge's lifecycle exactly so
/// the host-side observer keeps working unchanged across the language port:
/// stdin line-delimited envelope pump → TCP/HTTP/WS server → lockfile
/// publish → claude --ide spawn → ACP frame proxying → graceful shutdown
/// on stopReason / error / timeout / claude exit.
/// </summary>
internal sealed class Bridge : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    // _pendingLock guards three things together so DrainPending can observe a
    // consistent snapshot: the outbound queue (_pendingPayloads), the current
    // peer reference (_peer), and the peer-ready flag (_peerReady). Holding
    // the lock for the entire dequeue + send keeps frame order deterministic
    // when both the stdin pump and the accept handler call DrainPending
    // concurrently.
    private readonly object _pendingLock = new();
    private readonly Queue<string> _pendingPayloads = new();
    private readonly Stream? _stdinOverride;

    private BridgeConfig _config = BridgeConfig.Default;
    private TcpListener? _listener;
    private WebSocketConnection? _peer;
    private int _port;
    private string? _lockPath;
    private string? _authToken;
    private bool _peerReady;
    private Process? _claudeProcess;
    private int _shutdownState; // 0 = running, 1 = shutting down. Updated via Interlocked.Exchange.
    private int _exitCode;
    private Timer? _turnDeadline;
    private Task? _acceptLoopTask;
    private Task? _peerReceiveTask;
    private PosixSignalRegistration? _sigterm;
    private PosixSignalRegistration? _sigint;
    private PosixSignalRegistration? _sighup;

    private bool ShutdownStarted => Volatile.Read(ref _shutdownState) != 0;

    public Bridge() { }

    /// <summary>
    /// Test-only constructor: pipe a synthetic stdin stream into the bridge
    /// so an in-process end-to-end fixture can drive RunAsync without a
    /// real sandbox. Production binary path always uses the parameterless
    /// constructor which reads from <see cref="Console.OpenStandardInput"/>.
    /// </summary>
    internal Bridge(Stream stdinForTests) { _stdinOverride = stdinForTests; }

    public async Task<int> RunAsync()
    {
        Emitter.Emit("bridge_started", w => w.WriteNumber("pid", Environment.ProcessId));

        // Posix signal handling — stop politely on SIGTERM/SIGINT/SIGHUP.
        // .NET's Console.CancelKeyPress only catches Ctrl+C (SIGINT); SIGTERM
        // (the sandbox provider's normal stop signal) and SIGHUP need
        // PosixSignalRegistration to be observed. Without these, an in-VM
        // tear-down would silently leak the ~/.claude/ide/<port>.lock file
        // and the claude --ide subprocess tree.
        //
        // Shutdown(0) cancels _cts which tells ReadStdinAsync's loop to
        // exit, BUT StreamReader.ReadLineAsync(ct) over a stdin pipe on
        // Linux is parked inside the read() syscall on the pipe fd, which
        // does NOT honour managed cancellation — the read() only returns
        // when bytes arrive or the writer closes the fd. For a signal-
        // driven shutdown (sandbox tear-down via SIGTERM, terminal hangup
        // via SIGHUP), the host has no reason to also close its end of the
        // stdin pipe, so the cooperative path would hang RunAsync
        // indefinitely. After Shutdown completes its cleanup we therefore
        // start a watchdog that Environment.Exit's the process once the
        // grace window expires. RunAsync still gets a chance to return
        // cooperatively first; the watchdog is the belt-and-braces backstop.
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Shutdown(0); ScheduleForceExitAfterSignal(); });
        _sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; Shutdown(0); ScheduleForceExitAfterSignal(); });
        _sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; Shutdown(0); ScheduleForceExitAfterSignal(); });
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown(0);

        try
        {
            await ReadStdinAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        await WaitForBackgroundTasksAsync().ConfigureAwait(false);
        return _exitCode;
    }

    public async ValueTask DisposeAsync()
    {
        Shutdown(_exitCode);
        await WaitForBackgroundTasksAsync().ConfigureAwait(false);
        _cts.Dispose();
        _turnDeadline?.Dispose();
        _sigterm?.Dispose();
        _sigint?.Dispose();
        _sighup?.Dispose();
    }

    private async Task ReadStdinAsync(CancellationToken ct)
    {
        var inputStream = _stdinOverride ?? Console.OpenStandardInput();
        using var stdin = new StreamReader(inputStream, Encoding.UTF8);
        while (!ct.IsCancellationRequested && !ShutdownStarted)
        {
            string? line;
            try { line = await stdin.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (line is null) return; // EOF
            if (line.Length == 0) continue;
            DispatchEnvelope(line);
        }
    }

    private void DispatchEnvelope(string line)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String) return;

            switch (typeEl.GetString())
            {
                case "hello":
                    HandleHello(root);
                    break;
                case "acp_send":
                    if (root.TryGetProperty("payload", out var payload)
                        && payload.ValueKind == JsonValueKind.Object)
                    {
                        EnqueuePayload(payload.GetRawText());
                        DrainPending();
                    }
                    break;
                case "shutdown":
                    Shutdown(0);
                    break;
            }
        }
    }

    private void HandleHello(JsonElement root)
    {
        _config = BridgeConfig.FromHello(root);
        _turnDeadline = new Timer(_ =>
        {
            Emitter.Emit("turn_timeout");
            Shutdown(0);
        }, null, _config.TurnTimeoutSeconds * 1000, Timeout.Infinite);

        // Generate the auth token BEFORE we accept any inbound connections so
        // the WebSocket handshake guard can never observe an empty
        // _authToken (which would short-circuit the check). Cheap, eliminates
        // the local race window.
        _authToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

        try
        {
            StartServer();
            WriteLockfile();
            if (ShutdownStarted) return; // lockfile failure already raised fatal
            SpawnClaude();
        }
        catch (Exception ex)
        {
            Fatal("startup_failed", ex.Message);
        }
    }

    private void StartServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is not null && !ShutdownStarted)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream stream;
        try
        {
            stream = client.GetStream();
        }
        catch (ObjectDisposedException)
        {
            try { client.Dispose(); } catch { }
            return;
        }

        var conn = new WebSocketConnection(stream);
        bool accepted;
        try { accepted = await conn.AcceptHandshakeAsync(_authToken ?? "", ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { client.Dispose(); } catch { } return; }

        if (!accepted)
        {
            try { client.Dispose(); } catch { }
            return;
        }

        lock (_pendingLock)
        {
            _peer = conn;
            _peerReady = true;
        }
        Emitter.Emit("peer_connected");
        DrainPending();

        _peerReceiveTask = Task.Run(() => conn.ReceiveLoopAsync(OnIncomingFrame, ct));
        try { await _peerReceiveTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_pendingLock)
            {
                _peer = null;
                _peerReady = false;
            }
            Emitter.Emit("peer_closed");
            try { client.Dispose(); } catch { }
            MaybeFinish();
        }
    }

    private void OnIncomingFrame(string text)
    {
        var classification = BridgePayloads.ClassifyIncomingFrame(text, _config,
            out var idJson, out var methodName, out var stopReason, out var errorJson);

        switch (classification)
        {
            case BridgePayloads.FrameKind.Malformed:
                return;
            case BridgePayloads.FrameKind.AutoPermission:
                if (idJson is not null) SendPeerText(BridgePayloads.BuildPermissionReplyJson(idJson));
                Emitter.Emit("permission_auto_granted",
                    w => { if (methodName is not null) w.WriteString("method", methodName); });
                return;
            case BridgePayloads.FrameKind.AutoInput:
                if (idJson is not null) SendPeerText(BridgePayloads.BuildInputReplyJson(idJson));
                Emitter.Emit("question_auto_answered",
                    w => { if (methodName is not null) w.WriteString("method", methodName); });
                return;
            case BridgePayloads.FrameKind.TurnError:
                Emitter.EmitAcpRecv(text);
                Emitter.Emit("turn_error", w =>
                {
                    w.WritePropertyName("error");
                    if (errorJson is not null)
                    {
                        w.WriteRawValue(errorJson, skipInputValidation: false);
                    }
                    else
                    {
                        w.WriteStartObject();
                        w.WriteEndObject();
                    }
                });
                Shutdown(0);
                return;
            case BridgePayloads.FrameKind.TurnComplete:
                Emitter.EmitAcpRecv(text);
                if (stopReason is not null)
                    Emitter.Emit("turn_complete", w => w.WriteString("stopReason", stopReason));
                Shutdown(0);
                return;
            default:
                Emitter.EmitAcpRecv(text);
                return;
        }
    }

    /// <summary>
    /// Snapshot the current peer under the lock so a concurrent disconnect
    /// can't NRE us, and forward the text. SendText itself is internally
    /// serialised by the WebSocketConnection's write lock, so we don't need
    /// to hold _pendingLock during the actual byte write.
    /// </summary>
    private void SendPeerText(string text)
    {
        WebSocketConnection? peer;
        lock (_pendingLock) { peer = _peer; }
        peer?.SendText(text);
    }

    private void EnqueuePayload(string payloadJson)
    {
        lock (_pendingLock) _pendingPayloads.Enqueue(payloadJson);
    }

    /// <summary>
    /// Hold <see cref="_pendingLock"/> for the ENTIRE dequeue + send loop. If
    /// two drainers raced (stdin pump after acp_send vs. accept handler after
    /// the peer attaches) and only the dequeue was synchronised, the actual
    /// SendText calls could overtake each other and deliver ACP frames out
    /// of order (e.g. session/new ahead of initialize). The lock also gives
    /// us a single consistent read of <see cref="_peer"/>/<see cref="_peerReady"/>
    /// so a peer-disconnect can't NRE the send. WebSocketConnection.SendText
    /// is non-blocking (writes to a NetworkStream) so holding the lock is
    /// cheap.
    /// </summary>
    private void DrainPending()
    {
        lock (_pendingLock)
        {
            while (_peerReady && _peer is not null && _pendingPayloads.Count > 0)
            {
                var next = _pendingPayloads.Dequeue();
                _peer.SendText(next);

                // Mirror the JS bridge's acp_sent envelope (id + method may be absent).
                EmitAcpSentMeta(next);
            }
        }
    }

    private static void EmitAcpSentMeta(string payloadJson)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(payloadJson); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            Emitter.Emit("acp_sent", w =>
            {
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("id", out var idEl))
                    {
                        w.WritePropertyName("id");
                        idEl.WriteTo(w);
                    }
                    if (root.TryGetProperty("method", out var methodEl)
                        && methodEl.ValueKind == JsonValueKind.String)
                    {
                        w.WriteString("method", methodEl.GetString());
                    }
                }
            });
        }
    }

    private void WriteLockfile()
    {
        var baseDir = _config.LockDir ?? Path.Combine(
            Environment.GetEnvironmentVariable("HOME") ?? "/", ".claude", "ide");
        try
        {
            Directory.CreateDirectory(baseDir);
            try
            {
                File.SetUnixFileMode(baseDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch { /* best-effort — chmod failure is non-fatal */ }
        }
        catch (Exception ex)
        {
            Fatal("lockdir_create_failed", ex.Message);
            return;
        }

        // _authToken is assigned in HandleHello BEFORE the listener accepts
        // any peer so the WebSocket handshake guard cannot see an empty
        // value during a connect/auth race.
        _lockPath = Path.Combine(baseDir, _port + ".lock");

        var bytes = BridgePayloads.BuildLockfileBytes(
            Environment.ProcessId, _config.WorkingDirectory, _authToken ?? string.Empty, _port);
        try
        {
            File.WriteAllBytes(_lockPath, bytes);
            try
            {
                File.SetUnixFileMode(_lockPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch { /* best-effort */ }
        }
        catch (Exception ex)
        {
            Fatal("lockfile_write_failed", ex.Message);
            return;
        }

        Emitter.Emit("ready", w =>
        {
            w.WriteNumber("port", _port);
            w.WriteString("lockPath", _lockPath);
            w.WriteStartArray("workspaceFolders");
            w.WriteStringValue(_config.WorkingDirectory);
            w.WriteEndArray();
        });
    }

    private void SpawnClaude()
    {
        var psi = new ProcessStartInfo(_config.ClaudeBinary)
        {
            WorkingDirectory = _config.WorkingDirectory,
            UseShellExecute = false,
            // MUST be true: with RedirectStandardInput = false on Linux, .NET's
            // Process.Start lets the child inherit the parent's fd 0. The
            // bridge's own fd 0 is the host envelope pipe (sandbox.ExecAsync's
            // stdin) — sharing it with claude --ide would race the bridge's
            // ReadStdinAsync loop for hello/acp_send bytes. The JS bridge
            // pinned `stdio: ['ignore', 'pipe', 'pipe']` to give claude
            // /dev/null; we close StandardInput immediately after Start to
            // give claude EOF on its stdin, which is the equivalent.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--ide");
        foreach (var a in _config.ClaudeArgs) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _config.ClaudeEnv) psi.Environment[k] = v;

        try
        {
            _claudeProcess = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Fatal("claude_spawn_failed", ex.Message);
            return;
        }
        if (_claudeProcess is null)
        {
            Fatal("claude_spawn_failed", "Process.Start returned null");
            return;
        }

        // Close claude's stdin pipe NOW so it sees EOF and can't race the
        // bridge's stdin reader for host envelope bytes. See the
        // RedirectStandardInput = true comment above.
        try { _claudeProcess.StandardInput.Close(); } catch { }

        // Capture the process locally so the Exited handler can't NRE if a
        // future shutdown path nulls the field. Subscribe BEFORE flipping
        // EnableRaisingEvents — otherwise a fast-exiting claude can fire its
        // internal exit notification before the handler is attached, and
        // MaybeFinish would never run until the turn-deadline timer fires.
        var proc = _claudeProcess;
        proc.Exited += (_, _) =>
        {
            int exit;
            try { exit = proc.ExitCode; } catch { exit = -1; }
            Emitter.Emit("claude_exit", w =>
            {
                w.WriteNumber("code", exit);
                w.WriteNull("signal");
            });
            MaybeFinish();
        };
        proc.EnableRaisingEvents = true;
        // If claude already exited between Process.Start and the Exited
        // subscription, the event will never fire — drive MaybeFinish ourselves.
        if (proc.HasExited) MaybeFinish();

        _ = Task.Run(async () =>
        {
            try
            {
                var stdout = _claudeProcess.StandardOutput;
                var buf = new char[4096];
                while (true)
                {
                    var n = await stdout.ReadAsync(buf.AsMemory()).ConfigureAwait(false);
                    if (n <= 0) return;
                    var text = new string(buf, 0, n);
                    Emitter.Emit("claude_stdout", w => w.WriteString("text", text));
                }
            }
            catch (Exception) { /* stream closed */ }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = _claudeProcess.StandardError;
                var buf = new char[4096];
                while (true)
                {
                    var n = await stderr.ReadAsync(buf.AsMemory()).ConfigureAwait(false);
                    if (n <= 0) return;
                    var text = new string(buf, 0, n);
                    Emitter.Emit("claude_stderr", w => w.WriteString("text", text));
                }
            }
            catch (Exception) { /* stream closed */ }
        });
    }

    private void MaybeFinish()
    {
        if (ShutdownStarted) return;
        var claudeExited = _claudeProcess is not null && _claudeProcess.HasExited;
        bool peerReadySnapshot;
        lock (_pendingLock) peerReadySnapshot = _peerReady;
        if (claudeExited && !peerReadySnapshot) Shutdown(0);
    }

    private void Fatal(string message, string? detail)
    {
        Emitter.Emit("fatal", w =>
        {
            w.WriteString("message", message);
            if (detail is null) w.WriteNull("detail");
            else w.WriteString("detail", detail);
        });
        Shutdown(2);
    }

    private void Shutdown(int code)
    {
        // Atomic check-and-set so multiple Shutdown callers (turn-deadline
        // timer, posix signal, claude exited, peer closed, OnIncomingFrame
        // stopReason / error) cannot all run the cleanup body — that would
        // emit duplicate claude_exit envelopes and double-delete the lockfile.
        if (Interlocked.Exchange(ref _shutdownState, 1) != 0) return;
        _exitCode = code;
        try { _turnDeadline?.Dispose(); } catch { }
        try
        {
            if (_claudeProcess is { HasExited: false } p)
            {
                TerminateClaudeProcess(p);
            }
        }
        catch { }
        WebSocketConnection? peerToClose;
        lock (_pendingLock) peerToClose = _peer;
        try { peerToClose?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { if (_lockPath is not null) File.Delete(_lockPath); } catch { }
        try { _cts.Cancel(); } catch { }
    }

    /// <summary>
    /// SIGTERM the claude --ide subprocess with a brief grace window, then
    /// fall back to SIGKILL (Process.Kill) if it didn't exit. Parity with the
    /// JS bridge's <c>claudeProc.kill('SIGTERM')</c> — claude needs the polite
    /// signal so it gets a chance to flush its session JSONL transcript before
    /// exiting. SIGKILL leaves a half-written transcript that the next
    /// session/load can read back as a thinking-block immutability 400.
    /// .NET's <see cref="Process.Kill(bool)"/> always sends SIGKILL on Linux
    /// with no SIGTERM overload, so we P/Invoke <c>kill(2)</c> for the polite
    /// signal and use <see cref="Process.Kill(bool)"/> only as the fallback.
    /// </summary>
    private static void TerminateClaudeProcess(Process p)
    {
        // Snapshot the pid before signalling — once the process exits, .NET
        // disposes its internal handle and reading <c>p.Id</c> can throw.
        int pid;
        try { pid = p.Id; }
        catch { pid = 0; }

        bool politeSent = false;
        if (pid > 0)
        {
            try
            {
                // SIGTERM = 15 on Linux.
                politeSent = NativeMethods.Kill(pid, 15) == 0;
            }
            catch { politeSent = false; }
        }

        if (politeSent)
        {
            // Brief grace window so claude can flush ~/.claude/projects/<slug>/<session>.jsonl
            // before exiting. 1.5s is well under any operator-visible
            // teardown latency while comfortably covering the JSONL flush
            // path; if claude is wedged we still SIGKILL below.
            try { p.WaitForExit(milliseconds: 1500); }
            catch { /* WaitForExit can throw if the handle is racing dispose */ }
        }

        bool exited;
        try { exited = p.HasExited; }
        catch { exited = true; }

        if (!exited)
        {
            // Either the polite SIGTERM failed to deliver or claude is
            // wedged. Fall back to the original SIGKILL behaviour so a
            // hung child can never pin shutdown.
            try { p.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>
    /// Belt-and-braces watchdog the SIGTERM/SIGINT/SIGHUP handlers schedule
    /// after running <see cref="Shutdown(int)"/>. Cooperative exit (RunAsync
    /// returning normally after <c>_cts.Cancel()</c> unblocks the stdin read)
    /// is preferred and happens first when the read does unblock; the
    /// watchdog only fires if the read stayed parked past the grace window —
    /// the regression mode this guards against. By the time this delay
    /// elapses Shutdown has already deleted the lockfile, SIGTERM'd claude,
    /// stopped the TCP listener and cancelled the CTS, so force-exit is
    /// safe — the process simply leaves no half-cleaned state behind.
    /// </summary>
    private void ScheduleForceExitAfterSignal()
    {
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1500).ConfigureAwait(false); }
            catch { }
            try { Environment.Exit(_exitCode); }
            catch { }
        });
    }

    private static class NativeMethods
    {
        // DllImport with primitive int marshalling is fully NativeAOT-safe
        // (no codegen required at runtime) and avoids the AllowUnsafeBlocks
        // requirement LibraryImport's source generator imposes. The bridge
        // only ever ships on linux-musl-x64 so the libc resolution is fixed.
        //
        // CRITICAL: the published binary uses StaticExecutable=true, which
        // strips runtime dlopen support. Without the DirectPInvoke + the
        // NativeLibrary items in CodeyBox.Agents.Claude.AcpBridge.csproj
        // the NativeAOT PInvoke resolver would fall back to dlopen("libc.so")
        // and throw DllNotFoundException — which TerminateClaudeProcess
        // catches silently and degrades the SIGTERM-grace path to bare
        // SIGKILL, re-introducing the half-written-JSONL → thinking-block
        // immutability 400 cluster the polite signal was added to prevent.
        // Keep the csproj DirectPInvoke + NativeLibrary entries in sync
        // with this DllImport.
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int pid, int sig);
    }

    private async Task WaitForBackgroundTasksAsync()
    {
        if (_acceptLoopTask is not null)
        {
            try { await _acceptLoopTask.ConfigureAwait(false); }
            catch { }
        }
        if (_peerReceiveTask is not null)
        {
            try { await _peerReceiveTask.ConfigureAwait(false); }
            catch { }
        }
    }
}

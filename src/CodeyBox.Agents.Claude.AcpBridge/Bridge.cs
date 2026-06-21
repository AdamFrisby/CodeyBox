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
    private readonly Func<TcpListener> _listenerFactory;
    private readonly Action<int> _forceExit;
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
    private Stream? _stdinStream;
    private TcpListener? _listener;
    private WebSocketConnection? _peer;
    private int _port;
    private string? _lockPath;
    private string? _authToken;
    private bool _peerReady;
    private Process? _claudeProcess;
    private int _shutdownState; // 0 = running, 1 = shutting down. Updated via Interlocked.Exchange.
    private int _claudeExitEmitted; // 0 = not yet emitted, 1 = emitted. Single-fire guard.
    private int _exitCode;
    private Timer? _turnDeadline;
    private Task? _acceptLoopTask;
    private Task? _peerReceiveTask;
    private Task? _claudeMonitorTask;
    private Task? _claudeStdoutPumpTask;
    private Task? _claudeStderrPumpTask;
    private PosixSignalRegistration? _sigterm;
    private PosixSignalRegistration? _sigint;
    private PosixSignalRegistration? _sighup;

    private bool ShutdownStarted => Volatile.Read(ref _shutdownState) != 0;

    public Bridge()
    {
        _listenerFactory = CreateLoopbackListener;
        _forceExit = Environment.Exit;
    }

    /// <summary>
    /// Test-only constructor: pipe a synthetic stdin stream into the bridge
    /// so an in-process end-to-end fixture can drive RunAsync without a
    /// real sandbox. Production binary path always uses the parameterless
    /// constructor which reads from <see cref="Console.OpenStandardInput"/>.
    /// </summary>
    internal Bridge(Stream stdinForTests, Func<TcpListener>? listenerFactory = null)
        : this(stdinForTests, listenerFactory, _ => { })
    {
    }

    internal Bridge(
        Stream stdinForTests,
        Func<TcpListener>? listenerFactory,
        Action<int> forceExitForTests)
    {
        _stdinOverride = stdinForTests;
        _listenerFactory = listenerFactory ?? CreateLoopbackListener;
        _forceExit = forceExitForTests;
    }

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
        // Shutdown(0) cancels _cts and closes the stdin stream to unblock
        // ReadStdinAsync. StreamReader.ReadLineAsync(ct) over a stdin pipe on
        // Linux can be parked inside a read() syscall that does not honour
        // managed cancellation, and for signal-driven shutdown the host has no
        // reason to also close its end of the pipe. After Shutdown completes
        // cleanup we therefore start a watchdog that Environment.Exit's the
        // process once the grace window expires. RunAsync still gets a chance
        // to return cooperatively first; the watchdog is the backstop.
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
        _stdinStream = inputStream;
        using var stdin = new StreamReader(inputStream, Encoding.UTF8);
        while (!ct.IsCancellationRequested && !ShutdownStarted)
        {
            string? line;
            try { line = await stdin.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (IOException) when (ShutdownStarted) { return; }
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
        _listener = _listenerFactory();
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private static TcpListener CreateLoopbackListener() => new(IPAddress.Loopback, 0);

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
        catch (Exception ex)
        {
            Fatal("acp_frame_handler_failed", ex.Message);
        }
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
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(baseDir,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
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
            WriteLockfileBytesAtomically(_lockPath, bytes);
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

    private static void WriteLockfileBytesAtomically(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using (var fs = new FileStream(tmp, options))
            {
                fs.Write(bytes, 0, bytes.Length);
            }
            File.Move(tmp, path, overwrite: true);
            tmp = null!;
        }
        finally
        {
            if (tmp is not null && File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch { }
            }
        }
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

        // Drive claude_exit via an awaitable monitor task instead of the
        // proc.Exited event. Two reasons:
        //
        // 1. proc.Exited is documented to potentially miss the exit if the
        //    process exits before EnableRaisingEvents = true is set — under
        //    CPU stress the order Process.Start → close-stdin → subscribe →
        //    EnableRaisingEvents is non-trivial wall-clock, and a fast stub
        //    that's already been SIGTERM'd by a racing Shutdown can die in
        //    that window. The old `if (proc.HasExited) MaybeFinish()` post-
        //    check called MaybeFinish but did NOT emit claude_exit, so the
        //    envelope was silently dropped and the host's session-completion
        //    observer would never see the turn close.
        // 2. proc.Exited fires on a .NET monitor thread that is NOT awaited
        //    by Bridge.DisposeAsync, so a late-firing event could call
        //    Emitter.Emit AFTER the test's OverrideStreamForTests scope has
        //    been disposed. The emit would then race onto whatever _stdout
        //    is currently set — either the real process stdout (visible as
        //    JSON noise in the test runner's output) or, worse, the capture
        //    stream of a SUBSEQUENT test. The latter caused the audit-iter-10
        //    flake: a leaked claude_exit / fatal from one test contaminated
        //    the capture of the next test, breaking envelope-count assertions
        //    that read `Stdout.Snapshot()`.
        //
        // The monitor task is tracked in _claudeMonitorTask and awaited in
        // WaitForBackgroundTasksAsync — so by the time RunAsync returns and
        // DisposeAsync completes, the emit has either happened or won't
        // happen (because the process never exits and we cancelled), but
        // it is NEVER racing the test's emitter-scope disposal.
        var proc = _claudeProcess;
        _claudeMonitorTask = Task.Run(async () =>
        {
            try
            {
                await proc.WaitForExitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // _cts cancelled by Shutdown — but Shutdown also terminates
                // claude, so a short uncancelled wait should normally succeed.
                // Keep it bounded: if SIGTERM/SIGKILL failed for any reason,
                // cleanup must still release the bridge process instead of
                // pinning RunAsync forever.
                if (!await WaitForProcessExitAfterShutdownAsync(proc).ConfigureAwait(false))
                    return;
            }
            catch (Exception) { return; }
            EmitClaudeExitOnce(proc);
            MaybeFinish();
        });

        // Pass _cts.Token to the reader so Shutdown's _cts.Cancel() unblocks
        // it cleanly. Without the token, an orphaned grandchild (e.g. a bash
        // stub backgrounding `sleep 60 & wait $!` and exit-trapping SIGTERM
        // leaves the sleep process inheriting bash's stdout/stderr and the
        // pipe stays open even after bash exits) would keep the reader
        // blocked in ReadAsync forever, and WaitForBackgroundTasksAsync —
        // which now awaits this task — would never complete.
        var stdoutStream = _claudeProcess.StandardOutput;
        var stderrStream = _claudeProcess.StandardError;
        _claudeStdoutPumpTask = Task.Run(() => PumpReaderAsync(stdoutStream, "claude_stdout", _cts.Token));
        _claudeStderrPumpTask = Task.Run(() => PumpReaderAsync(stderrStream, "claude_stderr", _cts.Token));
    }

    private static async Task PumpReaderAsync(StreamReader reader, string envelopeType, CancellationToken ct)
    {
        try
        {
            var buf = new char[4096];
            while (true)
            {
                var n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) return;
                var text = new string(buf, 0, n);
                Emitter.Emit(envelopeType, w => w.WriteString("text", text));
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception) { /* stream closed */ }
    }

    /// <summary>
    /// Single-fire claude_exit emission. The monitor task calls this when the
    /// claude process exits naturally; Shutdown can also call it directly when
    /// it terminates the process so the envelope lands before background tasks
    /// have a chance to be cancelled (and so a fast-exiting stub can never
    /// drop the envelope because the monitor task hadn't yet started parking
    /// on WaitForExitAsync). The Interlocked guard makes the second caller a
    /// no-op so duplicates can't be emitted.
    /// </summary>
    private void EmitClaudeExitOnce(Process proc)
    {
        if (Interlocked.Exchange(ref _claudeExitEmitted, 1) != 0) return;
        int exit;
        try { exit = proc.ExitCode; } catch { exit = -1; }
        var signal = TryMapUnixSignalExit(exit);
        Emitter.Emit("claude_exit", w =>
        {
            if (signal is null) w.WriteNumber("code", exit);
            else w.WriteNull("code");

            if (signal is null) w.WriteNull("signal");
            else w.WriteString("signal", signal);
        });
    }

    private static string? TryMapUnixSignalExit(int exitCode)
    {
        if (OperatingSystem.IsWindows()) return null;
        return (exitCode - 128) switch
        {
            1 => "SIGHUP",
            2 => "SIGINT",
            9 => "SIGKILL",
            15 => "SIGTERM",
            _ => null,
        };
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
            if (_claudeProcess is { } p)
            {
                if (!HasProcessExited(p)) TerminateClaudeProcess(p);
                // Emit claude_exit BEFORE cancelling _cts. The monitor task
                // also calls this on its WaitForExitAsync completion, but
                // emitting it here while we're still on the cleanup path
                // means a host-side observer sees claude_exit before
                // peer_closed / process teardown, and it lands deterministic-
                // ally before the test's emitter scope can be disposed (the
                // monitor task may not have a chance to wake up before the
                // CTS cancel propagates). EmitClaudeExitOnce is single-fire,
                // so the monitor task's later call is a no-op.
                EmitClaudeExitOnce(p);
            }
        }
        catch { }
        WebSocketConnection? peerToClose;
        lock (_pendingLock) peerToClose = _peer;
        try { peerToClose?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { if (_lockPath is not null) File.Delete(_lockPath); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _stdinStream?.Dispose(); } catch { }
    }

    private static bool HasProcessExited(Process proc)
    {
        try { return proc.HasExited; }
        catch { return true; }
    }

    private static async Task<bool> WaitForProcessExitAfterShutdownAsync(Process proc)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
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
            try { p.WaitForExit(milliseconds: 500); } catch { }
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
            try { _forceExit(_exitCode); }
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
        // strips runtime dlopen support. Without the DirectPInvoke item in
        // CodeyBox.Agents.Claude.AcpBridge.csproj
        // the NativeAOT PInvoke resolver would fall back to dlopen("libc.so")
        // and throw DllNotFoundException — which TerminateClaudeProcess
        // catches silently and degrades the SIGTERM-grace path to bare
        // SIGKILL, re-introducing the half-written-JSONL → thinking-block
        // immutability 400 cluster the polite signal was added to prevent.
        // The musl C runtime is linked by the NativeAOT toolchain; do not add
        // a NativeLibrary Include="libc" item, because Unix NativeLibrary items
        // are passed to the linker as file paths rather than -l names.
        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        internal static extern int Kill(int pid, int sig);
    }

    private async Task WaitForBackgroundTasksAsync()
    {
        // Every background task that calls Emitter.Emit must be drained here
        // BEFORE DisposeAsync returns. The test harness wraps each Bridge
        // instance in an Emitter.OverrideStreamForTests scope and disposes
        // that scope right after _runTask completes — if any of these tasks
        // call Emitter.Emit after the scope is gone, the envelope either
        // leaks to the real process stdout or (under cross-test parallelism)
        // contaminates the next test's capture stream. The latter caused
        // the audit-iter-10 flake where one test's claude_exit landed in
        // another test's envelope list and broke its count assertions.
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
        if (_claudeMonitorTask is not null)
        {
            try { await _claudeMonitorTask.ConfigureAwait(false); }
            catch { }
        }
        if (_claudeStdoutPumpTask is not null)
        {
            try { await _claudeStdoutPumpTask.ConfigureAwait(false); }
            catch { }
        }
        if (_claudeStderrPumpTask is not null)
        {
            try { await _claudeStderrPumpTask.ConfigureAwait(false); }
            catch { }
        }
    }
}

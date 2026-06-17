using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
    private readonly object _pendingLock = new();
    private readonly Queue<string> _pendingPayloads = new();

    private BridgeConfig _config = BridgeConfig.Default;
    private TcpListener? _listener;
    private WebSocketConnection? _peer;
    private int _port;
    private string? _lockPath;
    private string? _authToken;
    private bool _peerReady;
    private Process? _claudeProcess;
    private bool _shutdownStarted;
    private int _exitCode;
    private Timer? _turnDeadline;
    private Task? _acceptLoopTask;
    private Task? _peerReceiveTask;

    public async Task<int> RunAsync()
    {
        Emitter.Emit("bridge_started", w => w.WriteNumber("pid", Environment.ProcessId));

        // Posix signal handling — stop politely on SIGTERM/SIGINT.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Shutdown(0); };
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
    }

    private async Task ReadStdinAsync(CancellationToken ct)
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        while (!ct.IsCancellationRequested && !_shutdownStarted)
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
            if (_shutdownStarted) return; // lockfile failure already raised fatal
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
        while (!ct.IsCancellationRequested && _listener is not null && !_shutdownStarted)
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

        _peer = conn;
        _peerReady = true;
        Emitter.Emit("peer_connected");
        DrainPending();

        _peerReceiveTask = Task.Run(() => conn.ReceiveLoopAsync(OnIncomingFrame, ct));
        try { await _peerReceiveTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally
        {
            _peer = null;
            _peerReady = false;
            Emitter.Emit("peer_closed");
            try { client.Dispose(); } catch { }
            MaybeFinish();
        }
    }

    private void OnIncomingFrame(string text)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { return; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("method", out var methodEl)
                && methodEl.ValueKind == JsonValueKind.String)
            {
                var method = methodEl.GetString();
                if (TryAutoHandle(root, method)) return;
            }

            Emitter.EmitAcpRecv(text);

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("error", out _))
                {
                    EmitTurnError(root);
                    Shutdown(0);
                    return;
                }
                if (root.TryGetProperty("result", out var resultEl)
                    && resultEl.ValueKind == JsonValueKind.Object)
                {
                    string? stopReason = null;
                    if (resultEl.TryGetProperty("stopReason", out var sr) && sr.ValueKind == JsonValueKind.String)
                        stopReason = sr.GetString();
                    else if (resultEl.TryGetProperty("stop_reason", out var sr2) && sr2.ValueKind == JsonValueKind.String)
                        stopReason = sr2.GetString();
                    if (stopReason is not null)
                    {
                        Emitter.Emit("turn_complete", w => w.WriteString("stopReason", stopReason));
                        Shutdown(0);
                    }
                }
            }
        }
    }

    private bool TryAutoHandle(JsonElement root, string? method)
    {
        if (_config.AutoApprovePermissions
            && (method == "session/request_permission" || method == "permission/request"))
        {
            if (root.TryGetProperty("id", out var idEl)) ReplyPermission(idEl);
            Emitter.Emit("permission_auto_granted",
                w => { if (method is not null) w.WriteString("method", method); });
            return true;
        }
        if (_config.AutoAnswerQuestions
            && (method == "session/request_input" || method == "input/request"))
        {
            if (root.TryGetProperty("id", out var idEl)) ReplyInput(idEl);
            Emitter.Emit("question_auto_answered",
                w => { if (method is not null) w.WriteString("method", method); });
            return true;
        }
        return false;
    }

    private void ReplyPermission(JsonElement idEl)
    {
        if (_peer is null) return;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            idEl.WriteTo(w);
            w.WriteStartObject("result");
            w.WriteStartObject("outcome");
            w.WriteString("outcome", "selected");
            w.WriteString("optionId", "allow_once");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        _peer.SendText(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private void ReplyInput(JsonElement idEl)
    {
        if (_peer is null) return;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WritePropertyName("id");
            idEl.WriteTo(w);
            w.WriteStartObject("result");
            w.WriteString("value",
                "<codeybox-question>: agent asked a blocking question; default applied, continuing.");
            w.WriteEndObject();
            w.WriteEndObject();
        }
        _peer.SendText(Encoding.UTF8.GetString(ms.ToArray()));
    }

    private void EmitTurnError(JsonElement root)
    {
        Emitter.Emit("turn_error", w =>
        {
            w.WritePropertyName("error");
            if (root.TryGetProperty("error", out var errEl)) errEl.WriteTo(w);
            else w.WriteStartObject();
        });
    }

    private void EnqueuePayload(string payloadJson)
    {
        lock (_pendingLock) _pendingPayloads.Enqueue(payloadJson);
    }

    private void DrainPending()
    {
        while (_peerReady && _peer is not null)
        {
            string next;
            lock (_pendingLock)
            {
                if (_pendingPayloads.Count == 0) return;
                next = _pendingPayloads.Dequeue();
            }
            _peer.SendText(next);

            // Mirror the JS bridge's acp_sent envelope (id + method may be absent).
            EmitAcpSentMeta(next);
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

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("pid", Environment.ProcessId);
            w.WriteStartArray("workspaceFolders");
            w.WriteStringValue(_config.WorkingDirectory);
            w.WriteEndArray();
            w.WriteString("ideName", "CodeyBox");
            w.WriteString("transport", "ws");
            w.WriteBoolean("runningInWindows", false);
            w.WriteString("authToken", _authToken);
            w.WriteString("url", "ws://127.0.0.1:" + _port);
            w.WriteEndObject();
        }
        try
        {
            File.WriteAllBytes(_lockPath, ms.ToArray());
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
            RedirectStandardInput = false,
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

        _claudeProcess.EnableRaisingEvents = true;
        _claudeProcess.Exited += (_, _) =>
        {
            Emitter.Emit("claude_exit", w =>
            {
                w.WriteNumber("code", _claudeProcess.ExitCode);
                w.WriteNull("signal");
            });
            MaybeFinish();
        };

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
        if (_shutdownStarted) return;
        var claudeExited = _claudeProcess is not null && _claudeProcess.HasExited;
        if (claudeExited && !_peerReady) Shutdown(0);
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
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _exitCode = code;
        try { _turnDeadline?.Dispose(); } catch { }
        try
        {
            if (_claudeProcess is { HasExited: false } p)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch { }
        try { _peer?.Close(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { if (_lockPath is not null) File.Delete(_lockPath); } catch { }
        try { _cts.Cancel(); } catch { }
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

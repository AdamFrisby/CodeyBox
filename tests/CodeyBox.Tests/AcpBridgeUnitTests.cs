using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeyBox.Agents.Claude.AcpBridge;

namespace CodeyBox.Tests;

/// <summary>
/// Direct unit tests for the in-sandbox C# bridge. Production tests in
/// <see cref="ClaudeAcpTransportTests"/> exercise the bridge end-to-end via a
/// <c>BridgeSandbox</c> fake that synthesises bridge stdout, which means a
/// behavioural drift inside Bridge / WebSocketConnection / BridgeConfig /
/// Emitter never trips those tests. The fixtures here pin the wire-level
/// contract of each component so a regression in any of:
///
/// <list type="bullet">
///   <item>BridgeConfig.FromHello field parsing</item>
///   <item>Emitter envelope shape (line-delimited JSON, type field first)</item>
///   <item>RFC6455 handshake (Sec-WebSocket-Accept derivation, auth header)</item>
///   <item>Frame encode/decode (text opcode, length-form boundaries, masking)</item>
/// </list>
///
/// surfaces here rather than via mysterious turn failures in production.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class AcpBridgeUnitTests
{
    private static readonly SemaphoreSlim EnvironmentVariableGate = new(1, 1);

    // ── BridgeConfig.FromHello ─────────────────────────────────────────────────

    [Fact]
    public void BridgeConfig_FromHello_ParsesAllSupportedFields()
    {
        var hello = JsonDocument.Parse("""
            {
              "type": "hello",
              "autoApprovePermissions": false,
              "autoAnswerQuestions": false,
              "claudeBinary": "/opt/claude/claude",
              "claudeArgs": ["--effort", "high", "--model", "claude-opus-4-7"],
              "workingDirectory": "/work/repo",
              "claudeEnv": { "API_TIMEOUT_MS": "60000", "CLAUDE_CODE_OAUTH_TOKEN": "tok" },
              "lockDir": "/tmp/locks",
              "turnTimeoutSeconds": 1200
            }
            """).RootElement;

        var cfg = BridgeConfig.FromHello(hello);

        Assert.False(cfg.AutoApprovePermissions);
        Assert.False(cfg.AutoAnswerQuestions);
        Assert.Equal("/opt/claude/claude", cfg.ClaudeBinary);
        Assert.Equal(new[] { "--effort", "high", "--model", "claude-opus-4-7" }, cfg.ClaudeArgs);
        Assert.Equal("/work/repo", cfg.WorkingDirectory);
        Assert.Equal("60000", cfg.ClaudeEnv["API_TIMEOUT_MS"]);
        Assert.Equal("tok", cfg.ClaudeEnv["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.Equal("/tmp/locks", cfg.LockDir);
        Assert.Equal(1200, cfg.TurnTimeoutSeconds);
    }

    [Fact]
    public void BridgeConfig_FromHello_MissingFieldsFallBackToDefaults()
    {
        // Forward-compat: an older host might omit optional fields. The bridge
        // must keep working with sensible defaults rather than crashing on
        // missing keys.
        var hello = JsonDocument.Parse("""{"type":"hello"}""").RootElement;
        var cfg = BridgeConfig.FromHello(hello);

        Assert.True(cfg.AutoApprovePermissions);
        Assert.True(cfg.AutoAnswerQuestions);
        Assert.Equal("claude", cfg.ClaudeBinary);
        Assert.Empty(cfg.ClaudeArgs);
        Assert.Empty(cfg.ClaudeEnv);
        Assert.Null(cfg.LockDir);
        Assert.Equal(900, cfg.TurnTimeoutSeconds);
    }

    [Fact]
    public void BridgeConfig_FromHello_TurnTimeoutSecondsHasFloor()
    {
        // The host can request a smaller turn timeout, but the bridge enforces
        // a 10s minimum so the deadline timer doesn't fire before claude
        // finishes spawning.
        var hello = JsonDocument.Parse("""{"type":"hello","turnTimeoutSeconds":3}""").RootElement;
        var cfg = BridgeConfig.FromHello(hello);
        Assert.Equal(10, cfg.TurnTimeoutSeconds);
    }

    [Fact]
    public void BridgeConfig_FromHello_IgnoresNonStringClaudeArgEntries()
    {
        var hello = JsonDocument.Parse("""
            {"type":"hello","claudeArgs":["--ok", 5, null, "--also-ok"]}
            """).RootElement;
        var cfg = BridgeConfig.FromHello(hello);
        Assert.Equal(new[] { "--ok", "--also-ok" }, cfg.ClaudeArgs);
    }

    [Fact]
    public void BridgeConfig_FromHello_InvalidScalarTypesFallBackToDefaults()
    {
        var hello = JsonDocument.Parse("""
            {
              "type": "hello",
              "autoApprovePermissions": "false",
              "autoAnswerQuestions": 0,
              "claudeBinary": 42,
              "workingDirectory": false,
              "lockDir": { "not": "a string" },
              "turnTimeoutSeconds": "900",
              "claudeArgs": { "not": "an array" },
              "claudeEnv": [ "not", "an", "object" ]
            }
            """).RootElement;

        var cfg = BridgeConfig.FromHello(hello);

        Assert.Equal(BridgeConfig.Default.AutoApprovePermissions, cfg.AutoApprovePermissions);
        Assert.Equal(BridgeConfig.Default.AutoAnswerQuestions, cfg.AutoAnswerQuestions);
        Assert.Equal(BridgeConfig.Default.ClaudeBinary, cfg.ClaudeBinary);
        Assert.Equal(BridgeConfig.Default.WorkingDirectory, cfg.WorkingDirectory);
        Assert.Null(cfg.LockDir);
        Assert.Equal(BridgeConfig.Default.TurnTimeoutSeconds, cfg.TurnTimeoutSeconds);
        Assert.Empty(cfg.ClaudeArgs);
        Assert.Empty(cfg.ClaudeEnv);
    }

    [Fact]
    public void BridgeConfig_FromHello_IgnoresNonStringClaudeEnvValues()
    {
        var hello = JsonDocument.Parse("""
            {
              "type": "hello",
              "claudeEnv": {
                "GOOD": "kept",
                "NUMBER": 42,
                "NULL": null,
                "OBJECT": {}
              }
            }
            """).RootElement;

        var cfg = BridgeConfig.FromHello(hello);

        Assert.Equal("kept", cfg.ClaudeEnv["GOOD"]);
        Assert.False(cfg.ClaudeEnv.ContainsKey("NUMBER"));
        Assert.False(cfg.ClaudeEnv.ContainsKey("NULL"));
        Assert.False(cfg.ClaudeEnv.ContainsKey("OBJECT"));
    }

    // ── Emitter envelope shape ─────────────────────────────────────────────────

    [Fact]
    public void Emitter_Emit_ProducesLineDelimitedJsonWithTypeField()
    {
        var captured = CaptureStdout(() =>
        {
            Emitter.Emit("ready", w =>
            {
                w.WriteNumber("port", 41999);
                w.WriteString("lockPath", "/home/u/.claude/ide/41999.lock");
            });
        });

        Assert.EndsWith("\n", captured);
        var line = captured.TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("ready", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(41999, doc.RootElement.GetProperty("port").GetInt32());
        Assert.Equal("/home/u/.claude/ide/41999.lock", doc.RootElement.GetProperty("lockPath").GetString());
    }

    [Fact]
    public void Emitter_EmitTypeOnly_ProducesValidJson()
    {
        var captured = CaptureStdout(() => Emitter.Emit("peer_connected"));
        var line = captured.TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("peer_connected", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Emitter_EmitAcpRecv_PreservesRawPayload()
    {
        // session/update notifications carry deep nested JSON. The emitter
        // must inline the host-supplied raw JSON without re-encoding (which
        // would risk a property-name drift like input_tokens → inputTokens).
        var payload = """{"jsonrpc":"2.0","method":"session/update","params":{"update":{"sessionUpdate":"agent_message_chunk"}}}""";
        var captured = CaptureStdout(() => Emitter.EmitAcpRecv(payload));
        var line = captured.TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("acp_recv", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("session/update",
            doc.RootElement.GetProperty("payload").GetProperty("method").GetString());
        Assert.Equal("agent_message_chunk",
            doc.RootElement.GetProperty("payload").GetProperty("params").GetProperty("update")
                .GetProperty("sessionUpdate").GetString());
    }

    [Fact]
    public void Emitter_EscapesAwkwardCharactersInTypeField()
    {
        // Any character that needs escaping in JSON (quote, backslash, control)
        // must be encoded by Utf8JsonWriter rather than naively interpolated —
        // a regression to $"\"type\":\"{type}\"" would corrupt the envelope.
        var captured = CaptureStdout(() => Emitter.Emit("a\"b\\c"));
        var line = captured.TrimEnd('\n');
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("a\"b\\c", doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ProgramMain_EmptyStdin_ExecutesBridgeEntryPointAndEmitsStartedEnvelope()
    {
        var bridgeDllPath = typeof(Bridge).Assembly.Location;
        Assert.True(File.Exists(bridgeDllPath),
            "AcpBridge dll missing at " + bridgeDllPath +
            " — the test project should ProjectReference the AcpBridge assembly.");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(bridgeDllPath);
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null for dotnet exec.");
        await proc.StandardInput.DisposeAsync();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

        var stdout = await stdoutTask.WaitAsync(TimeSpan.FromSeconds(5));
        var stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, proc.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(stderr), "bridge entry point wrote stderr: " + stderr);

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var line = Assert.Single(lines);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("bridge_started", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("pid").GetInt32() > 0);
    }

    // ── WebSocketConnection: RFC6455 handshake + frame round-trip ──────────────

    [Fact]
    public async Task WebSocketConnection_AcceptHandshake_ChecksAuthTokenAndAcceptKey()
    {
        // Stand up an in-process TCP listener, drive a fake claude --ide HTTP
        // upgrade against it, and assert the server reply is a 101 with the
        // RFC6455-computed Sec-WebSocket-Accept value.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const string authToken = "secret-token-deadbeef";
        const string clientKey = "dGhlIHNhbXBsZSBub25jZQ=="; // RFC6455 §1.3 worked example.
        var acceptTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            var ns = server.GetStream();
            var ws = new WebSocketConnection(ns);
            return await ws.AcceptHandshakeAsync(authToken, CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: " + clientKey + "\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "x-claude-code-ide-authorization: " + authToken + "\r\n" +
            "\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[1024];
        var n = await s.ReadAsync(respBuf.AsMemory());
        var resp = Encoding.ASCII.GetString(respBuf, 0, n);
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", resp);
        // Worked example: SHA1("dGhlIHNhbXBsZSBub25jZQ==258EAFA5-E914-47DA-95CA-C5AB0DC85B11")
        // base64 = "s3pPLMBiTxaQ9kYGzzhZRbK+xOo="
        Assert.Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", resp);
        Assert.True(await acceptTask);
        listener.Stop();
    }

    [Fact]
    public async Task WebSocketConnection_AcceptHandshake_RejectsWrongAuthTokenWith401()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var acceptTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            var ws = new WebSocketConnection(server.GetStream());
            return await ws.AcceptHandshakeAsync("the-real-token", CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: AAAA\r\nSec-WebSocket-Version: 13\r\n" +
            "x-claude-code-ide-authorization: wrong-token\r\n\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[256];
        var n = await s.ReadAsync(respBuf.AsMemory());
        var resp = Encoding.ASCII.GetString(respBuf, 0, n);
        Assert.StartsWith("HTTP/1.1 401 Unauthorized", resp);
        Assert.False(await acceptTask);
        listener.Stop();
    }

    [Fact]
    public async Task WebSocketConnection_FrameRoundTrip_PreservesPayloadAcrossLengthForms()
    {
        // Exercise all three RFC6455 length encodings (1-byte, 2-byte, 8-byte)
        // so a regression in BuildTextFrame's 126/65536 boundary or in
        // TryParseFrame's mask handling is caught.
        foreach (var payloadLen in new[] { 5, 125, 126, 200, 65535, 65536, 70000 })
        {
            await AssertTextFrameRoundTrip(payloadLen);
        }
    }

    private static async Task AssertTextFrameRoundTrip(int payloadLen)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Server side: accept, handshake, send one text frame, then receive
        // one text frame and capture it.
        string? received = null;
        var serverDone = new TaskCompletionSource<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var server = await listener.AcceptTcpClientAsync();
                var ws = new WebSocketConnection(server.GetStream());
                var ok = await ws.AcceptHandshakeAsync(authToken: "", CancellationToken.None);
                if (!ok) { serverDone.TrySetException(new Exception("handshake failed")); return; }

                var payload = new string('x', payloadLen);
                ws.SendText(payload);

                var got = new TaskCompletionSource<string>();
                _ = ws.ReceiveLoopAsync(t => got.TrySetResult(t), CancellationToken.None);
                received = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
                serverDone.TrySetResult(received);
            }
            catch (Exception ex) { serverDone.TrySetException(ex); }
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: AAAA\r\nSec-WebSocket-Version: 13\r\n\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[1024];
        var headerSoFar = new List<byte>();
        while (true)
        {
            var n = await s.ReadAsync(respBuf.AsMemory());
            if (n <= 0) break;
            for (int i = 0; i < n; i++) headerSoFar.Add(respBuf[i]);
            // wait for \r\n\r\n end of headers; the server may have already
            // sent the first text frame after it.
            if (HasHeaderTerminator(headerSoFar, out var headerEnd))
            {
                // Parse the text frame that follows the header.
                var afterHeader = headerSoFar.GetRange(headerEnd, headerSoFar.Count - headerEnd).ToArray();
                var frame = await ReadTextFrameFromUnmasked(s, afterHeader);
                Assert.Equal(new string('x', payloadLen), frame);
                break;
            }
        }

        // Echo a masked frame back to the server.
        var echoPayload = Encoding.UTF8.GetBytes(new string('y', payloadLen));
        var maskedFrame = BuildClientMaskedTextFrame(echoPayload);
        await s.WriteAsync(maskedFrame);
        await s.FlushAsync();

        await serverDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new string('y', payloadLen), received);

        listener.Stop();
    }

    [Fact]
    public async Task WebSocketConnection_HandshakeRejectsMissingSecWebSocketKey()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            var ws = new WebSocketConnection(server.GetStream());
            return await ws.AcceptHandshakeAsync("any", CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[256];
        var n = await s.ReadAsync(respBuf.AsMemory());
        Assert.StartsWith("HTTP/1.1 400 Bad Request",
            Encoding.ASCII.GetString(respBuf, 0, n));
        Assert.False(await accept);
        listener.Stop();
    }

    [Fact]
    public async Task WebSocketConnection_AcceptHandshake_RejectsOversizedMalformedHeadersWith400()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var accept = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            var ws = new WebSocketConnection(server.GetStream());
            return await ws.AcceptHandshakeAsync("any", CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n" + new string('x', 17 * 1024));
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[256];
        var n = await s.ReadAsync(respBuf.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("HTTP/1.1 400 Bad Request",
            Encoding.ASCII.GetString(respBuf, 0, n));
        Assert.False(await accept);
        listener.Stop();
    }

    [Fact]
    public async Task WebSocketConnection_ReceiveLoop_PropagatesFrameHandlerFailure()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receive = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            var ws = new WebSocketConnection(server.GetStream());
            var ok = await ws.AcceptHandshakeAsync(authToken: "", CancellationToken.None);
            Assert.True(ok);
            await ws.ReceiveLoopAsync(_ => throw new InvalidOperationException("handler boom"),
                CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: AAAA\r\nSec-WebSocket-Version: 13\r\n\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[1024];
        var headerSoFar = new List<byte>();
        while (true)
        {
            var n = await s.ReadAsync(respBuf.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (n <= 0) throw new EndOfStreamException("handshake stream closed");
            for (int i = 0; i < n; i++) headerSoFar.Add(respBuf[i]);
            if (HasHeaderTerminator(headerSoFar, out _)) break;
        }

        await s.WriteAsync(BuildClientMaskedTextFrame(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0"}""")));
        await s.FlushAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => receive.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("handler boom", ex.Message);
        listener.Stop();
    }

    // ── BridgePayloads: lockfile schema ─────────────────────────────────────────

    [Fact]
    public void BridgePayloads_BuildLockfileBytes_EmitsExactFieldsClaudeReads()
    {
        // claude --ide discovers the IDE by reading ~/.claude/ide/<port>.lock
        // — every one of these field names is part of that contract. A rename
        // (workspaceFolders → workspace_folders, authToken → auth_token,
        // transport → transport_kind, …) silently breaks discovery, so the
        // test pins every field a fresh claude release would look for.
        var bytes = BridgePayloads.BuildLockfileBytes(
            pid: 12345,
            workingDirectory: "/work/repo",
            authToken: "deadbeefcafe1234",
            port: 41999);

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(12345, root.GetProperty("pid").GetInt32());
        Assert.Equal("CodeyBox", root.GetProperty("ideName").GetString());
        Assert.Equal("ws", root.GetProperty("transport").GetString());
        Assert.False(root.GetProperty("runningInWindows").GetBoolean());
        Assert.Equal("deadbeefcafe1234", root.GetProperty("authToken").GetString());
        Assert.Equal("ws://127.0.0.1:41999", root.GetProperty("url").GetString());

        var folders = root.GetProperty("workspaceFolders");
        Assert.Equal(JsonValueKind.Array, folders.ValueKind);
        Assert.Equal(1, folders.GetArrayLength());
        Assert.Equal("/work/repo", folders[0].GetString());
    }

    [Fact]
    public void BridgePayloads_BuildLockfileBytes_DoesNotEmitSnakeCaseAliases()
    {
        // Regression: a refactor that "tidies up" by switching property
        // names to snake_case would silently break claude IDE discovery.
        // Assert the snake-case aliases are NOT present so a future drift
        // is caught.
        var bytes = BridgePayloads.BuildLockfileBytes(1, "/w", "t", 1);
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        Assert.False(root.TryGetProperty("workspace_folders", out _));
        Assert.False(root.TryGetProperty("ide_name", out _));
        Assert.False(root.TryGetProperty("auth_token", out _));
        Assert.False(root.TryGetProperty("running_in_windows", out _));
    }

    // ── BridgePayloads: ReplyPermission / ReplyInput JSON-RPC shape ─────────────

    [Fact]
    public void BridgePayloads_BuildPermissionReplyJson_HasInnerOutcomeWrapper()
    {
        // The protocol shape is {result:{outcome:{outcome:"selected",
        // optionId:"allow_once"}}} — two nested outcome objects. A regression
        // that flattened it (or dropped the wrapper) would make claude reject
        // the reply silently and no headless turn would make progress.
        var reply = BridgePayloads.BuildPermissionReplyJson("\"req-1\"");
        using var doc = JsonDocument.Parse(reply);
        var root = doc.RootElement;

        Assert.Equal("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.Equal("req-1", root.GetProperty("id").GetString());
        var outer = root.GetProperty("result").GetProperty("outcome");
        Assert.Equal("selected", outer.GetProperty("outcome").GetString());
        Assert.Equal("allow_once", outer.GetProperty("optionId").GetString());
    }

    [Fact]
    public void BridgePayloads_BuildPermissionReplyJson_EchoesNumericIdVerbatim()
    {
        // JSON-RPC allows the id to be a number, string, or null. The bridge
        // must echo the inbound id verbatim — coercing 42 → "42" would
        // mismatch the JSON-RPC correlation id and confuse claude.
        var reply = BridgePayloads.BuildPermissionReplyJson("42");
        using var doc = JsonDocument.Parse(reply);
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(42, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public void BridgePayloads_BuildInputReplyJson_CarriesCodeyboxQuestionSentinel()
    {
        // The literal "<codeybox-question>" prefix is what
        // ClaudeSessionWorker / observers grep for to confirm the auto-answer
        // path actually fired. A wording change here would silently break
        // session-worker test assertions.
        var reply = BridgePayloads.BuildInputReplyJson("\"req-7\"");
        using var doc = JsonDocument.Parse(reply);
        var value = doc.RootElement.GetProperty("result").GetProperty("value").GetString();
        Assert.NotNull(value);
        Assert.StartsWith("<codeybox-question>", value);
        Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("req-7", doc.RootElement.GetProperty("id").GetString());
    }

    // ── BridgePayloads: OnIncomingFrame classification ─────────────────────────

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_AutoApprovesSessionRequestPermission()
    {
        var cfg = BridgeConfig.Default;
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":1,"method":"session/request_permission","params":{}}""",
            cfg, out var id, out var method, out var stop, out var err);

        Assert.Equal(BridgePayloads.FrameKind.AutoPermission, kind);
        Assert.Equal("1", id);
        Assert.Equal("session/request_permission", method);
        Assert.Null(stop);
        Assert.Null(err);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_AutoApprovesPermissionRequestAlias()
    {
        var cfg = BridgeConfig.Default;
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":2,"method":"permission/request"}""",
            cfg, out _, out var method, out _, out _);

        Assert.Equal(BridgePayloads.FrameKind.AutoPermission, kind);
        Assert.Equal("permission/request", method);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_AutoAnswersSessionRequestInput()
    {
        var cfg = BridgeConfig.Default;
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":3,"method":"session/request_input"}""",
            cfg, out var id, out var method, out _, out _);

        Assert.Equal(BridgePayloads.FrameKind.AutoInput, kind);
        Assert.Equal("3", id);
        Assert.Equal("session/request_input", method);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_AutoAnswersInputRequestAlias()
    {
        var cfg = BridgeConfig.Default;
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":4,"method":"input/request"}""",
            cfg, out _, out var method, out _, out _);
        Assert.Equal(BridgePayloads.FrameKind.AutoInput, kind);
        Assert.Equal("input/request", method);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_DoesNotAutoApproveWhenConfigDisabled()
    {
        // If the host disables auto-approve (no defaults), a permission
        // request must NOT be intercepted — it falls through to the host as
        // a normal acp_recv envelope so the host's permission policy applies.
        var cfg = BridgeConfig.FromHello(JsonDocument.Parse("""
            {"type":"hello","autoApprovePermissions":false,"autoAnswerQuestions":false}
            """).RootElement);
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":9,"method":"session/request_permission"}""",
            cfg, out _, out _, out _, out _);
        Assert.Equal(BridgePayloads.FrameKind.Plain, kind);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_DoesNotAutoAnswerWhenConfigDisabled()
    {
        var cfg = BridgeConfig.FromHello(JsonDocument.Parse("""
            {"type":"hello","autoAnswerQuestions":false}
            """).RootElement);
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":10,"method":"session/request_input"}""",
            cfg, out _, out _, out _, out _);
        Assert.Equal(BridgePayloads.FrameKind.Plain, kind);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_DetectsCamelCaseStopReason()
    {
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":5,"result":{"stopReason":"end_turn"}}""",
            BridgeConfig.Default, out _, out _, out var stop, out _);
        Assert.Equal(BridgePayloads.FrameKind.TurnComplete, kind);
        Assert.Equal("end_turn", stop);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_DetectsSnakeCaseStopReason()
    {
        // A subset of claude releases emit snake_case stop_reason. Both
        // variants MUST be recognised — otherwise that release would never
        // be detected as turn-complete and the bridge would hang until its
        // turn-deadline timer fires.
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":6,"result":{"stop_reason":"max_tokens"}}""",
            BridgeConfig.Default, out _, out _, out var stop, out _);
        Assert.Equal(BridgePayloads.FrameKind.TurnComplete, kind);
        Assert.Equal("max_tokens", stop);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_DetectsTurnError()
    {
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","id":7,"error":{"code":-32603,"message":"boom"}}""",
            BridgeConfig.Default, out _, out _, out var stop, out var err);
        Assert.Equal(BridgePayloads.FrameKind.TurnError, kind);
        Assert.Null(stop);
        Assert.NotNull(err);
        using var errDoc = JsonDocument.Parse(err!);
        Assert.Equal(-32603, errDoc.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("boom", errDoc.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_PlainPassesThroughForSessionUpdate()
    {
        // Mid-turn streaming notifications must NOT trigger shutdown / auto
        // reply — they're plain acp_recv envelopes forwarded to the host.
        var kind = BridgePayloads.ClassifyIncomingFrame(
            """{"jsonrpc":"2.0","method":"session/update","params":{"update":{"sessionUpdate":"agent_message_chunk"}}}""",
            BridgeConfig.Default, out _, out var method, out _, out _);
        Assert.Equal(BridgePayloads.FrameKind.Plain, kind);
        Assert.Equal("session/update", method);
    }

    [Fact]
    public void BridgePayloads_ClassifyIncomingFrame_MalformedJsonIsDiscarded()
    {
        var kind = BridgePayloads.ClassifyIncomingFrame(
            "not-json-{",
            BridgeConfig.Default, out _, out _, out _, out _);
        Assert.Equal(BridgePayloads.FrameKind.Malformed, kind);
    }

    // ── Bridge.RunAsync end-to-end ─────────────────────────────────────────────

    [Fact]
    public async Task Bridge_RunAsync_EmitsBridgeStartedReadyAndClaudeExitInOrder()
    {
        // End-to-end fixture pinning the JS-bridge-parity envelope sequence:
        // bridge_started (process boot) → ready (lockfile written, port
        // assigned) → claude_exit (subprocess died) → bridge returns the
        // claude exit code as its own exit code via MaybeFinish/Shutdown(0).
        // Uses /usr/bin/true (or /bin/true) as the stand-in claude binary so
        // the subprocess exits immediately and the lifecycle completes
        // within a few hundred ms.

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-bridge-e2e-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "ide-locks");
            Directory.CreateDirectory(workDir);

            // /usr/bin/true on most distros, fall back to /bin/true.
            var claudeStub = File.Exists("/usr/bin/true") ? "/usr/bin/true" : "/bin/true";

            var hello = """
                {"type":"hello","claudeBinary":"%CLAUDE%","workingDirectory":"%WD%","lockDir":"%LD%","turnTimeoutSeconds":60}
                """.Replace("%CLAUDE%", claudeStub).Replace("%WD%", workDir).Replace("%LD%", lockDir);

            using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(hello + "\n"));
            using var stdoutCapture = new MemoryStream();
            int exitCode;
            using (Emitter.OverrideStreamForTests(stdoutCapture))
            {
                await using var bridge = new Bridge(stdin);
                exitCode = await bridge.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            Assert.Equal(0, exitCode);

            var envelopes = ParseEnvelopes(stdoutCapture.ToArray());
            Assert.Equal("bridge_started", envelopes[0].GetProperty("type").GetString());

            var ready = envelopes.FirstOrDefault(e => e.GetProperty("type").GetString() == "ready");
            Assert.NotEqual(default, ready);
            var port = ready.GetProperty("port").GetInt32();
            Assert.InRange(port, 1, 65535);
            var lockPath = ready.GetProperty("lockPath").GetString();
            Assert.NotNull(lockPath);
            Assert.StartsWith(lockDir, lockPath!);
            Assert.EndsWith(port + ".lock", lockPath);

            var claudeExit = envelopes.FirstOrDefault(e => e.GetProperty("type").GetString() == "claude_exit");
            Assert.NotEqual(default, claudeExit);
            Assert.Equal(0, claudeExit.GetProperty("code").GetInt32());

            // Bridge cleanup must remove the lockfile so a future turn on
            // the same port doesn't see a stale entry.
            Assert.False(File.Exists(lockPath!),
                "Lockfile must be deleted as part of Shutdown — orphaned files accumulate per-turn.");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bridge_RunAsync_WithoutLockDir_WritesDefaultHomeClaudeIdeLockfile()
    {
        if (!File.Exists("/bin/bash"))
            return;

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-default-lockdir-").FullName;
        await EnvironmentVariableGate.WaitAsync();
        var oldHome = Environment.GetEnvironmentVariable("HOME");
        try
        {
            var home = Path.Combine(tmpDir, "home");
            var workDir = Path.Combine(tmpDir, "work");
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(workDir);
            Environment.SetEnvironmentVariable("HOME", home);

            var stubPath = WriteLongRunningClaudeStub(tmpDir);
            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            var defaultLockDir = Path.Combine(home, ".claude", "ide");

            Assert.Equal(defaultLockDir, Path.GetDirectoryName(lockPath));
            Assert.Equal(Path.Combine(defaultLockDir, port + ".lock"), lockPath);
            Assert.True(Directory.Exists(defaultLockDir));
            Assert.True(File.Exists(lockPath),
                "The production hello envelope omits lockDir, so the HOME-based lockfile must exist after ready.");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(defaultLockDir));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(lockPath));

            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");
            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(lockPath),
                "Default-path lockfile must be cleaned up on bridge shutdown.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOME", oldHome);
            EnvironmentVariableGate.Release();
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bridge_RunAsync_FailsFatalOnMissingClaudeBinary()
    {
        // A missing or unrunnable claude binary must surface as a "fatal"
        // envelope with detail, NOT a silent hang. The orchestrator's
        // observer keys off this envelope to degrade the worker to print.
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-bridge-fatal-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "ide-locks");
            Directory.CreateDirectory(workDir);

            var missing = Path.Combine(tmpDir, "definitely-not-claude-binary");

            var hello = """
                {"type":"hello","claudeBinary":"%CLAUDE%","workingDirectory":"%WD%","lockDir":"%LD%"}
                """.Replace("%CLAUDE%", missing).Replace("%WD%", workDir).Replace("%LD%", lockDir);

            using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(hello + "\n"));
            using var stdoutCapture = new MemoryStream();
            int exit;
            using (Emitter.OverrideStreamForTests(stdoutCapture))
            {
                await using var bridge = new Bridge(stdin);
                exit = await bridge.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }

            Assert.Equal(2, exit);
            var envelopes = ParseEnvelopes(stdoutCapture.ToArray());
            var fatal = envelopes.FirstOrDefault(e => e.GetProperty("type").GetString() == "fatal");
            Assert.NotEqual(default, fatal);
            Assert.Equal("claude_spawn_failed", fatal.GetProperty("message").GetString());
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    private static List<JsonElement> ParseEnvelopes(byte[] captured)
    {
        var text = Encoding.UTF8.GetString(captured);
        var list = new List<JsonElement>();
        foreach (var line in text.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var doc = JsonDocument.Parse(line);
                // JsonElement from a disposed JsonDocument is unsafe; clone
                // into a heap JsonElement before disposing.
                list.Add(doc.RootElement.Clone());
                doc.Dispose();
            }
            catch (JsonException)
            {
                // ignore non-JSON lines (shouldn't happen — Emitter only emits JSON)
            }
        }
        return list;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string CaptureStdout(Action action)
    {
        // The Emitter caches Console.OpenStandardOutput() once at type-load,
        // so Console.SetOut won't intercept it. The Emitter exposes a
        // dedicated test seam that swaps the underlying stream temporarily.
        using var ms = new MemoryStream();
        using (Emitter.OverrideStreamForTests(ms))
        {
            action();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool HasHeaderTerminator(List<byte> buf, out int afterTerminatorIndex)
    {
        for (int i = 3; i < buf.Count; i++)
        {
            if (buf[i - 3] == (byte)'\r' && buf[i - 2] == (byte)'\n'
                && buf[i - 1] == (byte)'\r' && buf[i] == (byte)'\n')
            {
                afterTerminatorIndex = i + 1;
                return true;
            }
        }
        afterTerminatorIndex = -1;
        return false;
    }

    private static async Task<string> ReadTextFrameFromUnmasked(NetworkStream s, byte[] alreadyRead)
    {
        var acc = new List<byte>(alreadyRead);
        // Pull until we have enough for header + length + payload.
        while (true)
        {
            if (acc.Count >= 2)
            {
                long len = acc[1] & 0x7f;
                int offset = 2;
                if (len == 126)
                {
                    if (acc.Count < 4) { await ExtendAsync(s, acc); continue; }
                    len = (acc[2] << 8) | acc[3];
                    offset = 4;
                }
                else if (len == 127)
                {
                    if (acc.Count < 10) { await ExtendAsync(s, acc); continue; }
                    len = 0;
                    for (int i = 0; i < 8; i++) len = (len << 8) | acc[2 + i];
                    offset = 10;
                }
                if (acc.Count >= offset + len)
                {
                    return Encoding.UTF8.GetString(acc.ToArray(), offset, (int)len);
                }
            }
            await ExtendAsync(s, acc);
        }
    }

    private static async Task ExtendAsync(NetworkStream s, List<byte> acc)
    {
        var buf = new byte[8192];
        var n = await s.ReadAsync(buf.AsMemory());
        if (n <= 0) throw new EndOfStreamException();
        for (int i = 0; i < n; i++) acc.Add(buf[i]);
    }

    private static byte[] BuildClientMaskedTextFrame(byte[] payload)
    {
        var mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        var masked = new byte[payload.Length];
        for (int i = 0; i < payload.Length; i++) masked[i] = (byte)(payload[i] ^ mask[i % 4]);

        byte[] header;
        if (payload.Length < 126)
        {
            header = new byte[2 + 4];
            header[0] = 0x81;
            header[1] = (byte)(0x80 | payload.Length);
        }
        else if (payload.Length < 65536)
        {
            header = new byte[4 + 4];
            header[0] = 0x81;
            header[1] = (byte)(0x80 | 126);
            header[2] = (byte)((payload.Length >> 8) & 0xff);
            header[3] = (byte)(payload.Length & 0xff);
        }
        else
        {
            header = new byte[10 + 4];
            header[0] = 0x81;
            header[1] = (byte)(0x80 | 127);
            long len = payload.Length;
            for (int i = 0; i < 8; i++) header[2 + i] = (byte)((len >> ((7 - i) * 8)) & 0xff);
        }
        Buffer.BlockCopy(mask, 0, header, header.Length - 4, 4);

        var frame = new byte[header.Length + masked.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(masked, 0, frame, header.Length, masked.Length);
        return frame;
    }

    private static byte[] BuildClientMaskedCloseFrame()
    {
        var mask = new byte[] { 0x12, 0x34, 0x56, 0x78 };
        return new byte[] { 0x88, 0x80, mask[0], mask[1], mask[2], mask[3] };
    }

    // ── Bridge.OnIncomingFrame orchestration ────────────────────────────────────
    //
    // The fixtures above pin BridgePayloads.ClassifyIncomingFrame's classifier
    // table cell-by-cell, but the SIDE-EFFECT routing inside
    // Bridge.OnIncomingFrame (which is what makes a classification actually
    // change wire state — sending the auto-reply, emitting the per-kind
    // envelope, calling Shutdown(0)) was previously unexercised. A regression
    // that dropped the SendPeerText branch from AutoPermission, swapped the
    // Shutdown(0) ordering, or skipped the acp_recv passthrough for Plain
    // would pass every classifier-only fixture. The two end-to-end fixtures
    // below drive each branch through a REAL WebSocket peer and assert both
    // (a) the peer-side reply bytes and (b) the bridge-side stdout envelopes.

    [Fact]
    public async Task Bridge_OnIncomingFrame_DrivesPlainPermissionInputAndTurnCompleteOverRealWebSocket()
    {
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-onincoming-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(lockPath));

            // The auth token is generated inside HandleHello via
            // RandomNumberGenerator and never echoed on stdout; we recover it
            // by reading the lockfile claude --ide would read.
            var lockfile = JsonDocument.Parse(File.ReadAllBytes(lockPath)).RootElement;
            var authToken = lockfile.GetProperty("authToken").GetString()!;

            await ctx.ConnectWebSocketAsync(port, authToken);
            await ctx.WaitForEnvelopeAsync("peer_connected");
            var seenSoFar = ctx.Stdout.SnapshotCount();

            // 1. Plain frame (session/update notification) → acp_recv passthrough.
            const string plain =
                "{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{\"update\":{\"sessionUpdate\":\"agent_message_chunk\"}}}";
            await ctx.SendWebSocketFrameAsync(plain);
            var recv = await ctx.WaitForEnvelopeAsync("acp_recv", startIndex: seenSoFar);
            Assert.Equal("session/update",
                recv.GetProperty("payload").GetProperty("method").GetString());
            seenSoFar = ctx.Stdout.SnapshotCount();

            // 2. session/request_permission → auto-grant: WS reply + envelope.
            const string permReq =
                "{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"session/request_permission\",\"params\":{}}";
            await ctx.SendWebSocketFrameAsync(permReq);
            var permReplyJson = await ctx.ReadWebSocketFrameAsync(TimeSpan.FromSeconds(10));
            using (var pd = JsonDocument.Parse(permReplyJson))
            {
                Assert.Equal("2.0", pd.RootElement.GetProperty("jsonrpc").GetString());
                Assert.Equal(42, pd.RootElement.GetProperty("id").GetInt32());
                var outer = pd.RootElement.GetProperty("result").GetProperty("outcome");
                Assert.Equal("selected", outer.GetProperty("outcome").GetString());
                Assert.Equal("allow_once", outer.GetProperty("optionId").GetString());
            }
            var perm = await ctx.WaitForEnvelopeAsync("permission_auto_granted", startIndex: seenSoFar);
            Assert.Equal("session/request_permission", perm.GetProperty("method").GetString());
            seenSoFar = ctx.Stdout.SnapshotCount();

            // 3. session/request_input → auto-answer: WS reply + envelope.
            const string inputReq =
                "{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"session/request_input\",\"params\":{}}";
            await ctx.SendWebSocketFrameAsync(inputReq);
            var inputReplyJson = await ctx.ReadWebSocketFrameAsync(TimeSpan.FromSeconds(10));
            using (var id = JsonDocument.Parse(inputReplyJson))
            {
                Assert.Equal(43, id.RootElement.GetProperty("id").GetInt32());
                Assert.StartsWith("<codeybox-question>",
                    id.RootElement.GetProperty("result").GetProperty("value").GetString());
            }
            var qa = await ctx.WaitForEnvelopeAsync("question_auto_answered", startIndex: seenSoFar);
            Assert.Equal("session/request_input", qa.GetProperty("method").GetString());
            seenSoFar = ctx.Stdout.SnapshotCount();

            // 4. result.stopReason → turn_complete envelope + bridge shuts down.
            const string done =
                "{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{\"stopReason\":\"end_turn\"}}";
            var terminalStart = seenSoFar;
            await ctx.SendWebSocketFrameAsync(done);
            var doneRecv = await ctx.WaitForEnvelopeAsync("acp_recv", startIndex: terminalStart);
            Assert.Equal(99, doneRecv.GetProperty("payload").GetProperty("id").GetInt32());
            Assert.Equal("end_turn",
                doneRecv.GetProperty("payload").GetProperty("result").GetProperty("stopReason").GetString());
            var doneEnv = await ctx.WaitForEnvelopeAsync("turn_complete", startIndex: terminalStart);
            Assert.Equal("end_turn", doneEnv.GetProperty("stopReason").GetString());

            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(lockPath),
                "Lockfile must be deleted as part of the turn_complete Shutdown.");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bridge_RejectsAuthenticatedWebSocketPeerOutsideClaudeProcessTree()
    {
        if (!File.Exists("/bin/bash"))
            return;

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-peer-auth-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            await using var ctx = new BridgeRunHandle(useProductionPeerAuthorizer: true);
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            var authToken = JsonDocument.Parse(File.ReadAllBytes(lockPath)).RootElement
                .GetProperty("authToken").GetString()!;

            // This test process knows the lockfile token, but it is not the
            // spawned claude --ide process or one of its descendants. The
            // bridge must reject it after the WebSocket auth handshake and
            // must not let it become the active ACP peer.
            await ctx.ConnectWebSocketAsync(port, authToken);
            var rejected = await ctx.WaitForEnvelopeAsync("peer_rejected", TimeSpan.FromSeconds(10));
            Assert.Equal("untrusted_process", rejected.GetProperty("reason").GetString());
            Assert.Equal(0, ctx.Stdout.CountByType("peer_connected"));

            await Task.Delay(100);
            Assert.Equal(0, ctx.Stdout.CountByType("turn_complete"));

            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");
            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bridge_AcceptsAuthenticatedWebSocketPeerFromClaudeDescendantProcess()
    {
        if (!File.Exists("/bin/bash") || !CommandExists("python3"))
            return;

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-peer-auth-positive-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            Directory.CreateDirectory(lockDir);
            var markerPath = Path.Combine(tmpDir, "descendant-connected.marker");
            var stubPath = WriteClaudeStubThatConnectsFromDescendant(tmpDir);

            await using var ctx = new BridgeRunHandle(useProductionPeerAuthorizer: true);
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"" + lockDir + "\",\"" + markerPath
                + "\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            await ctx.WaitForEnvelopeAsync("ready");
            await ctx.WaitForEnvelopeAsync("peer_connected", TimeSpan.FromSeconds(15));

            for (int i = 0; i < 50 && !File.Exists(markerPath); i++)
                await Task.Delay(50);
            Assert.True(File.Exists(markerPath),
                "The descendant claude stub did not complete the authenticated WebSocket handshake.");
            Assert.Equal(0, ctx.Stdout.CountByType("peer_rejected"));

            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");
            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bridge_RejectsSecondAuthenticatedWebSocketPeerWhileOnePeerIsActive()
    {
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-active-peer-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            var authToken = JsonDocument.Parse(File.ReadAllBytes(lockPath)).RootElement
                .GetProperty("authToken").GetString()!;

            await ctx.ConnectWebSocketAsync(port, authToken);
            await ctx.WaitForEnvelopeAsync("peer_connected");
            var startIndex = ctx.Stdout.SnapshotCount();

            using var secondPeer = await ConnectAuthenticatedWebSocketClientAsync(port, authToken);
            var rejected = await ctx.WaitForEnvelopeAsync("peer_rejected", startIndex: startIndex);
            Assert.Equal("active_peer_exists", rejected.GetProperty("reason").GetString());
            Assert.Equal(1, ctx.Stdout.CountByType("peer_connected"));

            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");
            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bridge_OnIncomingFrame_AutoAnswerDisabled_PassesInputRequestThroughWithoutReply()
    {
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-noautoanswer-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"autoAnswerQuestions\":false,\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            var authToken = JsonDocument.Parse(File.ReadAllBytes(lockPath)).RootElement
                .GetProperty("authToken").GetString()!;

            await ctx.ConnectWebSocketAsync(port, authToken);
            await ctx.WaitForEnvelopeAsync("peer_connected");
            var seenSoFar = ctx.Stdout.SnapshotCount();

            const string inputReq =
                "{\"jsonrpc\":\"2.0\",\"id\":44,\"method\":\"session/request_input\",\"params\":{}}";
            await ctx.SendWebSocketFrameAsync(inputReq);

            var recv = await ctx.WaitForEnvelopeAsync("acp_recv", startIndex: seenSoFar);
            Assert.Equal("session/request_input",
                recv.GetProperty("payload").GetProperty("method").GetString());
            Assert.Equal(0, ctx.Stdout.CountByType("question_auto_answered"));
            await Assert.ThrowsAsync<TimeoutException>(
                () => ctx.ReadWebSocketFrameAsync(TimeSpan.FromMilliseconds(250)));

            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");
            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bridge_OnIncomingFrame_TurnError_EmitsEnvelopeAndShutsDownCleanly()
    {
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-onerror-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            var lockfile = JsonDocument.Parse(File.ReadAllBytes(lockPath)).RootElement;
            var authToken = lockfile.GetProperty("authToken").GetString()!;

            await ctx.ConnectWebSocketAsync(port, authToken);
            await ctx.WaitForEnvelopeAsync("peer_connected");
            var seenSoFar = ctx.Stdout.SnapshotCount();

            // error member present → turn_error envelope (with the raw error
            // subtree echoed) + Shutdown(0). The acp_recv passthrough also
            // fires from the TurnError branch.
            const string err =
                "{\"jsonrpc\":\"2.0\",\"id\":7,\"error\":{\"code\":-32603,\"message\":\"boom\"}}";
            await ctx.SendWebSocketFrameAsync(err);

            var recv = await ctx.WaitForEnvelopeAsync("acp_recv", startIndex: seenSoFar);
            Assert.Equal(7, recv.GetProperty("payload").GetProperty("id").GetInt32());
            Assert.Equal("boom",
                recv.GetProperty("payload").GetProperty("error").GetProperty("message").GetString());
            var turnErr = await ctx.WaitForEnvelopeAsync("turn_error", startIndex: seenSoFar);
            var errProp = turnErr.GetProperty("error");
            Assert.Equal(-32603, errProp.GetProperty("code").GetInt32());
            Assert.Equal("boom", errProp.GetProperty("message").GetString());

            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Bridge.SpawnClaude stdin redirection (regression for commit 497fdbe) ────
    //
    // The bug being pinned: when RedirectStandardInput was false, claude --ide
    // inherited the bridge's host envelope pipe (fd 0 carrying hello/acp_send
    // bytes) and raced the bridge's ReadStdinAsync reader for those bytes. The
    // fix sets RedirectStandardInput = true AND immediately closes
    // StandardInput so claude sees EOF on a private pipe. This test boots the
    // bridge with a bash stub that records what its fd 0 actually looks like —
    // a regression (a) (RedirectStandardInput back to false) makes the stub
    // inherit the test process's fd 0 (asserted via inode equality), and a
    // regression (b) (forgetting StandardInput.Close()) leaves the bridge-end
    // of the pipe open so the stub's read times out instead of EOF'ing.

    [Fact]
    public async Task Bridge_SpawnClaude_AppliesWorkingDirectoryEnvironmentAndClosedStdin()
    {
        // GNU coreutils' stat / readlink + bash are baseline on every
        // CodeyBox sandbox image, so this fixture is safe in CI.
        if (!File.Exists("/bin/bash"))
            return; // honour Skippable shape without taking the dependency

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-stdineof-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);

            // Stub claude that records its fd 0 link, fd 0 inode, and the
            // outcome of an immediate read with a 1-second timeout:
            //   rc=0  → bytes available (regression: claude inherited the
            //           bridge's host envelope pipe).
            //   rc=1  → EOF (the fix: bridge closed the pipe write-end).
            //   rc>128 → timeout (regression: bridge forgot to close the
            //           write-end, claude is blocked on an open empty pipe).
            //
            // The bridge prepends "--ide" before any configured claudeArgs,
            // so argv[1]="--ide", argv[2]=stub log path.
            var stubPath = Path.Combine(tmpDir, "claude-stub.sh");
            var stubLog = Path.Combine(tmpDir, "stub.log");
            File.WriteAllText(stubPath,
                "#!/bin/bash\n" +
                "set +e\n" +
                "LOG=\"$2\"\n" +
                "printf 'pwd=%s\\nenv_marker=%s\\napi_timeout=%s\\n' " +
                "\"$PWD\" \"${CODEYBOX_TEST_ENV:-}\" \"${API_TIMEOUT_MS:-}\" > \"$LOG\"\n" +
                "fd0_link=$(readlink /proc/self/fd/0 2>/dev/null || echo unknown)\n" +
                "fd0_inode=$(stat -L -c %i /proc/self/fd/0 2>/dev/null || echo unknown)\n" +
                "fd0_type=$(stat -L -c %F /proc/self/fd/0 2>/dev/null || echo unknown)\n" +
                "byte=\"\"\n" +
                "IFS= read -t 1 -r -N 16 byte\n" +
                "rc=$?\n" +
                "state=other\n" +
                "if [ \"$rc\" = \"0\" ]; then state=read\n" +
                "elif [ \"$rc\" = \"1\" ]; then state=eof\n" +
                "else state=timeout\n" +
                "fi\n" +
                "printf 'fd0_link=%s\\nfd0_inode=%s\\nfd0_type=%s\\nread_state=%s\\nrc=%s\\nbytes_read=%s\\n' " +
                "\"$fd0_link\" \"$fd0_inode\" \"$fd0_type\" \"$state\" \"$rc\" \"$byte\" >> \"$LOG\"\n" +
                "exit 0\n");
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Capture THIS test process's fd 0 inode. A non-redirected
            // subprocess inherits the parent's fd 0, so the inode it reports
            // is the test's stdin inode. Bridge's claude subprocess MUST get
            // a fresh pipe with a DIFFERENT inode if RedirectStandardInput
            // is true.
            var testFd0Inode = GetCurrentProcessStdinInodeViaSubprocess();

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"" + stubLog
                + "\"],\"claudeEnv\":{\"CODEYBOX_TEST_ENV\":\"env-from-bridge\",\"API_TIMEOUT_MS\":\"12345\"},\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":30}";
            await ctx.WriteStdinLineAsync(hello);

            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0, exitCode);

            Assert.True(File.Exists(stubLog),
                "Stub claude did not write its diagnostic log — Bridge.SpawnClaude probably never invoked it.");
            var kv = ParseKeyValueLog(stubLog);

            Assert.Equal(workDir, kv["pwd"]);
            Assert.Equal("env-from-bridge", kv["env_marker"]);
            Assert.Equal("12345", kv["api_timeout"]);

            // Regression (b) — bridge created the pipe but forgot to close
            // the write-end. claude's read would BLOCK on an open empty pipe
            // and the stub's 1-second timeout would fire.
            Assert.Equal("eof", kv["read_state"]);
            Assert.Equal("1", kv["rc"]);

            // Regression (a) — bridge didn't redirect at all. claude would
            // inherit the test process's fd 0; fd0_link could be /dev/null,
            // a tty, or the test runner's pipe — none of which start with
            // "pipe:" if the test was launched without an explicit stdin pipe.
            // This assertion catches some forms of regression (a).
            Assert.StartsWith("pipe:", kv["fd0_link"]);

            // Deterministic regression (a) catcher: a fresh pipe created by
            // .NET's Process.Start for the redirected stdin has a NEW inode,
            // distinct from the test process's fd 0 inode. If the bridge
            // failed to redirect, claude would inherit the test's fd 0 and
            // the inodes would be EQUAL.
            Assert.True(int.TryParse(kv["fd0_inode"], out var stubInode),
                "Stub did not report a parseable fd 0 inode.");
            Assert.NotEqual(testFd0Inode, stubInode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Bridge.DrainPending order contract (regression for commit 8152b99) ──────
    //
    // The bug being pinned: when the lock was narrowed to dequeue-only, two
    // drainers (the stdin pump after acp_send, and the accept handler after
    // the peer attaches) could overlap their SendText calls and deliver ACP
    // frames out of enqueue order — e.g. session/new ahead of initialize.
    // The fix permits only one active drainer at a time while doing the actual
    // NetworkStream.Write outside _pendingLock so shutdown can still close a
    // stalled peer.
    //
    // This fixture pre-queues many ACP frames before the peer connects, then
    // feeds more from a background task while the peer-connect drain is in
    // flight. Both drainers contend for the same lock; the test asserts that
    // every frame arrives at the WebSocket peer in strict enqueue order. A
    // regression that narrows the lock back to dequeue-only would interleave
    // the SendText calls under contention — the assertion catches the
    // resulting out-of-order frames.

    [Fact]
    public async Task Bridge_DrainPending_PreservesEnqueueOrderUnderConcurrentDrainers()
    {
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-drainorder-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            const int PreConnectFrames = 100;
            const int PostConnectFrames = 100;
            const int TotalFrames = PreConnectFrames + PostConnectFrames;

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            var lockfile = JsonDocument.Parse(File.ReadAllBytes(lockPath)).RootElement;
            var authToken = lockfile.GetProperty("authToken").GetString()!;

            // Phase A: pre-queue PreConnectFrames acp_send envelopes BEFORE
            // the peer connects. DrainPending is a no-op while _peerReady is
            // false, so these accumulate in _pendingPayloads.
            for (int i = 0; i < PreConnectFrames; i++)
            {
                await ctx.WriteStdinLineAsync(BuildSeqAcpSendEnvelope(i));
            }

            // Phase B: connect the peer. HandleClientAsync flips _peerReady=true
            // and calls DrainPending, which begins sending the PreConnectFrames
            // queued frames.
            await ctx.ConnectWebSocketAsync(port, authToken);

            // Phase C: feed PostConnectFrames MORE envelopes from a background
            // task that races the peer-connect drain. The stdin pump processes
            // them one at a time, and each enqueue triggers another DrainPending
            // call from the stdin pump thread — concurrent with the accept
            // handler's drain. With a single-drainer guard the drainers serialise
            // and frames stay in enqueue order; a narrowed lock without that guard
            // would let SendText calls overlap and reorder under load.
            var feedTask = Task.Run(async () =>
            {
                for (int i = PreConnectFrames; i < TotalFrames; i++)
                {
                    await ctx.WriteStdinLineAsync(BuildSeqAcpSendEnvelope(i));
                }
            });

            // Read all TotalFrames from the WS peer and verify strict ordering.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var received = new List<int>(TotalFrames);
            for (int i = 0; i < TotalFrames; i++)
            {
                var frame = await ctx.ReadWebSocketFrameAsync(cts.Token);
                using var doc = JsonDocument.Parse(frame);
                var seq = doc.RootElement.GetProperty("params").GetProperty("seq").GetInt32();
                received.Add(seq);
            }
            await feedTask;

            Assert.Equal(TotalFrames, received.Count);
            for (int i = 0; i < TotalFrames; i++)
            {
                Assert.Equal(i, received[i]);
            }

            // Shut down the bridge cleanly.
            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");
            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Bridge.Shutdown idempotence (regression for commit 8152b99) ─────────────
    //
    // The bug being pinned: a non-atomic shutdown flag could let multiple
    // Shutdown callers (turn-deadline timer, posix signal, claude exited,
    // peer closed, OnIncomingFrame stopReason / error) all run the cleanup
    // body — emitting duplicate claude_exit envelopes, double-deleting the
    // lockfile, and clobbering the first cause's exit code. The fix uses
    // Interlocked.Exchange(ref _shutdownState, 1) so the first caller wins
    // and all others are no-ops.
    //
    // The first fixture below is a direct sequential reflection test of the
    // exit-code-stickiness contract that the Interlocked.Exchange guards.
    // The second fixture drives two real Shutdown causes (claude exiting +
    // TurnComplete frame arriving) through a live bridge and asserts the
    // observable wire contract: claude_exit emitted exactly once, lockfile
    // deleted, no fatal envelope leak.

    [Fact]
    public void Bridge_Shutdown_IsIdempotent_FirstExitCodeWinsAcrossRepeatCalls()
    {
        // Sequential test that pins the Interlocked.Exchange short-circuit.
        // A regression that swaps Interlocked.Exchange for a plain
        // `if (_shutdownState != 0) return; _shutdownState = 1;` would still
        // pass this sequential fixture (both forms reject the second call),
        // but a regression that drops the early-return entirely OR moves the
        // `_exitCode = code` assignment ABOVE the guard would let later calls
        // overwrite the first cause's exit code — and that IS caught here.
        var bridge = new Bridge(new MemoryStream());
        var shutdownMethod = typeof(Bridge).GetMethod("Shutdown",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var exitCodeField = typeof(Bridge).GetField("_exitCode",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var shutdownStateField = typeof(Bridge).GetField("_shutdownState",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        // First call wins, both the exit code and the shutdown-state flag are
        // observed flipped.
        shutdownMethod.Invoke(bridge, new object[] { 0 });
        Assert.Equal(0, (int)exitCodeField.GetValue(bridge)!);
        Assert.Equal(1, (int)shutdownStateField.GetValue(bridge)!);

        // Subsequent calls must NOT overwrite _exitCode — they're no-ops.
        shutdownMethod.Invoke(bridge, new object[] { 99 });
        Assert.Equal(0, (int)exitCodeField.GetValue(bridge)!);
        Assert.Equal(1, (int)shutdownStateField.GetValue(bridge)!);

        shutdownMethod.Invoke(bridge, new object[] { 42 });
        Assert.Equal(0, (int)exitCodeField.GetValue(bridge)!);
    }

    [Fact]
    public async Task Bridge_SignalForceExitWatchdog_UsesInjectedExitAction()
    {
        var forcedExit = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new Bridge(new MemoryStream(), listenerFactory: null,
            forceExitForTests: code => forcedExit.TrySetResult(code));

        var exitCodeField = typeof(Bridge).GetField("_exitCode",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        exitCodeField.SetValue(bridge, 17);

        var schedule = typeof(Bridge).GetMethod("ScheduleForceExitAfterSignal",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        schedule.Invoke(bridge, null);

        Assert.Equal(17, await forcedExit.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Bridge_Shutdown_ConcurrentCauses_ClaudeExitEmittedExactlyOnceAndLockfileGone()
    {
        // End-to-end fixture exercising two Shutdown causes back-to-back:
        // a short-lived claude (0.5s sleep) AND a TurnComplete frame the test
        // sends as soon as the peer connects. Whichever cause fires first
        // wins and runs cleanup; the other observes ShutdownStarted and
        // becomes a no-op (Interlocked.Exchange returns the prior 1). The
        // observable wire contract: claude_exit emitted exactly once (the
        // Process.Exited event runs once per process instance), lockfile
        // deleted, bridge exits with code 0, no fatal envelope.
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-shutdown-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"0.5\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":30}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            var lockfile = JsonDocument.Parse(File.ReadAllBytes(lockPath)).RootElement;
            var authToken = lockfile.GetProperty("authToken").GetString()!;

            await ctx.ConnectWebSocketAsync(port, authToken);
            await ctx.WaitForEnvelopeAsync("peer_connected");

            // Fire the TurnComplete frame immediately; the sleep stub will
            // exit ~500 ms later (Exited handler fires → MaybeFinish →
            // Shutdown). The two Shutdown triggers race; the idempotence
            // contract is what keeps cleanup single-shot.
            const string turnComplete =
                "{\"jsonrpc\":\"2.0\",\"id\":99,\"result\":{\"stopReason\":\"end_turn\"}}";
            await ctx.SendWebSocketFrameAsync(turnComplete);

            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);

            // claude_exit may be emitted from the Process.Exited handler
            // AFTER RunAsync returns (the event fires on .NET's monitor
            // thread and is not awaited by Shutdown). Wait for it before
            // counting, so we don't race with late delivery.
            await ctx.WaitForEnvelopeAsync("claude_exit", TimeSpan.FromSeconds(10));

            Assert.Equal(1, ctx.Stdout.CountByType("claude_exit"));
            Assert.Equal(1, ctx.Stdout.CountByType("turn_complete"));
            Assert.Equal(0, ctx.Stdout.CountByType("fatal"));
            Assert.False(File.Exists(lockPath));
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Bridge.EmitAcpSentMeta envelope contract ────────────────────────────────
    //
    // DrainPending emits a paired acp_sent envelope on stdout immediately after
    // every SendText so a host-side observer can correlate outbound JSON-RPC
    // frames by id/method. Plausible regressions the existing drain-order test
    // does NOT catch: dropping the EmitAcpSentMeta call entirely, swapping
    // id ↔ method, coercing a numeric id to a string (would happen if a
    // refactor replaced WriteRawValue(idJson) with WriteString), or emitting
    // an empty body for envelopes whose payload doesn't carry both fields.

    [Fact]
    public async Task Bridge_EmitAcpSentMeta_EnvelopePreservesIdTypeAndMethodPerSendText()
    {
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-sent-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":60}";
            await ctx.WriteStdinLineAsync(hello);

            var ready = await ctx.WaitForEnvelopeAsync("ready");
            var port = ready.GetProperty("port").GetInt32();
            var lockPath = ready.GetProperty("lockPath").GetString()!;
            var authToken = JsonDocument.Parse(File.ReadAllBytes(lockPath)).RootElement
                .GetProperty("authToken").GetString()!;

            // Enqueue THREE envelopes before the peer connects so DrainPending
            // runs all three back-to-back on the accept handler's drain pass:
            //   - numeric id (must remain a JSON number, not a string)
            //   - string id (must remain a JSON string)
            //   - method-only (no id field at all — envelope must still emit)
            await ctx.WriteStdinLineAsync(
                "{\"type\":\"acp_send\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"initialize\",\"params\":{}}}");
            await ctx.WriteStdinLineAsync(
                "{\"type\":\"acp_send\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":\"req-abc\",\"method\":\"session/new\",\"params\":{}}}");
            await ctx.WriteStdinLineAsync(
                "{\"type\":\"acp_send\",\"payload\":{\"jsonrpc\":\"2.0\",\"method\":\"session/update\",\"params\":{}}}");

            var beforeConnect = ctx.Stdout.SnapshotCount();
            await ctx.ConnectWebSocketAsync(port, authToken);
            await ctx.WaitForEnvelopeAsync("peer_connected");

            // Drain the three WS frames so we know all three SendText calls
            // ran (and therefore all three EmitAcpSentMeta calls have queued).
            for (int i = 0; i < 3; i++)
                _ = await ctx.ReadWebSocketFrameAsync(TimeSpan.FromSeconds(10));

            // Wait for the three acp_sent envelopes to surface on stdout. The
            // peer can read a frame before the paired stdout envelope has been
            // flushed, so don't snapshot immediately after the first envelope.
            List<JsonElement> sentEnvs;
            var deadline = Environment.TickCount64 + (long)TimeSpan.FromSeconds(10).TotalMilliseconds;
            while (true)
            {
                var snap = ctx.Stdout.Snapshot();
                sentEnvs = snap.Skip(beforeConnect)
                    .Where(e => e.TryGetProperty("type", out var t)
                        && t.ValueKind == JsonValueKind.String
                        && t.GetString() == "acp_sent")
                    .ToList();
                if (sentEnvs.Count >= 3)
                    break;
                if (Environment.TickCount64 >= deadline)
                    break;
                await Task.Delay(50);
            }
            Assert.Equal(3, sentEnvs.Count);

            // Envelope 0: numeric id preserved as Number (not coerced to String).
            Assert.Equal(JsonValueKind.Number, sentEnvs[0].GetProperty("id").ValueKind);
            Assert.Equal(42, sentEnvs[0].GetProperty("id").GetInt32());
            Assert.Equal("initialize", sentEnvs[0].GetProperty("method").GetString());

            // Envelope 1: string id preserved as String.
            Assert.Equal(JsonValueKind.String, sentEnvs[1].GetProperty("id").ValueKind);
            Assert.Equal("req-abc", sentEnvs[1].GetProperty("id").GetString());
            Assert.Equal("session/new", sentEnvs[1].GetProperty("method").GetString());

            // Envelope 2: id absent (notification shape), method present.
            Assert.False(sentEnvs[2].TryGetProperty("id", out _),
                "acp_sent envelope must not synthesise an id when the payload has none.");
            Assert.Equal("session/update", sentEnvs[2].GetProperty("method").GetString());

            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");
            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Bridge.HandleHello turn-deadline timer contract ────────────────────────
    //
    // The timer arms with `_config.TurnTimeoutSeconds * 1000` ms; on fire it
    // emits a {"type":"turn_timeout"} envelope and calls Shutdown(0). Plausible
    // regressions: dropping the `* 1000` so the seconds value is used as ms
    // (timer would fire near-instantly), forgetting Shutdown(0) so the bridge
    // keeps running after the envelope, passing Timeout.Infinite as the due
    // time (timer never fires), or disposing the timer in HandleHello before
    // it ever fires. We force the timer to fire immediately via Timer.Change
    // (testing the callback contract — emit + shutdown) and separately assert
    // the envelope did NOT fire while the bridge was idle (catches the missing
    // `* 1000` scaling — a 10s timeout used as 10ms would fire before our
    // forced trigger).

    [Fact]
    public async Task Bridge_TurnDeadlineTimer_OnFire_EmitsEnvelopeAndShutsDownWithCodeZero()
    {
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-deadline-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            var stubPath = WriteLongRunningClaudeStub(tmpDir);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"30\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                // turnTimeoutSeconds floors to 10 — that's the smallest legal
                // configuration. The forced-fire below proves the callback
                // works without waiting for the wall clock; the pre-fire
                // count assertion proves the timer ISN'T accidentally firing
                // before that (catches the missing `* 1000` regression).
                + "\",\"turnTimeoutSeconds\":10}";
            await ctx.WriteStdinLineAsync(hello);

            await ctx.WaitForEnvelopeAsync("ready");
            // Give the bridge a moment to settle and ensure the timer hasn't
            // misfired. If TurnTimeoutSeconds was used as ms instead of
            // seconds (no `* 1000`), the 10ms timer would have fired by now.
            await Task.Delay(250);
            Assert.Equal(0, ctx.Stdout.CountByType("turn_timeout"));

            // Force the timer to fire immediately. The contract under test
            // is the callback: emit("turn_timeout") + Shutdown(0).
            var timerField = typeof(Bridge).GetField("_turnDeadline",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var timer = timerField.GetValue(ctx.Bridge) as Timer;
            Assert.NotNull(timer);
            timer!.Change(0, Timeout.Infinite);

            var envelope = await ctx.WaitForEnvelopeAsync("turn_timeout",
                TimeSpan.FromSeconds(10));
            Assert.Equal("turn_timeout", envelope.GetProperty("type").GetString());

            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Bridge.WriteLockfile fatal-envelope contracts ──────────────────────────
    //
    // WriteLockfile emits two distinct fatal envelopes the host observer keys
    // off ("lockdir_create_failed" / "lockfile_write_failed"). The observer's
    // ObserveBridgeOutput pattern-matches the literal envelope message string
    // to decide whether to degrade — a rename or shape drift would silently
    // strand the work item. Plausible regressions covered: dropping the
    // Fatal call entirely (silent exit instead of envelope), changing the
    // message string, omitting Shutdown(2) so the bridge keeps running, or
    // moving the lockfile_write_failed catch outside the WriteAllBytes block
    // so an EACCES escapes as an unhandled exception.

    [Fact]
    public async Task Bridge_WriteLockfile_LockdirCreateFailed_EmitsFatalWithMessageAndShutsDown()
    {
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-lockdir-").FullName;
        try
        {
            // Point lockDir at an existing REGULAR FILE so
            // Directory.CreateDirectory throws IOException.
            var fileAsLockDir = Path.Combine(tmpDir, "this-is-actually-a-file");
            File.WriteAllText(fileAsLockDir, "not a dir");

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"/usr/bin/true\",\"workingDirectory\":\""
                + tmpDir + "\",\"lockDir\":\"" + fileAsLockDir
                + "\",\"turnTimeoutSeconds\":30}";
            await ctx.WriteStdinLineAsync(hello);

            var fatal = await ctx.WaitForEnvelopeAsync("fatal", TimeSpan.FromSeconds(10));
            Assert.Equal("lockdir_create_failed", fatal.GetProperty("message").GetString());
            Assert.True(fatal.TryGetProperty("detail", out var detail));
            Assert.NotEqual(JsonValueKind.Null, detail.ValueKind);

            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(2, exitCode);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Bridge_WriteLockfile_LockfileWriteFailed_EmitsFatalWithMessageAndShutsDown()
    {
        // Drive WriteLockfile in isolation via reflection. We can't easily
        // trigger File.WriteAllBytes to fail through the live RunAsync path
        // because the bridge unconditionally chmod 0700's the lockDir before
        // writing — so a pre-set 0500 mode is bumped back to writable
        // (intentionally; the JS parent re-mkdir'd the dir on every turn for
        // similar reasons). Instead we pre-create the LOCKFILE PATH itself as
        // a directory, then invoke WriteLockfile with state set so its
        // `_port + ".lock"` collides with that directory. File.WriteAllBytes
        // on a path that exists as a directory throws UnauthorizedAccessException
        // — the WriteLockfile catch routes it to Fatal("lockfile_write_failed").
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-lockwrite-").FullName;
        try
        {
            const int fixedPort = 41999;
            var lockDir = Path.Combine(tmpDir, "lockdir");
            Directory.CreateDirectory(lockDir);
            // Block the lockfile path with a directory entry of the same name.
            Directory.CreateDirectory(Path.Combine(lockDir, fixedPort + ".lock"));

            using var stdoutCapture = new MemoryStream();
            var bridge = new Bridge(new MemoryStream());
            using (Emitter.OverrideStreamForTests(stdoutCapture))
            {
                var configField = typeof(Bridge).GetField("_config",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                var portField = typeof(Bridge).GetField("_port",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                var tokenField = typeof(Bridge).GetField("_authToken",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;

                // Build a BridgeConfig directly via the record's init contract
                // so we can pin LockDir without going through the FromHello
                // floor / parser.
                var cfg = BridgeConfig.Default with
                {
                    LockDir = lockDir,
                    WorkingDirectory = tmpDir,
                };
                configField.SetValue(bridge, cfg);
                portField.SetValue(bridge, fixedPort);
                tokenField.SetValue(bridge, "deadbeefcafe1234");

                var writeLockfile = typeof(Bridge).GetMethod("WriteLockfile",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                writeLockfile.Invoke(bridge, null);
            }

            var envelopes = ParseEnvelopes(stdoutCapture.ToArray());
            var fatal = envelopes.FirstOrDefault(
                e => e.TryGetProperty("type", out var t) && t.GetString() == "fatal");
            Assert.NotEqual(default, fatal);
            Assert.Equal("lockfile_write_failed", fatal.GetProperty("message").GetString());
            Assert.True(fatal.TryGetProperty("detail", out var detail));
            Assert.NotEqual(JsonValueKind.Null, detail.ValueKind);

            // Fatal must Shutdown(2): _exitCode = 2 and _shutdownState = 1.
            var exitCodeField = typeof(Bridge).GetField("_exitCode",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var shutdownStateField = typeof(Bridge).GetField("_shutdownState",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Equal(2, (int)exitCodeField.GetValue(bridge)!);
            Assert.Equal(1, (int)shutdownStateField.GetValue(bridge)!);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Bridge.Fatal startup_failed envelope contract ──────────────────────────
    //
    // HandleHello wraps StartServer/WriteLockfile/SpawnClaude in an outer
    // catch that calls Fatal("startup_failed", ex.Message). The inner methods
    // each have their own envelope-specific catches (lockdir_create_failed,
    // lockfile_write_failed, claude_spawn_failed), so the outer catch is
    // defense-in-depth — it only fires when something unexpected throws
    // (e.g. TcpListener.Start raising SocketException for EADDRINUSE on a
    // sandbox-locked port). The audit's plausible regression is a refactor
    // that drops the outer catch entirely; this fixture pins the envelope
    // shape Fatal produces so a regression that changes the envelope message
    // (and silently breaks the host observer's pattern match) is caught.

    [Fact]
    public async Task Bridge_HandleHello_UnexpectedStartupFailure_EmitsStartupFailedFatal()
    {
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"hello\"}\n"));
        using var stdoutCapture = new MemoryStream();
        int exit;
        using (Emitter.OverrideStreamForTests(stdoutCapture))
        {
            await using var bridge = new Bridge(stdin,
                listenerFactory: () => throw new InvalidOperationException("listener boom"));
            exit = await bridge.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(2, exit);
        var envelopes = ParseEnvelopes(stdoutCapture.ToArray());
        var fatalEnv = envelopes.FirstOrDefault(
            e => e.TryGetProperty("type", out var t) && t.GetString() == "fatal");
        Assert.NotEqual(default, fatalEnv);
        Assert.Equal("startup_failed", fatalEnv.GetProperty("message").GetString());
        Assert.Contains("listener boom", fatalEnv.GetProperty("detail").GetString());
    }

    [Fact]
    public void Bridge_Fatal_StartupFailed_EmitsEnvelopeWithMessageDetailAndShutsDownWithCode2()
    {
        using var stdoutCapture = new MemoryStream();
        var bridge = new Bridge(new MemoryStream());
        using (Emitter.OverrideStreamForTests(stdoutCapture))
        {
            var fatal = typeof(Bridge).GetMethod("Fatal",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            fatal.Invoke(bridge, new object?[]
            {
                "startup_failed",
                "Address already in use on TcpListener.Start",
            });
        }

        var envelopes = ParseEnvelopes(stdoutCapture.ToArray());
        var fatalEnv = envelopes.FirstOrDefault(
            e => e.TryGetProperty("type", out var t) && t.GetString() == "fatal");
        Assert.NotEqual(default, fatalEnv);
        Assert.Equal("startup_failed", fatalEnv.GetProperty("message").GetString());
        Assert.Equal("Address already in use on TcpListener.Start",
            fatalEnv.GetProperty("detail").GetString());

        // Fatal must Shutdown(2): _exitCode is 2 and _shutdownState is 1.
        var exitCodeField = typeof(Bridge).GetField("_exitCode",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var shutdownStateField = typeof(Bridge).GetField("_shutdownState",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(2, (int)exitCodeField.GetValue(bridge)!);
        Assert.Equal(1, (int)shutdownStateField.GetValue(bridge)!);
    }

    // ── Bridge.SpawnClaude stdout/stderr pump envelopes ────────────────────────
    //
    // SpawnClaude pumps claude's StandardOutput/StandardError into
    // {"type":"claude_stdout","text":...} / {"type":"claude_stderr","text":...}
    // envelopes. AcpClaudeTransport.ObserveBridgeOutput consumes the
    // claude_stderr envelope to populate obs.Stderr — a property-name drift
    // ("text" → "data" / "content") would silently break the host's stderr
    // surface. None of the existing Bridge fixtures use a claude stub that
    // actually writes to either stream, so the pump path is entirely
    // unexercised. This fixture uses a bash stub that writes a marker line
    // to each stream then exits, and asserts both envelopes carry the
    // emitted bytes under the documented "text" field name.

    [Fact]
    public async Task Bridge_SpawnClaude_PumpsClaudeStdoutAndStderrIntoTextEnvelopes()
    {
        if (!File.Exists("/bin/bash"))
            return; // honour Skippable shape without taking the dependency

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-claudeio-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);

            // Stub writes a distinct marker to each stream then exits 0. Sleep
            // briefly so the bridge's reader tasks have observed both before
            // claude exits and the streams close.
            var stubPath = Path.Combine(tmpDir, "claude-streams-stub.sh");
            File.WriteAllText(stubPath,
                "#!/bin/bash\n" +
                "printf 'STDOUT-MARKER-LINE\\n'\n" +
                "printf 'STDERR-MARKER-LINE\\n' 1>&2\n" +
                "sleep 0.25\n" +
                "exit 0\n");
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":30}";
            await ctx.WriteStdinLineAsync(hello);

            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);

            // Concatenate every claude_stdout.text and every claude_stderr.text
            // emitted by the bridge — the readers chunk reads, so a single
            // line may arrive across multiple envelopes.
            var snap = ctx.Stdout.Snapshot();
            var stdoutText = string.Concat(snap
                .Where(e => e.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && t.GetString() == "claude_stdout")
                .Select(e => e.GetProperty("text").GetString() ?? ""));
            var stderrText = string.Concat(snap
                .Where(e => e.TryGetProperty("type", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && t.GetString() == "claude_stderr")
                .Select(e => e.GetProperty("text").GetString() ?? ""));

            Assert.Contains("STDOUT-MARKER-LINE", stdoutText);
            Assert.Contains("STDERR-MARKER-LINE", stderrText);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── WebSocketConnection auth-header fallback shapes ────────────────────────
    //
    // AcceptHandshakeAsync admits two auth header shapes the dedicated handshake
    // fixtures don't cover: (1) the lowercase `authorization` header used as a
    // fallback when `x-claude-code-ide-authorization` is absent, and (2) the
    // `Bearer <token>` prefixed form admitted via the EndsWith fallback. Both
    // are claimed as "drop-in JS parity" but neither was pinned. A regression
    // that drops either path would silently break claude --ide releases that
    // pick the other header shape (releases have been observed using both).

    [Fact]
    public async Task WebSocketConnection_AcceptHandshake_AcceptsLowercaseAuthorizationHeaderFallback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const string authToken = "lowercase-auth-fallback-token";
        var acceptTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            var ws = new WebSocketConnection(server.GetStream());
            return await ws.AcceptHandshakeAsync(authToken, CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        // Only the lowercase `authorization` header — NOT
        // x-claude-code-ide-authorization. The bridge's fallback branch is
        // what must admit this.
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "authorization: " + authToken + "\r\n" +
            "\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[1024];
        var n = await s.ReadAsync(respBuf.AsMemory());
        var resp = Encoding.ASCII.GetString(respBuf, 0, n);
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", resp);
        Assert.True(await acceptTask);
        listener.Stop();
    }

    [Fact]
    public async Task WebSocketConnection_AcceptHandshake_AcceptsBearerPrefixedAuthToken()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        const string authToken = "bearer-prefix-token-deadbeef";
        var acceptTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            var ws = new WebSocketConnection(server.GetStream());
            return await ws.AcceptHandshakeAsync(authToken, CancellationToken.None);
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        // `Bearer <token>` — admitted by the EndsWith branch.
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\n" +
            "Host: 127.0.0.1\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
            "Sec-WebSocket-Version: 13\r\n" +
            "x-claude-code-ide-authorization: Bearer " + authToken + "\r\n" +
            "\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[1024];
        var n = await s.ReadAsync(respBuf.AsMemory());
        var resp = Encoding.ASCII.GetString(respBuf, 0, n);
        Assert.StartsWith("HTTP/1.1 101 Switching Protocols", resp);
        Assert.True(await acceptTask);
        listener.Stop();
    }

    // ── WebSocketConnection.TryParseFrame oversized-length-127 defence ─────────
    //
    // RFC6455 §5.2 lets the length-127 form carry an 8-byte length, with the
    // MSB required to be 0 and a "reasonable" upper bound recommended. The
    // bridge parser accumulates the 8 bytes into a signed `long`, so a peer
    // that sets the MSB (0xFF as the high byte) yields a negative `len` —
    // `new byte[len]` then throws ArgumentOutOfRangeException. A peer that
    // sets a giant positive value (e.g. 0x00FF_FFFF_FFFF_FFFF) yields a
    // multi-PB allocation that throws OutOfMemoryException. Neither exception
    // is caught in ReceiveLoopAsync (only IOException/ObjectDisposedException
    // on the read are swallowed) and neither is caught in HandleClientAsync's
    // try/catch(OperationCanceledException) on the peer-receive task — so
    // either would escape Task.Run as an UnobservedTaskException and crash
    // the bridge under the default unobserved-exception policy.
    //
    // The fix caps `len` and signals the caller (via the new closeConnection
    // out parameter) to drop the peer cleanly. This fixture pins the contract:
    // an oversized length-127 frame causes the receive task to complete
    // gracefully without throwing, even though the bytes never form a valid
    // frame.

    [Fact]
    public async Task WebSocketConnection_TryParseFrame_RejectsNegativeLength127Frame_AndClosesReceiveLoopCleanly()
    {
        // 0xFF_FF_FF_FF_FF_FF_FF_FF parses as a negative signed long. A
        // no-bounds parser would feed that value into `new byte[len]`.
        await AssertOversizedLength127FrameClosesReceiveLoopCleanlyAsync(
        [
            0x81,                                   // FIN + text opcode
            (byte)(0x80 | 127),                     // masked + length-127
            0xFF, 0xFF, 0xFF, 0xFF,                 // high 4 bytes (MSB set -> negative)
            0xFF, 0xFF, 0xFF, 0xFF,                 // low 4 bytes
            0x12, 0x34, 0x56, 0x78,                 // mask bytes (irrelevant)
        ]);
    }

    [Fact]
    public async Task WebSocketConnection_TryParseFrame_RejectsPositiveOverCapLength127Frame_AndClosesReceiveLoopCleanly()
    {
        // 0x00_00_00_00_00_80_00_01 is MaxFramePayloadBytes + 1. It is a
        // positive signed long, so this case specifically pins the upper-bound
        // cap rather than the signed-overflow guard.
        await AssertOversizedLength127FrameClosesReceiveLoopCleanlyAsync(
        [
            0x81,                                   // FIN + text opcode
            (byte)(0x80 | 127),                     // masked + length-127
            0x00, 0x00, 0x00, 0x00,                 // high 4 bytes
            0x00, 0x80, 0x00, 0x01,                 // low 4 bytes: 8 MiB + 1
            0x12, 0x34, 0x56, 0x78,                 // mask bytes (irrelevant)
        ]);
    }

    private static async Task AssertOversizedLength127FrameClosesReceiveLoopCleanlyAsync(byte[] hostile)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receiveDone = new TaskCompletionSource<bool>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var server = await listener.AcceptTcpClientAsync();
                var ws = new WebSocketConnection(server.GetStream());
                var ok = await ws.AcceptHandshakeAsync(authToken: "", CancellationToken.None);
                if (!ok)
                {
                    receiveDone.TrySetException(new Exception("handshake failed"));
                    return;
                }
                // The receive loop MUST return without throwing — even with a
                // hostile oversized length the closeConnection signal should
                // drop us out cleanly. A regression that dropped the
                // out-parameter close signal would either loop forever
                // re-parsing the same bad prefix or crash the loop.
                await ws.ReceiveLoopAsync(_ => { }, CancellationToken.None);
                receiveDone.TrySetResult(true);
            }
            catch (Exception ex) { receiveDone.TrySetException(ex); }
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: AAAA\r\nSec-WebSocket-Version: 13\r\n\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        // Wait for the 101 upgrade response.
        var respBuf = new byte[1024];
        var headerSoFar = new List<byte>();
        while (true)
        {
            var n = await s.ReadAsync(respBuf.AsMemory());
            if (n <= 0) throw new EndOfStreamException("handshake stream closed");
            for (int i = 0; i < n; i++) headerSoFar.Add(respBuf[i]);
            if (HasHeaderTerminator(headerSoFar, out _)) break;
        }

        // The frame carries the mask bit so it looks valid up to the length
        // check; the 4 mask bytes are never reached because the length check
        // fails first.
        await s.WriteAsync(hostile);
        await s.FlushAsync();

        // The server's receive task is expected to detect the bad length,
        // mark closeConnection, and return out of ReceiveLoopAsync. Close the
        // client side so any post-rejection read returns EOF cleanly.
        try { client.Close(); } catch { }

        // The Task.Run wrapper must complete with `true` (no exception),
        // proving both (a) the length-127 path didn't crash with
        // ArgumentOutOfRangeException/OutOfMemoryException, and (b) the
        // closeConnection signal actually drove the loop to return.
        var ok = await receiveDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(ok);

        listener.Stop();
    }

    [Fact]
    public async Task WebSocketConnection_ReceiveLoop_CloseFrameEndsStreamWithoutDeliveringText()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var receiveDone = new TaskCompletionSource<int>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var server = await listener.AcceptTcpClientAsync();
                var ws = new WebSocketConnection(server.GetStream());
                var ok = await ws.AcceptHandshakeAsync(authToken: "", CancellationToken.None);
                if (!ok)
                {
                    receiveDone.TrySetException(new Exception("handshake failed"));
                    return;
                }

                var delivered = 0;
                await ws.ReceiveLoopAsync(_ => delivered++, CancellationToken.None);
                receiveDone.TrySetResult(delivered);
            }
            catch (Exception ex) { receiveDone.TrySetException(ex); }
        });

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: AAAA\r\nSec-WebSocket-Version: 13\r\n\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();

        var respBuf = new byte[1024];
        var headerSoFar = new List<byte>();
        while (true)
        {
            var n = await s.ReadAsync(respBuf.AsMemory()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (n <= 0) throw new EndOfStreamException("handshake stream closed");
            for (int i = 0; i < n; i++) headerSoFar.Add(respBuf[i]);
            if (HasHeaderTerminator(headerSoFar, out _)) break;
        }

        await s.WriteAsync(BuildClientMaskedCloseFrame());
        await s.FlushAsync();

        var deliveredFrames = await receiveDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, deliveredFrames);

        listener.Stop();
    }

    // ── WebSocketConnection.WriteRawAsync IOException/ODE swallow ──────────────
    //
    // Commit 89a9a03 wrapped WriteRawAsync's stream.WriteAsync in
    // IOException/ObjectDisposedException swallows specifically because the
    // 400/401 error-branch awaits in AcceptHandshakeAsync have NO outer
    // try/catch (the only try/catch in HandleClientAsync's fire-and-forget
    // Task.Run is on OperationCanceledException). Without the catch, a peer
    // that tore the TCP connection down mid-handshake would propagate
    // IOException out as an UnobservedTaskException — under the default
    // unobserved-exception policy, that crashes the bridge.
    //
    // The handshake-rejection fixtures (RejectsWrongAuthTokenWith401,
    // HandshakeRejectsMissingSecWebSocketKey) keep the client connected
    // throughout the reply, so they never exercise the catch. This fixture
    // forces the WriteAsync to fail by sending a complete bad-auth request
    // and immediately RST-closing the TCP connection (LingerOption(true, 0)
    // → RST instead of FIN), then delaying briefly before the server-side
    // AcceptHandshakeAsync runs. The server reads the request from the kernel
    // buffer (already delivered before the RST), checks auth (wrong → 401
    // branch), then tries to write the 401 response on a dead socket. The
    // WriteRawAsync catch must absorb the resulting IOException and
    // AcceptHandshakeAsync must complete with `false` rather than throwing.

    [Fact]
    public async Task WebSocketConnection_AcceptHandshake_WriteRawAsyncFailureOnPeerDisconnect_IsSwallowed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverDone = new TaskCompletionSource<bool>();
        var clientGone = new TaskCompletionSource<bool>();
        _ = Task.Run(async () =>
        {
            try
            {
                using var server = await listener.AcceptTcpClientAsync();
                // Wait until the test driver has closed the client side and
                // the kernel has processed the RST so the server-side write
                // will hit ECONNRESET — without this delay the WriteAsync may
                // race ahead of the RST and complete by buffering locally.
                await clientGone.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(150);
                var ws = new WebSocketConnection(server.GetStream());
                // AcceptHandshakeAsync reads the queued bytes, checks auth
                // (wrong token → 401 branch), tries to write the 401 reply
                // on a dead socket. The IOException must be swallowed inside
                // WriteRawAsync and AcceptHandshakeAsync must return false.
                var ok = await ws.AcceptHandshakeAsync("the-real-token", CancellationToken.None);
                serverDone.TrySetResult(ok);
            }
            catch (Exception ex) { serverDone.TrySetException(ex); }
        });

        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        // LingerState(true, 0) causes Close() to send TCP RST instead of FIN.
        // A subsequent server-side write hits ECONNRESET which surfaces as
        // IOException in NetworkStream.WriteAsync — the exact failure mode
        // commit 89a9a03's catch was added to absorb.
        client.Client.LingerState = new LingerOption(true, 0);
        var s = client.GetStream();
        var req = Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: AAAA\r\nSec-WebSocket-Version: 13\r\n" +
            "x-claude-code-ide-authorization: wrong-token\r\n\r\n");
        await s.WriteAsync(req);
        await s.FlushAsync();
        client.Close();
        clientGone.TrySetResult(true);

        // The server-side AcceptHandshakeAsync MUST complete cleanly with
        // false. If WriteRawAsync's IOException catch is dropped, this
        // serverDone task faults with IOException and the assertion fails.
        var result = await serverDone.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.False(result,
            "AcceptHandshakeAsync must return false on auth mismatch and absorb the " +
            "IOException raised by the 401-reply write to a torn-down peer.");
        listener.Stop();
    }

    // ── Bridge.TerminateClaudeProcess SIGTERM-first path ───────────────────────
    //
    // Commit 89a9a03 added a P/Invoke kill(2) with SIGTERM=15, a 1.5s grace
    // window, and a Process.Kill(entireProcessTree:true) fallback specifically
    // to give claude --ide time to flush its session JSONL transcript before
    // exiting. .NET's Process.Kill(bool) always sends SIGKILL on Linux — no
    // SIGTERM overload — so reverting to a bare Process.Kill leaves a
    // half-written transcript that surfaces as a thinking-block immutability
    // 400 on the next session/load.
    //
    // The existing end-to-end fixtures use either /usr/bin/true (already
    // exited before Shutdown runs; HasExited=true short-circuits
    // TerminateClaudeProcess) or a short-lived 0.5s sleep stub (also exited
    // by the time Shutdown fires). Neither verifies that SIGTERM=15 is
    // actually delivered first. This fixture wires a bash stub that traps
    // SIGTERM, writes a marker file (proof the polite signal arrived), and
    // exits 0 — if a regression dropped NativeMethods.Kill and reverted to a
    // bare Process.Kill, the stub would be SIGKILL'd immediately, the trap
    // would never run, and the marker file would not exist.

    [Fact]
    public async Task Bridge_TerminateClaudeProcess_SendsSigtermFirst_GivingClaudeGraceToFlushTranscript()
    {
        if (!File.Exists("/bin/bash"))
            return; // honour Skippable shape without taking the dependency

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-sigterm-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);

            var markerPath = Path.Combine(tmpDir, "sigterm-trapped.marker");
            var stubPath = Path.Combine(tmpDir, "claude-sigterm-stub.sh");

            // Bash stub: trap SIGTERM → write marker → exit 0. The `sleep &
            // wait` pattern is required because bash's `trap` only fires
            // BETWEEN commands by default; backgrounding the sleep and
            // wait-ing on it lets the trap deliver mid-sleep. argv[1] ==
            // "--ide" (the flag Bridge always prepends), argv[2] == the
            // marker path the trap writes.
            File.WriteAllText(stubPath,
                "#!/bin/bash\n" +
                "MARKER=\"$2\"\n" +
                "trap 'echo \"got-sigterm\" > \"$MARKER\"; exit 0' SIGTERM\n" +
                "sleep 60 &\n" +
                "wait $!\n");
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"" + markerPath
                + "\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":30}";
            await ctx.WriteStdinLineAsync(hello);

            await ctx.WaitForEnvelopeAsync("ready");

            // Trigger shutdown via the host envelope — drives Bridge.Shutdown,
            // which sees the stub still running (HasExited=false) and calls
            // TerminateClaudeProcess. SIGTERM-first means the bash trap runs;
            // SIGKILL-first means it doesn't.
            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");

            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);

            // Give the bash trap a brief moment to flush its marker file —
            // TerminateClaudeProcess returns as soon as p.HasExited goes true,
            // which happens just before the file write completes.
            for (int i = 0; i < 50 && !File.Exists(markerPath); i++)
                await Task.Delay(50);

            Assert.True(File.Exists(markerPath),
                "Marker file missing — bash stub did NOT receive SIGTERM, meaning " +
                "TerminateClaudeProcess sent SIGKILL directly (regression: dropped " +
                "the NativeMethods.Kill(pid, 15) call). The polite-signal contract " +
                "is what protects claude's session JSONL flush.");
            // Marker contents pin SIGTERM specifically — the bash trap only
            // writes this literal when invoked from the SIGTERM handler.
            Assert.Equal("got-sigterm", File.ReadAllText(markerPath).Trim());
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bridge_TerminateClaudeProcess_FallsBackToSigkillWhenChildIgnoresSigterm()
    {
        if (!File.Exists("/bin/bash"))
            return;

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-sigkill-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);

            var pidPath = Path.Combine(tmpDir, "claude.pid");
            var markerPath = Path.Combine(tmpDir, "sigterm-observed.marker");
            var stubPath = Path.Combine(tmpDir, "claude-ignore-sigterm-stub.sh");

            File.WriteAllText(stubPath,
                "#!/bin/bash\n" +
                "PIDFILE=\"$2\"\n" +
                "MARKER=\"$3\"\n" +
                "echo $$ > \"$PIDFILE\"\n" +
                "trap 'echo \"got-sigterm-but-staying-alive\" > \"$MARKER\"' SIGTERM\n" +
                "while true; do sleep 60 & wait $!; done\n");
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            await using var ctx = new BridgeRunHandle();
            var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                + "\",\"claudeArgs\":[\"" + pidPath + "\",\"" + markerPath
                + "\"],\"workingDirectory\":\"" + workDir
                + "\",\"lockDir\":\"" + lockDir
                + "\",\"turnTimeoutSeconds\":30}";
            await ctx.WriteStdinLineAsync(hello);

            await ctx.WaitForEnvelopeAsync("ready");
            for (int i = 0; i < 50 && !File.Exists(pidPath); i++)
                await Task.Delay(50);
            Assert.True(File.Exists(pidPath), "SIGKILL fallback fixture did not record a child pid.");
            var childPid = int.Parse(File.ReadAllText(pidPath).Trim(), CultureInfo.InvariantCulture);

            await ctx.WriteStdinLineAsync("{\"type\":\"shutdown\"}");
            var exitCode = await ctx.WaitForExitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, exitCode);

            Assert.True(File.Exists(markerPath),
                "Fixture child did not observe the initial SIGTERM; this test must exercise the SIGKILL fallback, not a direct kill.");
            for (int i = 0; i < 50 && Directory.Exists("/proc/" + childPid); i++)
                await Task.Delay(50);
            Assert.False(Directory.Exists("/proc/" + childPid),
                "Child process remained alive after ignoring SIGTERM; TerminateClaudeProcess must fall back to SIGKILL.");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    // ── Bridge PosixSignalRegistration handlers (SIGTERM / SIGINT / SIGHUP) ────
    //
    // Commit 8152b99 added PosixSignalRegistration.Create for SIGTERM, SIGINT
    // and SIGHUP because the prior Console.CancelKeyPress + ProcessExit pair
    // never fired on SIGTERM (the sandbox provider's normal stop signal),
    // silently leaking ~/.claude/ide/<port>.lock files and claude --ide
    // subprocess trees per the commit message. No test verifies these
    // registrations actually trigger Shutdown — sending a real signal from
    // the in-process BridgeRunHandle test seam would kill the test runner.
    //
    // Coverage requires spawning the bridge as a subprocess and signalling
    // it. We invoke the just-built bridge dll via `dotnet exec` (the test
    // project ProjectReferences the AcpBridge assembly so `typeof(Bridge)
    // .Assembly.Location` resolves to the IL build alongside its deps.json /
    // runtimeconfig.json), pipe a hello envelope in, wait for the `ready`
    // envelope on stdout, then kill(2) the subprocess with the signal under
    // test. The handler's `ctx.Cancel = true` MUST suppress .NET's default
    // (terminate) so the bridge exits cleanly with code 0; the handler's
    // Shutdown(0) MUST clean up the lockfile.
    //
    // A regression that drops the three PosixSignalRegistration.Create calls
    // (or wires them to a noop callback) would either let the process exit
    // with the signal-default exit code 128+signal (SIGTERM → 143, SIGINT →
    // 130, SIGHUP → 129) OR leave a leaked lockfile in place — both are
    // failure modes this fixture catches.

    [Theory]
    [InlineData(15, "SIGTERM")] // sandbox provider's normal stop signal
    [InlineData(2,  "SIGINT")]  // Ctrl+C
    [InlineData(1,  "SIGHUP")]  // controlling-terminal hangup
    public async Task Bridge_PosixSignalHandlers_TriggerCleanShutdownAndLockfileCleanup(int signo, string signalName)
    {
        if (!File.Exists("/bin/bash"))
            return; // honour Skippable shape without taking the dependency

        var bridgeDllPath = typeof(Bridge).Assembly.Location;
        Assert.True(File.Exists(bridgeDllPath),
            "AcpBridge dll missing at " + bridgeDllPath +
            " — the test project should ProjectReference the AcpBridge assembly.");

        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-posixsig-" + signalName + "-").FullName;
        try
        {
            var workDir = Path.Combine(tmpDir, "work");
            var lockDir = Path.Combine(tmpDir, "locks");
            Directory.CreateDirectory(workDir);
            // Long-running stub so the bridge stays alive until we signal it.
            var stubPath = Path.Combine(tmpDir, "claude-longsleep-stub.sh");
            File.WriteAllText(stubPath,
                "#!/bin/bash\n" +
                "exec sleep 60\n");
            File.SetUnixFileMode(stubPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var psi = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = tmpDir,
            };
            psi.ArgumentList.Add("exec");
            psi.ArgumentList.Add(bridgeDllPath);

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for dotnet exec.");

            try
            {
                var hello = "{\"type\":\"hello\",\"claudeBinary\":\"" + stubPath
                    + "\",\"workingDirectory\":\"" + workDir
                    + "\",\"lockDir\":\"" + lockDir
                    + "\",\"turnTimeoutSeconds\":60}";
                await proc.StandardInput.WriteLineAsync(hello);
                await proc.StandardInput.FlushAsync();

                // Read stdout line-by-line until we see the `ready` envelope —
                // confirms the bridge is up, the lockfile is written, and the
                // signal handlers have been registered (HandleHello completes
                // before `ready` fires, but PosixSignalRegistration.Create
                // happens at the top of RunAsync which runs first).
                string? lockPath = null;
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (DateTime.UtcNow < deadline)
                {
                    var line = await proc.StandardOutput.ReadLineAsync().WaitAsync(
                        TimeSpan.FromSeconds(5));
                    if (line is null) break;
                    if (string.IsNullOrEmpty(line)) continue;
                    using var doc = JsonDocument.Parse(line);
                    if (!doc.RootElement.TryGetProperty("type", out var typeEl)) continue;
                    if (typeEl.GetString() == "ready")
                    {
                        lockPath = doc.RootElement.GetProperty("lockPath").GetString();
                        break;
                    }
                }
                Assert.NotNull(lockPath);
                Assert.True(File.Exists(lockPath),
                    "Lockfile must exist after `ready` envelope — pre-signal state.");

                // Drain remaining stdout in the background so the pipe doesn't
                // fill up and block the bridge while we wait for the signal.
                var drainTask = Task.Run(async () =>
                {
                    try
                    {
                        while (await proc.StandardOutput.ReadLineAsync() is not null) { }
                    }
                    catch { }
                });

                // Send the signal directly via libc.kill(2). The bridge's
                // PosixSignalRegistration handler runs, sets ctx.Cancel=true
                // (suppressing .NET's default-terminate), and calls
                // Shutdown(0) → lockfile cleanup → bridge exits with code 0.
                var killResult = LibcKill(proc.Id, signo);
                Assert.Equal(0, killResult);

                Assert.True(proc.WaitForExit(milliseconds: 15_000),
                    "Bridge did not exit within 15s of " + signalName +
                    " — regression: PosixSignalRegistration handler missing or wired to a noop.");

                // Exit code 0 means our handler suppressed the default and
                // ran Shutdown(0). Exit codes 128+signo (143 / 130 / 129)
                // indicate the default action ran — the handler is missing
                // or didn't set ctx.Cancel.
                Assert.Equal(0, proc.ExitCode);

                // The Shutdown handler also deletes the lockfile. A regression
                // that calls Shutdown(0) but skips the lockfile delete still
                // exits cleanly; this assertion catches that drift separately.
                Assert.False(File.Exists(lockPath),
                    "Lockfile leaked after " + signalName +
                    " — Shutdown(0) ran but the per-turn lockfile cleanup was skipped.");

                await drainTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int LibcKill(int pid, int sig);

    // ── Helpers for the new orchestration / regression fixtures above ───────────

    private static string BuildSeqAcpSendEnvelope(int seq) =>
        "{\"type\":\"acp_send\",\"payload\":{\"jsonrpc\":\"2.0\",\"id\":" + seq
        + ",\"method\":\"seq/test\",\"params\":{\"seq\":" + seq + "}}}";

    /// <summary>
    /// Writes a bash stub at <paramref name="stubPath"/> that mirrors the
    /// invocation contract claude --ide would honour: argv[0] is the literal
    /// "--ide" flag the bridge always prepends, argv[1] is the desired sleep
    /// duration in seconds (so the stub stays alive long enough for the test
    /// to drive the WebSocket peer). Off-the-shelf `sleep` can't be used as a
    /// long-lived stub because GNU sleep rejects the leading `--ide` arg and
    /// exits ~immediately, racing the test's WebSocket connect.
    /// </summary>
    private static string WriteLongRunningClaudeStub(string tmpDir)
    {
        var stubPath = Path.Combine(tmpDir, "claude-sleep-stub.sh");
        File.WriteAllText(stubPath,
            "#!/bin/bash\n" +
            "# argv[1] == \"--ide\" (the flag Bridge always prepends), argv[2] == duration.\n" +
            "exec sleep \"${2:-30}\"\n");
        File.SetUnixFileMode(stubPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return stubPath;
    }

    private static string WriteClaudeStubThatConnectsFromDescendant(string tmpDir)
    {
        var stubPath = Path.Combine(tmpDir, "claude-descendant-ws-stub.sh");
        File.WriteAllText(stubPath, """
#!/bin/bash
set -euo pipefail
LOCK_DIR="$2"
MARKER="$3"
python3 - "$LOCK_DIR" "$MARKER" <<'PY' &
import glob
import json
import os
import socket
import sys
import time

lock_dir = sys.argv[1]
marker = sys.argv[2]
deadline = time.time() + 10
lock_path = None
while time.time() < deadline:
    matches = glob.glob(os.path.join(lock_dir, "*.lock"))
    if matches:
        lock_path = matches[0]
        break
    time.sleep(0.05)
if lock_path is None:
    sys.exit(3)

with open(lock_path, encoding="utf-8") as handle:
    lockfile = json.load(handle)
port = int(lockfile["url"].rsplit(":", 1)[1].split("/", 1)[0])
auth = lockfile["authToken"]

sock = socket.create_connection(("127.0.0.1", port), timeout=5)
request = (
    "GET / HTTP/1.1\r\n"
    "Host: 127.0.0.1\r\n"
    "Upgrade: websocket\r\n"
    "Connection: Upgrade\r\n"
    "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n"
    "Sec-WebSocket-Version: 13\r\n"
    "x-claude-code-ide-authorization: " + auth + "\r\n\r\n"
)
sock.sendall(request.encode("ascii"))
response = b""
while b"\r\n\r\n" not in response:
    chunk = sock.recv(4096)
    if not chunk:
        break
    response += chunk
if not response.startswith(b"HTTP/1.1 101"):
    sys.stderr.write("unexpected websocket handshake response: %r\n" % response[:200])
    sys.exit(4)

with open(marker, "w", encoding="utf-8") as handle:
    handle.write("connected\n")

try:
    time.sleep(30)
finally:
    sock.close()
PY
child=$!
trap 'kill "$child" 2>/dev/null || true; exit 0' TERM INT HUP
wait "$child"
""");
        File.SetUnixFileMode(stubPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return stubPath;
    }

    private static bool CommandExists(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, name)))
                return true;
        }
        return false;
    }

    private static async Task<TcpClient> ConnectAuthenticatedWebSocketClientAsync(
        int port,
        string authToken,
        TimeSpan? timeout = null)
    {
        timeout ??= TimeSpan.FromSeconds(10);
        using var cts = new CancellationTokenSource(timeout.Value);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            var req = Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "x-claude-code-ide-authorization: " + authToken + "\r\n\r\n");
            await stream.WriteAsync(req, cts.Token).ConfigureAwait(false);
            await stream.FlushAsync(cts.Token).ConfigureAwait(false);

            var buf = new byte[4096];
            var header = new List<byte>();
            while (true)
            {
                var n = await stream.ReadAsync(buf.AsMemory(), cts.Token).ConfigureAwait(false);
                if (n <= 0)
                    throw new IOException("WS handshake response stream closed before headers ended.");
                for (int i = 0; i < n; i++) header.Add(buf[i]);
                if (HasHeaderTerminator(header, out _))
                {
                    var statusLine = Encoding.ASCII.GetString(header.ToArray(), 0, Math.Min(header.Count, 256));
                    if (!statusLine.StartsWith("HTTP/1.1 101", StringComparison.Ordinal))
                        throw new IOException("WS handshake did not return 101: " + statusLine);
                    return client;
                }
                if (header.Count > 32 * 1024)
                    throw new IOException("WS handshake response headers exceeded 32 KiB.");
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static Dictionary<string, string> ParseKeyValueLog(string path)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(path))
        {
            var idx = line.IndexOf('=');
            if (idx <= 0) continue;
            d[line.Substring(0, idx)] = line.Substring(idx + 1);
        }
        return d;
    }

    private static int GetCurrentProcessStdinInodeViaSubprocess()
    {
        // Spawn `stat` WITHOUT redirecting stdin → subprocess inherits the
        // test process's fd 0 → reports the test's stdin inode. We compare
        // this against the bridge-spawned stub's reported fd 0 inode to
        // determine whether RedirectStandardInput=true did its job.
        var psi = new ProcessStartInfo("stat", "-L -c %i /proc/self/fd/0")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Explicitly NOT redirecting stdin so the child inherits ours.
            RedirectStandardInput = false,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        if (!p.WaitForExit(5000))
            throw new TimeoutException("stat subprocess timed out");
        if (!int.TryParse(output, out var inode))
            throw new InvalidOperationException(
                "Could not parse fd 0 inode from `stat` output: " + output);
        return inode;
    }

    private static bool TryParseUnmaskedWebSocketFrame(List<byte> acc, out int opcode, out byte[] payload)
    {
        opcode = 0;
        payload = Array.Empty<byte>();
        if (acc.Count < 2) return false;
        var b1 = acc[0];
        var b2 = acc[1];
        opcode = b1 & 0x0f;
        bool masked = (b2 & 0x80) != 0;
        long len = b2 & 0x7f;
        int offset = 2;
        if (len == 126)
        {
            if (acc.Count < 4) return false;
            len = (acc[2] << 8) | acc[3];
            offset = 4;
        }
        else if (len == 127)
        {
            if (acc.Count < 10) return false;
            len = 0;
            for (int i = 0; i < 8; i++) len = (len << 8) | acc[2 + i];
            offset = 10;
        }
        int maskStart = -1;
        if (masked)
        {
            if (acc.Count < offset + 4) return false;
            maskStart = offset;
            offset += 4;
        }
        if (acc.Count < offset + len) return false;
        var raw = new byte[len];
        for (long i = 0; i < len; i++) raw[i] = acc[offset + (int)i];
        if (masked)
        {
            for (long i = 0; i < len; i++) raw[i] ^= acc[maskStart + (int)(i % 4)];
        }
        acc.RemoveRange(0, offset + (int)len);
        payload = raw;
        return true;
    }

    /// <summary>
    /// In-process driver for end-to-end Bridge fixtures. Owns:
    ///   - A System.IO.Pipelines.Pipe whose reader is wired into the bridge's
    ///     test-seam stdin (so the test can write hello / acp_send / shutdown
    ///     envelopes over time, not as one upfront blob).
    ///   - A <see cref="LineCapturingStream"/> wired into the Emitter so the
    ///     test can wait for / count envelopes the bridge wrote to stdout.
    ///   - A TCP/WebSocket client that performs the RFC6455 handshake against
    ///     the bridge's listener (using the auth token recovered from the
    ///     lockfile) and lets the test send / receive ACP frames.
    /// </summary>
    private sealed class BridgeRunHandle : IAsyncDisposable
    {
        private readonly Pipe _stdinPipe = new();
        private readonly LineCapturingStream _stdout = new();
        private readonly IDisposable _emitterScope;
        private readonly Bridge _bridge;
        private readonly Task<int> _runTask;

        private TcpClient? _wsClient;
        private NetworkStream? _wsStream;
        private readonly List<byte> _wsRecvBuffer = new();

        public LineCapturingStream Stdout => _stdout;
        public Bridge Bridge => _bridge;

        public BridgeRunHandle(bool useProductionPeerAuthorizer = false)
        {
            _emitterScope = Emitter.OverrideStreamForTests(_stdout);
            _bridge = new Bridge(
                _stdinPipe.Reader.AsStream(leaveOpen: true),
                peerAuthorizer: useProductionPeerAuthorizer ? null : (_, _) => true);
            _runTask = Task.Run(() => _bridge.RunAsync());
        }

        public async Task WriteStdinLineAsync(string envelopeJson)
        {
            var bytes = Encoding.UTF8.GetBytes(envelopeJson + "\n");
            await _stdinPipe.Writer.WriteAsync(bytes).ConfigureAwait(false);
            await _stdinPipe.Writer.FlushAsync().ConfigureAwait(false);
        }

        public Task<JsonElement> WaitForEnvelopeAsync(string type,
            TimeSpan? timeout = null, int startIndex = 0) =>
            _stdout.WaitForEnvelopeAsync(type, timeout ?? TimeSpan.FromSeconds(15), startIndex);

        public Task<int> WaitForExitAsync(TimeSpan timeout) =>
            _runTask.WaitAsync(timeout);

        public async Task ConnectWebSocketAsync(int port, string authToken,
            TimeSpan? timeout = null, CancellationToken ct = default)
        {
            timeout ??= TimeSpan.FromSeconds(10);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout.Value);

            _wsClient = new TcpClient();
            await _wsClient.ConnectAsync(IPAddress.Loopback, port, cts.Token).ConfigureAwait(false);
            _wsStream = _wsClient.GetStream();
            var req = Encoding.ASCII.GetBytes(
                "GET / HTTP/1.1\r\n" +
                "Host: 127.0.0.1\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n" +
                "Sec-WebSocket-Version: 13\r\n" +
                "x-claude-code-ide-authorization: " + authToken + "\r\n\r\n");
            await _wsStream.WriteAsync(req, cts.Token).ConfigureAwait(false);
            await _wsStream.FlushAsync(cts.Token).ConfigureAwait(false);

            // Drain response headers up to \r\n\r\n. Any bytes that follow
            // (e.g. an immediately-sent text frame from the server) stay in
            // _wsRecvBuffer for the next ReadWebSocketFrameAsync call.
            var buf = new byte[4096];
            while (true)
            {
                var n = await _wsStream.ReadAsync(buf.AsMemory(), cts.Token).ConfigureAwait(false);
                if (n <= 0)
                    throw new IOException("WS handshake response stream closed before headers ended.");
                for (int i = 0; i < n; i++) _wsRecvBuffer.Add(buf[i]);
                if (HasHeaderTerminator(_wsRecvBuffer, out var after))
                {
                    var statusLine = Encoding.ASCII.GetString(
                        _wsRecvBuffer.ToArray(), 0, Math.Min(_wsRecvBuffer.Count, 256));
                    if (!statusLine.StartsWith("HTTP/1.1 101"))
                        throw new IOException("WS handshake did not return 101: " + statusLine);
                    _wsRecvBuffer.RemoveRange(0, after);
                    return;
                }
                if (_wsRecvBuffer.Count > 32 * 1024)
                    throw new IOException("WS handshake response headers exceeded 32 KiB.");
            }
        }

        public async Task SendWebSocketFrameAsync(string text,
            CancellationToken ct = default)
        {
            if (_wsStream is null) throw new InvalidOperationException("WS not connected.");
            var frame = BuildClientMaskedTextFrame(Encoding.UTF8.GetBytes(text));
            await _wsStream.WriteAsync(frame, ct).ConfigureAwait(false);
            await _wsStream.FlushAsync(ct).ConfigureAwait(false);
        }

        public Task<string> ReadWebSocketFrameAsync(TimeSpan timeout) =>
            ReadWebSocketFrameAsync(new CancellationTokenSource(timeout).Token);

        public async Task<string> ReadWebSocketFrameAsync(CancellationToken ct)
        {
            if (_wsStream is null) throw new InvalidOperationException("WS not connected.");
            var buf = new byte[8192];
            while (true)
            {
                if (TryParseUnmaskedWebSocketFrame(_wsRecvBuffer, out var op, out var payload))
                {
                    if (op == 0x1 || op == 0x2)
                        return Encoding.UTF8.GetString(payload);
                    if (op == 0x8)
                        throw new EndOfStreamException("WS peer sent CLOSE frame.");
                    // ignore ping / pong / continuation
                    continue;
                }
                int n;
                try { n = await _wsStream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw new TimeoutException("WS read timed out."); }
                if (n <= 0) throw new EndOfStreamException("WS stream EOF before a complete frame.");
                for (int i = 0; i < n; i++) _wsRecvBuffer.Add(buf[i]);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _wsStream?.Close(); } catch { }
            try { _wsClient?.Dispose(); } catch { }
            try { await _stdinPipe.Writer.CompleteAsync().ConfigureAwait(false); } catch { }
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch { }
            try { _emitterScope.Dispose(); } catch { }
            try { await _bridge.DisposeAsync().ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Thread-safe stdout sink for end-to-end bridge fixtures: parses each
    /// emitted line as a JSON envelope and exposes wait / count helpers.
    /// </summary>
    private sealed class LineCapturingStream : Stream
    {
        private readonly List<JsonElement> _envelopes = new();
        private readonly StringBuilder _partial = new();
        private readonly object _lock = new();
        private readonly SemaphoreSlim _signal = new(0, int.MaxValue);

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            ProcessChunk(Encoding.UTF8.GetString(buffer, offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) =>
            ProcessChunk(Encoding.UTF8.GetString(buffer));

        public override void WriteByte(byte value) =>
            ProcessChunk(((char)value).ToString());

        private void ProcessChunk(string chunk)
        {
            int released = 0;
            lock (_lock)
            {
                foreach (var c in chunk)
                {
                    if (c == '\n')
                    {
                        var line = _partial.ToString();
                        _partial.Clear();
                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            _envelopes.Add(doc.RootElement.Clone());
                            released++;
                        }
                        catch (JsonException) { /* non-JSON line — emitter never produces these */ }
                    }
                    else _partial.Append(c);
                }
            }
            for (int i = 0; i < released; i++) _signal.Release();
        }

        public int SnapshotCount()
        {
            lock (_lock) return _envelopes.Count;
        }

        public IReadOnlyList<JsonElement> Snapshot()
        {
            lock (_lock) return _envelopes.ToList();
        }

        public int CountByType(string type)
        {
            lock (_lock)
            {
                int n = 0;
                foreach (var e in _envelopes)
                {
                    if (e.TryGetProperty("type", out var t)
                        && t.ValueKind == JsonValueKind.String
                        && string.Equals(t.GetString(), type, StringComparison.Ordinal))
                        n++;
                }
                return n;
            }
        }

        public async Task<JsonElement> WaitForEnvelopeAsync(string type, TimeSpan timeout, int startIndex)
        {
            var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            while (true)
            {
                lock (_lock)
                {
                    for (int i = startIndex; i < _envelopes.Count; i++)
                    {
                        if (_envelopes[i].TryGetProperty("type", out var t)
                            && t.ValueKind == JsonValueKind.String
                            && string.Equals(t.GetString(), type, StringComparison.Ordinal))
                            return _envelopes[i];
                    }
                }
                var remainingMs = deadline - Environment.TickCount64;
                if (remainingMs <= 0)
                {
                    string typesSeen;
                    lock (_lock)
                    {
                        typesSeen = string.Join(",", _envelopes
                            .Select(e => e.TryGetProperty("type", out var t)
                                ? t.GetString() ?? "?"
                                : "?"));
                    }
                    throw new TimeoutException(
                        $"Timed out waiting for envelope type '{type}'. Captured: [{typesSeen}]");
                }
                await _signal.WaitAsync(TimeSpan.FromMilliseconds(Math.Min(remainingMs, 250)))
                    .ConfigureAwait(false);
            }
        }
    }

    private static PublishScriptFixture CreatePublishScriptFixture()
    {
        var solutionRoot = FindAncestorContaining(AppContext.BaseDirectory, "CodeyBox.slnx")
            ?? throw new InvalidOperationException(
                "Cannot locate solution root from " + AppContext.BaseDirectory +
                " — ensure CodeyBox.slnx exists in an ancestor directory.");

        var sourceScript = Path.Combine(solutionRoot, "scripts", "publish-acp-bridge.sh");
        Assert.True(File.Exists(sourceScript), "Publish script missing at " + sourceScript);

        var tempRoot = Directory.CreateTempSubdirectory("cb-acp-publish-script-").FullName;
        var scriptsDir = Path.Combine(tempRoot, "scripts");
        var resourceDir = Path.Combine(tempRoot, "src", "CodeyBox.Agents.Claude", "Resources");
        var toolsDir = Path.Combine(tempRoot, "tools");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(resourceDir);
        Directory.CreateDirectory(toolsDir);

        var scriptPath = Path.Combine(scriptsDir, "publish-acp-bridge.sh");
        File.Copy(sourceScript, scriptPath);
        MakeExecutable(scriptPath);

        var callLog = Path.Combine(tempRoot, "calls.log");
        WriteExecutable(Path.Combine(toolsDir, "dotnet"), """
#!/usr/bin/env bash
set -euo pipefail
printf 'dotnet' >> "$CALL_LOG"
for arg in "$@"; do printf ' %s' "$arg" >> "$CALL_LOG"; done
printf '\n' >> "$CALL_LOG"
expected=(
    publish
    src/CodeyBox.Agents.Claude.AcpBridge/CodeyBox.Agents.Claude.AcpBridge.csproj
    -c
    Release
    -r
    linux-musl-x64
    --self-contained
    true
    -p:PublishAot=true
    -p:StaticExecutable=true
)
actual=("$@")
if [ "${#actual[@]}" -ne "${#expected[@]}" ]; then
    echo "unexpected dotnet publish argc: $*" >&2
    exit 23
fi
for i in "${!expected[@]}"; do
    if [ "${actual[$i]}" != "${expected[$i]}" ]; then
        echo "unexpected dotnet publish argv[$i]: expected ${expected[$i]}, got ${actual[$i]}" >&2
        exit 23
    fi
done
publish_dir="src/CodeyBox.Agents.Claude.AcpBridge/bin/Release/net10.0/linux-musl-x64/publish"
mkdir -p "$publish_dir"
if [ "${CODEYBOX_TEST_DOTNET_SKIP_OUTPUT:-0}" = "1" ]; then
    exit 0
fi
cat > "$publish_dir/CodeyBox.Agents.Claude.AcpBridge" <<'PY'
#!/usr/bin/env python3
import json
import os
import sys

lock_path = None
session_method = None

def emit(payload):
    print(json.dumps(payload, separators=(",", ":")), flush=True)

for raw in sys.stdin:
    if not raw.strip():
        continue
    envelope = json.loads(raw)
    kind = envelope.get("type")
    if kind == "hello":
        lock_dir = os.path.join(os.path.expanduser("~"), ".claude", "ide")
        os.makedirs(lock_dir, exist_ok=True)
        lock_path = os.path.join(lock_dir, "40123.lock")
        with open(lock_path, "w", encoding="utf-8") as handle:
            json.dump({
                "pid": os.getpid(),
                "workspaceFolders": [envelope["workingDirectory"]],
                "ideName": "CodeyBox",
                "transport": "ws",
                "runningInWindows": False,
                "authToken": "test-token",
                "url": "ws://127.0.0.1:40123",
            }, handle)
        emit({"type": "bridge_started", "pid": os.getpid()})
        emit({"type": "ready", "port": 40123, "lockPath": lock_path})
        emit({"type": "peer_connected"})
    elif kind == "acp_send":
        payload = envelope["payload"]
        method = payload.get("method")
        emit({"type": "acp_sent", "id": payload.get("id"), "method": method})
        if method in ("session/new", "session/load"):
            session_method = method
            session_id = payload.get("params", {}).get("sessionId") or "verify-session"
            emit({"type": "acp_recv", "payload": {"jsonrpc": "2.0", "id": payload.get("id"), "result": {"sessionId": session_id}}})
        elif method == "session/prompt":
            usage = {
                "input_tokens": 10,
                "output_tokens": 3,
                "cache_read_input_tokens": 2048 if session_method == "session/load" else 0,
                "cache_creation_input_tokens": 0 if session_method == "session/load" else 2048,
            }
            emit({"type": "acp_recv", "payload": {"jsonrpc": "2.0", "id": payload.get("id"), "result": {"stopReason": "end_turn", "usage": usage}}})
            emit({"type": "turn_complete", "stopReason": "end_turn"})
            if lock_path and os.path.exists(lock_path):
                os.remove(lock_path)
            sys.exit(0)
PY
chmod 755 "$publish_dir/CodeyBox.Agents.Claude.AcpBridge"
""");
        WriteExecutable(Path.Combine(toolsDir, "ldd"), """
#!/usr/bin/env bash
set -euo pipefail
printf 'ldd' >> "$CALL_LOG"
for arg in "$@"; do printf ' %s' "$arg" >> "$CALL_LOG"; done
printf '\n' >> "$CALL_LOG"
echo "${CODEYBOX_TEST_LDD_OUT:-not a dynamic executable}"
""");
        WriteExecutable(Path.Combine(toolsDir, "claude"), """
#!/usr/bin/env bash
set -euo pipefail
printf 'claude' >> "$CALL_LOG"
for arg in "$@"; do printf ' %s' "$arg" >> "$CALL_LOG"; done
printf '\n' >> "$CALL_LOG"
if [ -z "${ANTHROPIC_API_KEY:-}" ] \
    && [ -z "${CLAUDE_CODE_OAUTH_TOKEN:-}" ] \
    && [ -z "${CODEYBOX_CLAUDE_OAUTH_JSON:-}" ]; then
    echo "missing verifier auth env" >&2
    exit 42
fi
if [ "${1:-}" = "--version" ]; then
    echo "claude test-double version"
    exit 0
fi
echo "unit-test claude stub should only be used for --version" >&2
exit 2
""");
        WriteExecutable(Path.Combine(toolsDir, "multipass"), """
#!/usr/bin/env bash
set -euo pipefail
printf 'multipass' >> "$CALL_LOG"
for arg in "$@"; do printf ' %s' "$arg" >> "$CALL_LOG"; done
printf '\n' >> "$CALL_LOG"

if [ "$#" -ge 1 ] && [ "$1" = "start" ]; then
    exit 0
fi
if [ "$#" -ge 1 ] && [ "$1" = "transfer" ]; then
    src="$2"
    remote="${3#*:}"
    mkdir -p "$(dirname "$remote")"
    cp "$src" "$remote"
    exit 0
fi
if [ "$#" -ge 4 ] && [ "$1" = "exec" ]; then
    shift 3
    case "$1" in
        mktemp)
            mkdir -p "$CODEYBOX_TEST_VM_ROOT"
            mktemp -d "$CODEYBOX_TEST_VM_ROOT/codeybox-acp-bridge-verify.XXXXXX"
            exit 0
            ;;
        chmod)
            "$@"
            exit 0
            ;;
        sh)
            script="$3"
            if [[ "$script" == *"cat > '"* ]]; then
                target="${script#*cat > \'}"
                target="${target%%\'*}"
                cat > "$target"
                exit 0
            fi
            if [[ "$script" == *"python3 "* ]]; then
                if [ "${CODEYBOX_TEST_MULTIPASS_PYTHON_EXIT:-0}" != "0" ]; then
                    echo "simulated verifier failure"
                    exit "$CODEYBOX_TEST_MULTIPASS_PYTHON_EXIT"
                fi
                if [ "${CODEYBOX_TEST_MULTIPASS_PYTHON_NO_SUCCESS:-0}" = "1" ]; then
                    echo "verifier finished without marker"
                    exit 0
                fi
                eval "$script"
                exit $?
            fi
            echo "unexpected sh script: $script" >&2
            exit 9
            ;;
        python3)
            if [ "${CODEYBOX_TEST_MULTIPASS_PYTHON_EXIT:-0}" != "0" ]; then
                echo "simulated verifier failure"
                exit "$CODEYBOX_TEST_MULTIPASS_PYTHON_EXIT"
            fi
            if [ "${CODEYBOX_TEST_MULTIPASS_PYTHON_NO_SUCCESS:-0}" = "1" ]; then
                echo "verifier finished without marker"
                exit 0
            fi
            "$@"
            exit $?
            ;;
        rm)
            "$@"
            exit 0
            ;;
    esac
fi

echo "unexpected multipass argv: $*" >&2
exit 9
""");

        return new PublishScriptFixture(
            tempRoot,
            scriptPath,
            toolsDir,
            callLog,
            Path.Combine(resourceDir, "acp-bridge"));
    }

    private static Dictionary<string, string?> PublishScriptEnv(string toolsDir, string callLog)
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = toolsDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["CALL_LOG"] = callLog,
            ["CODEYBOX_TEST_VM_ROOT"] = Path.Combine(Path.GetDirectoryName(callLog)!, "vm"),
            ["CODEYBOX_CLAUDE_API_KEY"] = "sk-ant-test-verifier",
        };
    }

    private static string CreateToolPathWithoutMultipass(PublishScriptFixture fixture)
    {
        var tools = Path.Combine(fixture.TempRoot, "tools-no-multipass");
        Directory.CreateDirectory(tools);

        foreach (var name in new[] { "dotnet", "ldd", "claude" })
        {
            var source = Path.Combine(fixture.ToolsDir, name);
            var dest = Path.Combine(tools, name);
            File.Copy(source, dest, overwrite: true);
            MakeExecutable(dest);
        }

        foreach (var name in new[] { "bash", "dirname", "mkdir", "rm", "cp", "chmod", "ls", "file", "mktemp", "cat" })
        {
            var source = RequireExecutableOnPath(name);
            var dest = Path.Combine(tools, name);
            TryLinkOrCopyExecutable(source, dest);
        }

        return tools;
    }

    private static string RequireExecutableOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Required test tool not found on PATH: " + name);
    }

    private static void TryLinkOrCopyExecutable(string source, string dest)
    {
        try
        {
            File.CreateSymbolicLink(dest, source);
        }
        catch
        {
            File.Copy(source, dest, overwrite: true);
            MakeExecutable(dest);
        }
    }

    private static void WriteExecutable(string path, string contents)
    {
        File.WriteAllText(path, contents);
        MakeExecutable(path);
    }

    private static void MakeExecutable(string path)
    {
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?> env)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.Environment.Remove("CODEYBOX_ACP_BRIDGE_VERIFY_VM");
        psi.Environment.Remove("CODEYBOX_ACP_BRIDGE_SKIP_VM_VERIFY");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in env)
        {
            if (value is null)
                psi.Environment.Remove(key);
            else
                psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start " + fileName);
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (process.ExitCode, stdout, stderr);
    }

    private sealed record PublishScriptFixture(
        string TempRoot,
        string ScriptPath,
        string ToolsDir,
        string CallLog,
        string ResourcePath);

    // ── NativeAOT static-link contract ─────────────────────────────────────────

    /// <summary>
    /// The bridge csproj sets <c>&lt;StaticExecutable&gt;true&lt;/StaticExecutable&gt;</c>
    /// + <c>&lt;PublishAot&gt;true&lt;/PublishAot&gt;</c>, producing a fully-statically
    /// linked ELF with NO <c>dlopen</c> support at runtime. The default
    /// NativeAOT PInvoke resolver falls back to <c>dlopen("libc.so")</c> on
    /// first call which throws <see cref="DllNotFoundException"/> in a fully-
    /// static binary. <c>Bridge.NativeMethods.Kill</c> P/Invokes libc's
    /// <c>kill(2)</c> for the polite SIGTERM-then-grace-then-SIGKILL teardown
    /// of <c>claude --ide</c> — the exception is caught silently by
    /// <c>TerminateClaudeProcess</c> and the SIGTERM-grace path regresses to
    /// bare SIGKILL, re-introducing the half-written-JSONL → thinking-block
    /// immutability 400 cluster the polite signal was specifically added to
    /// prevent.
    ///
    /// The fix is <c>&lt;DirectPInvoke Include="libc" /&gt;</c> in
    /// <c>CodeyBox.Agents.Claude.AcpBridge.csproj</c>, which resolves the
    /// P/Invoke at link time so the static binary does not need runtime
    /// <c>dlopen</c>. The musl C runtime comes from the linux-musl-x64 toolchain;
    /// <c>&lt;NativeLibrary Include="libc" /&gt;</c> must NOT be used because
    /// NativeAOT passes Unix NativeLibrary items as raw file inputs.
    ///
    /// This regression is invisible to the rest of the suite because every
    /// other AcpBridge fixture exercises the IL build (where libc resolves
    /// via the host's dynamic loader normally); the failure mode only
    /// manifests in the actually-published binary the sandbox executes.
    /// We can't easily exec the AOT-published ELF from the test runner
    /// (musl-tools may not be installed), so pin the csproj contents
    /// instead — a regression that drops either MSBuild item will fail
    /// this test loudly and force the operator to keep them in sync with
    /// the DllImport.
    /// </summary>
    [Fact]
    public void AcpBridge_Csproj_DeclaresDirectPInvokeAndDoesNotPassLibcAsNativeLibraryFile()
    {
        var solutionRoot = FindAncestorContaining(AppContext.BaseDirectory, "CodeyBox.slnx")
            ?? throw new InvalidOperationException(
                "Cannot locate solution root from " + AppContext.BaseDirectory +
                " — ensure CodeyBox.slnx exists in an ancestor directory.");

        var csprojPath = Path.Combine(
            solutionRoot,
            "src",
            "CodeyBox.Agents.Claude.AcpBridge",
            "CodeyBox.Agents.Claude.AcpBridge.csproj");
        Assert.True(File.Exists(csprojPath),
            "Bridge csproj missing at " + csprojPath);

        var csprojText = File.ReadAllText(csprojPath);

        Assert.Contains("<StaticExecutable>true</StaticExecutable>", csprojText);
        Assert.Contains("<PublishAot>true</PublishAot>", csprojText);
        Assert.Contains("<LinkerFlavor>lld</LinkerFlavor>", csprojText);

        Assert.Contains("<DirectPInvoke Include=\"libc\" />", csprojText);
        Assert.DoesNotContain("<NativeLibrary Include=\"libc\" />", csprojText);
    }

    [Fact]
    public async Task AcpBridge_PublishScript_SkipMultipassVerifyArgumentIsRejectedBeforePublish()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath, "--skip-multipass-verify"], env);

            Assert.Equal(64, run.ExitCode);
            Assert.Contains("Usage: scripts/publish-acp-bridge.sh", run.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ResourcePath),
                "Rejected skip arguments must not refresh the embedded bridge resource.");

            if (File.Exists(fixture.CallLog))
                Assert.Equal("", File.ReadAllText(fixture.CallLog));
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_EnvironmentSkipMultipassVerifyDoesNotBypassVmRequirement()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);
            env["CODEYBOX_ACP_BRIDGE_SKIP_VM_VERIFY"] = "1";

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("CODEYBOX_ACP_BRIDGE_VERIFY_VM must name", run.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ResourcePath),
                "Environment skip flags must not refresh the embedded bridge resource.");

            var calls = File.ReadAllText(fixture.CallLog);
            Assert.Contains("dotnet publish", calls, StringComparison.Ordinal);
            Assert.Contains("ldd", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("multipass start", calls, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_RequiresVerifyVmAndRunsMultipassVerifier()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);
            env["CODEYBOX_ACP_BRIDGE_VERIFY_VM"] = "cb-baseline-test";
            env["CODEYBOX_ACP_BRIDGE_VERIFY_MODEL"] = "claude-opus-test";
            env["CODEYBOX_ACP_BRIDGE_VERIFY_TURN_TIMEOUT_SECONDS"] = "321";
            env["API_TIMEOUT_MS"] = "456000";

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.Equal(0, run.ExitCode);
            Assert.True(File.Exists(fixture.ResourcePath),
                "The publish script should move the verified candidate into the embedded resource path.");
            Assert.Contains("Multipass ACP verification passed on cb-baseline-test", run.Stdout, StringComparison.Ordinal);

            var calls = File.ReadAllText(fixture.CallLog);
            Assert.Contains("dotnet publish", calls, StringComparison.Ordinal);
            Assert.Contains("ldd", calls, StringComparison.Ordinal);
            Assert.Contains("multipass start cb-baseline-test", calls, StringComparison.Ordinal);
            Assert.Contains("multipass transfer", calls, StringComparison.Ordinal);
            Assert.Contains("multipass exec cb-baseline-test -- sh -c . '", calls, StringComparison.Ordinal);
            Assert.Contains("python3 '", calls, StringComparison.Ordinal);
            Assert.Contains("claude --version", calls, StringComparison.Ordinal);
            Assert.Contains("CODEYBOX_ACP_BRIDGE_VERIFY_MODEL", calls, StringComparison.Ordinal);
            Assert.Contains("CODEYBOX_ACP_BRIDGE_VERIFY_TURN_TIMEOUT_SECONDS", calls, StringComparison.Ordinal);
            Assert.Contains("API_TIMEOUT_MS", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("multipass launch", calls, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_OAuthVerifierUsesTemporaryHomeAndCleansCredentials()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);
            env["CODEYBOX_ACP_BRIDGE_VERIFY_VM"] = "cb-baseline-test";
            env["CODEYBOX_CLAUDE_API_KEY"] = null;
            env["ANTHROPIC_API_KEY"] = null;
            env["CLAUDE_CODE_OAUTH_TOKEN"] = null;
            env["CODEYBOX_CLAUDE_OAUTH_JSON"] = "{\"accessToken\":\"oauth-test\"}";
            var hostHome = Path.Combine(fixture.TempRoot, "host-home");
            Directory.CreateDirectory(hostHome);
            env["HOME"] = hostHome;

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.Equal(0, run.ExitCode);
            Assert.True(File.Exists(fixture.ResourcePath));
            Assert.False(File.Exists(Path.Combine(hostHome, ".claude", ".credentials.json")),
                "The verifier must not write OAuth credentials to the reusable VM user's HOME.");
            Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(fixture.TempRoot, "vm")));
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_RejectsDynamicallyLinkedCandidateBeforeRefreshingResource()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);
            env["CODEYBOX_TEST_LDD_OUT"] = "\tlinux-vdso.so.1 (0x00007ffc00000000)\n\tlibc.so.6 => /lib/x86_64-linux-gnu/libc.so.6";

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("published binary appears dynamically linked", run.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ResourcePath),
                "A dynamically linked candidate must not replace the embedded bridge resource.");

            var calls = File.ReadAllText(fixture.CallLog);
            Assert.Contains("ldd", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("multipass", calls, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_MissingPublishedBinaryDoesNotRefreshResource()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);
            env["CODEYBOX_TEST_DOTNET_SKIP_OUTPUT"] = "1";

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("published binary not found", run.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ResourcePath),
                "A publish run that does not produce the expected binary must not refresh the embedded bridge resource.");

            var calls = File.ReadAllText(fixture.CallLog);
            Assert.Contains("dotnet publish", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("ldd", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("multipass", calls, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_RequiresMultipassWhenVmVerificationIsNotSkipped()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var toolsWithoutMultipass = CreateToolPathWithoutMultipass(fixture);
            var env = PublishScriptEnv(toolsWithoutMultipass, fixture.CallLog);
            env["PATH"] = toolsWithoutMultipass;
            env["CODEYBOX_ACP_BRIDGE_VERIFY_VM"] = "cb-baseline-test";

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("multipass is required", run.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ResourcePath));
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_RequiresVerifyVmWhenVmVerificationIsNotSkipped()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("CODEYBOX_ACP_BRIDGE_VERIFY_VM must name", run.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ResourcePath));

            var calls = File.ReadAllText(fixture.CallLog);
            Assert.Contains("ldd", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("multipass start", calls, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_RejectsMissingVerifierCredentialsBeforeMutatingVm()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);
            env["CODEYBOX_ACP_BRIDGE_VERIFY_VM"] = "cb-baseline-test";
            env["CODEYBOX_CLAUDE_API_KEY"] = null;
            env["ANTHROPIC_API_KEY"] = null;
            env["CLAUDE_CODE_OAUTH_TOKEN"] = null;
            env["CODEYBOX_CLAUDE_OAUTH_JSON"] = null;

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("VM verification requires a Claude credential", run.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ResourcePath));

            var calls = File.ReadAllText(fixture.CallLog);
            Assert.Contains("dotnet publish", calls, StringComparison.Ordinal);
            Assert.Contains("ldd", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("multipass start", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("multipass exec", calls, StringComparison.Ordinal);
            Assert.DoesNotContain("multipass transfer", calls, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_VerifierNonZeroExitDoesNotRefreshResource()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            await File.WriteAllTextAsync(fixture.ResourcePath, "previous verified bridge");
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);
            env["CODEYBOX_ACP_BRIDGE_VERIFY_VM"] = "cb-baseline-test";
            env["CODEYBOX_TEST_MULTIPASS_PYTHON_EXIT"] = "7";

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("ACP bridge end-to-end verification failed", run.Stderr, StringComparison.Ordinal);
            Assert.Contains("simulated verifier failure", run.Stderr, StringComparison.Ordinal);
            Assert.Equal("previous verified bridge", await File.ReadAllTextAsync(fixture.ResourcePath));
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_PublishScript_VerifierMissingSuccessMarkerDoesNotRefreshResource()
    {
        var fixture = CreatePublishScriptFixture();
        try
        {
            var env = PublishScriptEnv(fixture.ToolsDir, fixture.CallLog);
            env["CODEYBOX_ACP_BRIDGE_VERIFY_VM"] = "cb-baseline-test";
            env["CODEYBOX_TEST_MULTIPASS_PYTHON_NO_SUCCESS"] = "1";

            var run = await RunProcessAsync("/bin/sh", [fixture.ScriptPath], env);

            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains("ACP bridge verifier did not report success", run.Stderr, StringComparison.Ordinal);
            Assert.Contains("verifier finished without marker", run.Stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(fixture.ResourcePath));
        }
        finally
        {
            try { Directory.Delete(fixture.TempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AcpBridge_ClaudeProject_ReleaseBuildRequiresPreparedNativeResourceByDefault()
    {
        var csprojPath = ClaudeProjectPath();
        var missingResource = Path.Combine(Path.GetTempPath(), "missing-acp-bridge-" + Guid.NewGuid().ToString("N"));
        var placeholderPath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Resources", "acp-bridge.placeholder");

        var run = await RunProcessAsync("dotnet",
            [
                "msbuild",
                csprojPath,
                "-nologo",
                "-t:SelectAcpBridgeEmbeddedResource",
                "-p:Configuration=Release",
                "-p:AcpBridgeResourcePath=" + missingResource,
                "-p:AcpBridgePlaceholderResourcePath=" + placeholderPath,
            ],
            DotnetTestEnv());

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("RequireAcpBridgeNativeResource=true but no ACP bridge resource exists",
            run.Stdout + run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcpBridge_ClaudeProject_ExplicitPlaceholderOptOutSelectsPlaceholderResource()
    {
        var csprojPath = ClaudeProjectPath();
        var missingResource = Path.Combine(Path.GetTempPath(), "missing-acp-bridge-" + Guid.NewGuid().ToString("N"));
        var placeholderPath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Resources", "acp-bridge.placeholder");

        var run = await RunProcessAsync("dotnet",
            [
                "msbuild",
                csprojPath,
                "-nologo",
                "-t:SelectAcpBridgeEmbeddedResource",
                "-getItem:EmbeddedResource",
                "-p:Configuration=Release",
                "-p:RequireAcpBridgeNativeResource=false",
                "-p:AcpBridgeResourcePath=" + missingResource,
                "-p:AcpBridgePlaceholderResourcePath=" + placeholderPath,
            ],
            DotnetTestEnv());

        Assert.Equal(0, run.ExitCode);
        using var doc = JsonDocument.Parse(run.Stdout);
        var resources = doc.RootElement.GetProperty("Items").GetProperty("EmbeddedResource").EnumerateArray().ToList();
        var resource = Assert.Single(resources);
        Assert.Equal(Path.GetFullPath(placeholderPath), resource.GetProperty("Identity").GetString());
        Assert.Equal("acp-bridge", resource.GetProperty("LogicalName").GetString());
        Assert.Equal("Resources/acp-bridge.placeholder", resource.GetProperty("Link").GetString());
    }

    [Fact]
    public async Task AcpBridge_ClaudeProject_RequiredBuildSelectsPreparedNativeResource()
    {
        var csprojPath = ClaudeProjectPath();
        var tmpDir = Directory.CreateTempSubdirectory("cb-acp-resource-msbuild-").FullName;
        try
        {
            var resourcePath = Path.Combine(tmpDir, "acp-bridge");
            await File.WriteAllBytesAsync(resourcePath, [0x7f, (byte)'E', (byte)'L', (byte)'F', 2]);
            var placeholderPath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "Resources", "acp-bridge.placeholder");

            var run = await RunProcessAsync("dotnet",
                [
                    "msbuild",
                    csprojPath,
                    "-nologo",
                    "-t:SelectAcpBridgeEmbeddedResource",
                    "-getItem:EmbeddedResource",
                    "-p:Configuration=Release",
                    "-p:RequireAcpBridgeNativeResource=true",
                    "-p:AcpBridgeResourcePath=" + resourcePath,
                    "-p:AcpBridgePlaceholderResourcePath=" + placeholderPath,
                ],
                DotnetTestEnv());

            Assert.Equal(0, run.ExitCode);
            using var doc = JsonDocument.Parse(run.Stdout);
            var resources = doc.RootElement.GetProperty("Items").GetProperty("EmbeddedResource").EnumerateArray().ToList();
            var resource = Assert.Single(resources);
            Assert.Equal(Path.GetFullPath(resourcePath), resource.GetProperty("Identity").GetString());
            Assert.Equal("acp-bridge", resource.GetProperty("LogicalName").GetString());
            Assert.Equal("Resources/acp-bridge", resource.GetProperty("Link").GetString());
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AcpBridge_PreMergeRevalidateWorkflow_DoesNotRunCredentialedVerifierOnUntrustedPrCode()
    {
        var solutionRoot = FindAncestorContaining(AppContext.BaseDirectory, "CodeyBox.slnx")
            ?? throw new InvalidOperationException(
                "Cannot locate solution root from " + AppContext.BaseDirectory +
                " — ensure CodeyBox.slnx exists in an ancestor directory.");
        var workflowPath = Path.Combine(solutionRoot, ".github", "workflows", "pre-merge-revalidate.yml");
        var text = File.ReadAllText(workflowPath);

        Assert.Contains("runs-on: ubuntu-latest", text, StringComparison.Ordinal);
        Assert.DoesNotContain("runs-on: [self-hosted, multipass]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CODEYBOX_ACP_BRIDGE_VERIFY_VM", text, StringComparison.Ordinal);
        Assert.DoesNotContain("scripts/publish-acp-bridge.sh", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-p:RequireAcpBridgeNativeResource=false", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AcpBridge_SuspendResilienceWorkflow_DoesNotLetManualDispatchChooseVerifierVm()
    {
        var solutionRoot = FindAncestorContaining(AppContext.BaseDirectory, "CodeyBox.slnx")
            ?? throw new InvalidOperationException(
                "Cannot locate solution root from " + AppContext.BaseDirectory +
                " — ensure CodeyBox.slnx exists in an ancestor directory.");
        var workflowPath = Path.Combine(solutionRoot, ".github", "workflows", "agent-suspend-resilience.yml");
        var text = File.ReadAllText(workflowPath);

        Assert.Contains("vars.CODEYBOX_ACP_BRIDGE_VERIFY_VM", text, StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.acp_bridge_verify_vm", text, StringComparison.Ordinal);
        Assert.DoesNotContain("acp_bridge_verify_vm:", text, StringComparison.Ordinal);
    }

    private static string ClaudeProjectPath()
    {
        var solutionRoot = FindAncestorContaining(AppContext.BaseDirectory, "CodeyBox.slnx")
            ?? throw new InvalidOperationException(
                "Cannot locate solution root from " + AppContext.BaseDirectory +
                " — ensure CodeyBox.slnx exists in an ancestor directory.");

        var csprojPath = Path.Combine(
            solutionRoot,
            "src",
            "CodeyBox.Agents.Claude",
            "CodeyBox.Agents.Claude.csproj");
        Assert.True(File.Exists(csprojPath), "Claude csproj missing at " + csprojPath);
        return csprojPath;
    }

    private static Dictionary<string, string?> DotnetTestEnv() =>
        new(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
        };

    private static string? FindAncestorContaining(string start, string fileName)
    {
        var dir = start;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, fileName)))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}

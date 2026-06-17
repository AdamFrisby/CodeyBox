using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
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
}

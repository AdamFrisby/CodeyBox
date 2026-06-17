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

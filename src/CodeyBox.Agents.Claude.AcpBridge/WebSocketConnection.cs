using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Agents.Claude.AcpBridge;

/// <summary>
/// Hand-rolled minimal RFC6455 server. Mirrors the JS bridge's hand-rolled
/// implementation rather than reaching for <c>System.Net.WebSockets</c>:
/// keeps the NativeAOT binary small and removes the dependency on
/// <c>HttpListener</c> (which has known AOT compatibility footguns).
///
/// <para>Supports the subset claude --ide actually uses: HTTP/1.1 Upgrade
/// handshake with a CodeyBox-specified auth header check, plus text frames in
/// both directions (claude masks all client→server frames; the bridge sends
/// unmasked server→client frames as the protocol allows for servers).</para>
/// </summary>
internal sealed class WebSocketConnection
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const string AuthHeaderName = "x-claude-code-ide-authorization";

    private readonly NetworkStream _stream;
    private readonly object _writeLock = new();

    public WebSocketConnection(NetworkStream stream) { _stream = stream; }

    /// <summary>
    /// Read the inbound HTTP upgrade request, authorize against
    /// <paramref name="authToken"/>, and complete the handshake. Returns
    /// false if the request is malformed or the auth header doesn't match —
    /// callers should close the connection on false.
    /// </summary>
    public async Task<bool> AcceptHandshakeAsync(string authToken, CancellationToken ct)
    {
        var request = await ReadHttpRequestAsync(ct).ConfigureAwait(false);
        if (request is null)
        {
            await WriteRawAsync("HTTP/1.1 400 Bad Request\r\n\r\n", ct).ConfigureAwait(false);
            return false;
        }

        if (!request.Headers.TryGetValue("sec-websocket-key", out var wsKey)
            || string.IsNullOrEmpty(wsKey))
        {
            await WriteRawAsync("HTTP/1.1 400 Bad Request\r\n\r\n", ct).ConfigureAwait(false);
            return false;
        }

        if (!string.IsNullOrEmpty(authToken))
        {
            var supplied = "";
            if (request.Headers.TryGetValue(AuthHeaderName, out var a)) supplied = a;
            else if (request.Headers.TryGetValue("authorization", out var b)) supplied = b;
            if (supplied != authToken && !supplied.EndsWith(authToken, StringComparison.Ordinal))
            {
                await WriteRawAsync("HTTP/1.1 401 Unauthorized\r\n\r\n", ct).ConfigureAwait(false);
                return false;
            }
        }

        var accept = ComputeAcceptKey(wsKey);
        var reply =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
        await WriteRawAsync(reply, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Read frames until the connection is closed or cancellation fires.
    /// Each complete TEXT/BINARY frame is delivered to <paramref name="onText"/>
    /// as a UTF-8 decoded string. CLOSE frames cause a graceful end of stream.
    /// </summary>
    public async Task ReceiveLoopAsync(Action<string> onText, CancellationToken ct)
    {
        var buf = new byte[8192];
        var acc = new List<byte>(8192);
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (IOException) { return; }
            catch (ObjectDisposedException) { return; }
            if (n <= 0) return;
            for (int i = 0; i < n; i++) acc.Add(buf[i]);

            while (TryParseFrame(acc, out var op, out var payload))
            {
                if (op == 0x8) return; // close
                if (op == 0x1 || op == 0x2)
                {
                    var text = Encoding.UTF8.GetString(payload);
                    try { onText(text); } catch { /* swallow downstream errors */ }
                }
                // ignore ping/pong/continuation
            }
        }
    }

    /// <summary>
    /// Send a single text frame, unmasked (server → client is allowed to be
    /// unmasked per RFC6455). Length is encoded using the shortest of the
    /// three RFC6455 length forms.
    /// </summary>
    public void SendText(string text)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        var frame = BuildTextFrame(payload);
        lock (_writeLock)
        {
            try { _stream.Write(frame, 0, frame.Length); _stream.Flush(); }
            catch (IOException) { /* peer closed */ }
            catch (ObjectDisposedException) { /* peer closed */ }
        }
    }

    public void Close()
    {
        try { _stream.Close(); } catch { }
    }

    private static byte[] BuildTextFrame(byte[] payload)
    {
        int len = payload.Length;
        byte[] header;
        if (len < 126)
        {
            header = new byte[] { 0x81, (byte)len };
        }
        else if (len < 65536)
        {
            header = new byte[4];
            header[0] = 0x81;
            header[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2, 2), (ushort)len);
        }
        else
        {
            header = new byte[10];
            header[0] = 0x81;
            header[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(2, 8), (ulong)len);
        }
        var frame = new byte[header.Length + len];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(payload, 0, frame, header.Length, len);
        return frame;
    }

    private static bool TryParseFrame(List<byte> acc, out int opcode, out byte[] payload)
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
        if ((long)acc.Count < (long)offset + len) return false;
        var raw = new byte[len];
        for (long i = 0; i < len; i++) raw[i] = acc[offset + (int)i];
        if (masked)
        {
            var mask = new byte[4];
            for (int i = 0; i < 4; i++) mask[i] = acc[maskStart + i];
            for (long i = 0; i < len; i++) raw[i] ^= mask[i % 4];
        }
        acc.RemoveRange(0, offset + (int)len);
        payload = raw;
        return true;
    }

    private static string ComputeAcceptKey(string clientKey)
    {
        var bytes = Encoding.ASCII.GetBytes(clientKey + WebSocketGuid);
        var hash = SHA1.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private async Task<HttpRequest?> ReadHttpRequestAsync(CancellationToken ct)
    {
        var buf = new byte[4096];
        var acc = new List<byte>(4096);
        while (!ct.IsCancellationRequested)
        {
            int n;
            try
            {
                n = await _stream.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            if (n <= 0) return null;
            for (int i = 0; i < n; i++) acc.Add(buf[i]);
            // header terminator
            for (int i = 3; i < acc.Count; i++)
            {
                if (acc[i - 3] == (byte)'\r' && acc[i - 2] == (byte)'\n'
                    && acc[i - 1] == (byte)'\r' && acc[i] == (byte)'\n')
                {
                    return HttpRequest.Parse(Encoding.ASCII.GetString(acc.ToArray(), 0, i + 1));
                }
            }
            if (acc.Count > 16 * 1024) return null; // header too large
        }
        return null;
    }

    private async Task WriteRawAsync(string text, CancellationToken ct)
    {
        // Mirror SendText/ReceiveLoopAsync: swallow IOException /
        // ObjectDisposedException from a peer that tore the TCP connection
        // down mid-handshake. The 400/401 error-branch callers in
        // AcceptHandshakeAsync await us without their own try/catch, so an
        // unhandled IOException here propagates out of HandleClientAsync's
        // fire-and-forget Task.Run (whose only catch is for
        // OperationCanceledException) and surfaces as an
        // UnobservedTaskException — under the default unobserved-exception
        // policy that crashes the bridge.
        var bytes = Encoding.ASCII.GetBytes(text);
        try
        {
            await _stream.WriteAsync(bytes.AsMemory(), ct).ConfigureAwait(false);
        }
        catch (IOException) { /* peer torn down mid-handshake */ }
        catch (ObjectDisposedException) { /* peer closed */ }
    }

    private sealed record HttpRequest(IReadOnlyDictionary<string, string> Headers)
    {
        public static HttpRequest? Parse(string raw)
        {
            var lines = raw.Split("\r\n");
            if (lines.Length < 2) return null;
            var hdrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) break;
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var name = line.AsSpan(0, idx).Trim().ToString();
                var value = line.AsSpan(idx + 1).Trim().ToString();
                hdrs[name] = value;
            }
            return new HttpRequest(hdrs);
        }
    }
}

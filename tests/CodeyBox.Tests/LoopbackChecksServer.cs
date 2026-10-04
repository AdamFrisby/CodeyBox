using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CodeyBox.Tests;

/// <summary>
/// Minimal controllable HTTP/1.1 test server over loopback TCP. Lets
/// Checks-API tests drive a real <see cref="HttpClient"/> (real framing,
/// status lines, headers like <c>Retry-After</c>, and connection failures)
/// with scripted per-request responses — stronger than a handler stub for
/// protocol and transaction guarantees, with no live-network dependence.
/// </summary>
internal sealed class LoopbackChecksServer : IAsyncDisposable
{
    public sealed record RecordedRequest(
        string Method,
        string PathAndQuery,
        Dictionary<string, string> Headers,
        string Body);

    public sealed record ScriptedResponse(
        int StatusCode,
        string Body,
        Dictionary<string, string>? Headers = null,
        ServerBehavior Behavior = ServerBehavior.Respond);

    public enum ServerBehavior
    {
        Respond,
        CloseWithoutResponse,
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<Task> _connections = [];
    private readonly object _scriptLock = new();
    private readonly Queue<Func<RecordedRequest, ScriptedResponse>> _script = new();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public Uri BaseUri { get; }

    private LoopbackChecksServer(TcpListener listener)
    {
        _listener = listener;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{port}");
        _acceptLoop = AcceptLoopAsync(_cts.Token);
    }

    public static LoopbackChecksServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackChecksServer(listener);
    }

    public void EnqueueJson(int status, string json, Dictionary<string, string>? headers = null) =>
        Enqueue(_ => new ScriptedResponse(status, json, headers));

    public void Enqueue(Func<RecordedRequest, ScriptedResponse> responder)
    {
        lock (_scriptLock)
            _script.Enqueue(responder);
    }

    public void EnqueueCloseWithoutResponse() =>
        Enqueue(_ => new ScriptedResponse(0, string.Empty, Behavior: ServerBehavior.CloseWithoutResponse));

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            lock (_connections)
                _connections.Add(HandleAsync(client, ct));
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                RecordedRequest? request;
                try
                {
                    request = await ReadRequestAsync(stream, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break;
                }
                if (request is null)
                    break;
                Requests.Enqueue(request);

                Func<RecordedRequest, ScriptedResponse>? responder;
                lock (_scriptLock)
                    _script.TryDequeue(out responder);
                var scripted = responder?.Invoke(request)
                    ?? new ScriptedResponse(200, "{}");

                if (scripted.Behavior == ServerBehavior.CloseWithoutResponse)
                    break;

                try
                {
                    await WriteResponseAsync(stream, scripted, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break;
                }
                // One request per connection keeps framing trivially correct.
                break;
            }
        }
    }

    private static async Task<RecordedRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var headerBytes = new List<byte>();
        var buffer = new byte[1];
        var terminator = new byte[] { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
        while (headerBytes.Count < 65_536)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0)
                return headerBytes.Count == 0 ? null : throw new InvalidOperationException("Connection closed mid-headers.");
            headerBytes.Add(buffer[0]);
            if (headerBytes.Count >= 4 &&
                headerBytes[^4] == terminator[0] && headerBytes[^3] == terminator[1] &&
                headerBytes[^2] == terminator[2] && headerBytes[^1] == terminator[3])
                break;
        }
        var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
        var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            throw new InvalidOperationException("Malformed request line.");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthValue))
            int.TryParse(lengthValue, out contentLength);
        var isChunked = headers.TryGetValue("Transfer-Encoding", out var encoding) &&
            encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        if (headers.TryGetValue("Expect", out var expect) &&
            expect.Contains("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
            await stream.WriteAsync(interim, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        string bodyText;
        if (isChunked)
        {
            bodyText = await ReadChunkedBodyAsync(stream, ct).ConfigureAwait(false);
        }
        else
        {
            contentLength = Math.Min(contentLength, 4_194_304);
            var body = new byte[contentLength];
            var received = 0;
            while (received < contentLength)
            {
                var read = await stream.ReadAsync(body.AsMemory(received, contentLength - received), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                received += read;
            }
            bodyText = Encoding.UTF8.GetString(body, 0, received);
        }
        return new RecordedRequest(
            requestLine[0],
            requestLine[1],
            headers,
            bodyText);
    }

    private static async Task<string> ReadChunkedBodyAsync(NetworkStream stream, CancellationToken ct)
    {
        using var collected = new MemoryStream();
        while (true)
        {
            var sizeLine = await ReadLineAsync(stream, ct).ConfigureAwait(false);
            var semicolon = sizeLine.IndexOf(';');
            var sizeText = (semicolon >= 0 ? sizeLine[..semicolon] : sizeLine).Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.HexNumber, null, out var size))
                throw new InvalidOperationException($"Malformed chunk size: '{sizeText}'.");
            if (size == 0)
            {
                // Consume trailers + final CRLF.
                string trailer;
                do
                {
                    trailer = await ReadLineAsync(stream, ct).ConfigureAwait(false);
                } while (!string.IsNullOrEmpty(trailer));
                break;
            }
            if (size > 4_194_304)
                throw new InvalidOperationException("Chunk exceeds the 4 MB test cap.");
            var chunk = new byte[size];
            var received = 0;
            while (received < size)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(received, size - received), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidOperationException("Connection closed mid-chunk.");
                received += read;
            }
            collected.Write(chunk, 0, received);
            var crlf = new byte[2];
            var crlfReceived = 0;
            while (crlfReceived < 2)
            {
                var read = await stream.ReadAsync(crlf.AsMemory(crlfReceived, 2 - crlfReceived), ct).ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidOperationException("Connection closed mid-chunk terminator.");
                crlfReceived += read;
            }
        }
        return Encoding.UTF8.GetString(collected.ToArray());
    }

    private static async Task<string> ReadLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var line = new List<byte>();
        while (line.Count < 16_384)
        {
            var one = new byte[1];
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (read == 0)
                throw new InvalidOperationException("Connection closed mid-line.");
            if (one[0] == (byte)'\n')
                break;
            if (one[0] != (byte)'\r')
                line.Add(one[0]);
        }
        return Encoding.ASCII.GetString(line.ToArray());
    }

    private static async Task WriteResponseAsync(NetworkStream stream, ScriptedResponse response, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {response.StatusCode} {Reason(response.StatusCode)}\r\n");
        sb.Append("Content-Type: application/json\r\n");
        sb.Append($"Content-Length: {body.Length}\r\n");
        sb.Append("Connection: close\r\n");
        if (response.Headers is not null)
            foreach (var (key, value) in response.Headers)
                sb.Append($"{key}: {value}\r\n");
        sb.Append("\r\n");
        var header = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        Task[] connections;
        lock (_connections)
            connections = _connections.ToArray();
        try
        {
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        _cts.Dispose();
    }
}

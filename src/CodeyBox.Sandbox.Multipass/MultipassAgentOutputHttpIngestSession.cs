using System.Buffers;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Sandbox.Multipass;

internal sealed class MultipassAgentOutputHttpIngestSession : IAsyncDisposable
{
    internal const string UrlEnvironmentVariable = "CODEYBOX_AGENT_OUTPUT_URL";
    internal const string TokenEnvironmentVariable = "CODEYBOX_AGENT_OUTPUT_TOKEN";
    internal const string RunIdEnvironmentVariable = "CODEYBOX_AGENT_OUTPUT_RUN_ID";
    internal const int MaxChunkBytes = 256 * 1024;
    internal const int MaxRequestsPerSecond = 2048;

    private const int TokenBytes = 32;
    private const int MinPort = 20000;
    private const int MaxPort = 60999;
    private const string PathPrefix = "codeybox-agent-output";

    private readonly HttpListener _listener;
    private readonly ILogger _log;
    private readonly byte[] _expectedTokenHash;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;
    private readonly object _rateGate = new();
    private readonly StreamState _stdout;
    private readonly StreamState _stderr;
    private DateTimeOffset _rateWindowStart = DateTimeOffset.UtcNow;
    private int _rateWindowCount;
    private bool _disposed;

    private MultipassAgentOutputHttpIngestSession(
        HttpListener listener,
        string baseUrl,
        string runId,
        string token,
        ILogger log,
        Action<string>? stdoutChunkCallback,
        Action<string>? stderrChunkCallback)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        RunId = runId;
        Token = token;
        _log = log;
        _expectedTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        _stdout = new StreamState(stdoutChunkCallback);
        _stderr = new StreamState(stderrChunkCallback);
        _listenTask = Task.Run(ListenAsync);
    }

    public string BaseUrl { get; }
    public string RunId { get; }
    public string Token { get; }
    public bool ReceivedAgentBytes => _stdout.BytesReceived > 0 || _stderr.BytesReceived > 0;
    public string Stdout => _stdout.Text;
    public string Stderr => _stderr.Text;

    public static async Task<MultipassAgentOutputHttpIngestSession?> TryStartAsync(
        IPAddress bindAddress,
        string runId,
        ILogger log,
        Action<string>? stdoutChunkCallback,
        Action<string>? stderrChunkCallback,
        CancellationToken ct)
    {
        if (bindAddress.AddressFamily != AddressFamily.InterNetwork)
            return null;

        var token = GenerateToken();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var port = RandomNumberGenerator.GetInt32(MinPort, MaxPort + 1);
            var host = bindAddress.ToString();
            var prefix = $"http://{host}:{port}/{PathPrefix}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return new MultipassAgentOutputHttpIngestSession(
                    listener,
                    prefix.TrimEnd('/'),
                    runId,
                    token,
                    log,
                    stdoutChunkCallback,
                    stderrChunkCallback);
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                if (IsAddressInUse(ex))
                    continue;
                log.LogDebug(ex, "Agent output HTTP ingest listener could not bind to {Address}", prefix);
                return null;
            }
            catch (SocketException ex)
            {
                listener.Close();
                if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                    continue;
                log.LogDebug(ex, "Agent output HTTP ingest listener could not bind to {Address}", prefix);
                return null;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return null;
    }

    public static IPAddress? TryResolveBridgeAddress(string bridgeName)
    {
        if (string.IsNullOrWhiteSpace(bridgeName))
            return null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!string.Equals(nic.Name, bridgeName, StringComparison.Ordinal))
                continue;

            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                    return address.Address;
            }
        }

        return null;
    }

    public IReadOnlyDictionary<string, string> BuildEnvironment()
        => new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [UrlEnvironmentVariable] = BaseUrl,
            [TokenEnvironmentVariable] = Token,
            [RunIdEnvironmentVariable] = RunId,
        };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { await _listenTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        _stdout.FlushDecoder();
        _stderr.FlushDecoder();
        _cts.Dispose();
    }

    private static bool IsAddressInUse(HttpListenerException ex)
        => ex.ErrorCode is 32 or 48 or 98 or 10048;

    private static string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (HttpListenerException) when (!_listener.IsListening) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Agent output HTTP ingest listener failed while accepting a request");
                continue;
            }

            try
            {
                await HandleAsync(context, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Agent output HTTP ingest request failed");
                TrySetStatus(context.Response, (int)HttpStatusCode.InternalServerError);
                TryClose(context.Response);
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;

        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            Reject(response, HttpStatusCode.MethodNotAllowed);
            return;
        }

        if (!TokenMatches(request.Headers["Authorization"]))
        {
            Reject(response, HttpStatusCode.Unauthorized);
            return;
        }

        var parts = request.Url?.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 4 }
            || !string.Equals(parts[0], PathPrefix, StringComparison.Ordinal)
            || !string.Equals(Uri.UnescapeDataString(parts[1]), RunId, StringComparison.Ordinal))
        {
            Reject(response, HttpStatusCode.Forbidden);
            return;
        }

        if (!AllowRequest())
        {
            Reject(response, (HttpStatusCode)429);
            return;
        }

        var streamName = Uri.UnescapeDataString(parts[2]);
        if (string.Equals(streamName, "ready", StringComparison.Ordinal))
        {
            Reject(response, HttpStatusCode.NoContent);
            return;
        }

        if (!long.TryParse(parts[3], out var seq) || seq < 0)
        {
            Reject(response, HttpStatusCode.BadRequest);
            return;
        }

        var stream = streamName switch
        {
            "stdout" => _stdout,
            "stderr" => _stderr,
            _ => null,
        };
        if (stream is null)
        {
            Reject(response, HttpStatusCode.BadRequest);
            return;
        }

        var outcome = await stream.AppendAsync(seq, request.InputStream, MaxChunkBytes, ct).ConfigureAwait(false);
        Reject(response, outcome switch
        {
            AppendOutcome.Appended => HttpStatusCode.OK,
            AppendOutcome.Duplicate => HttpStatusCode.OK,
            AppendOutcome.OutOfOrder => HttpStatusCode.Conflict,
            AppendOutcome.TooLarge => HttpStatusCode.RequestEntityTooLarge,
            _ => HttpStatusCode.InternalServerError,
        });
    }

    private bool TokenMatches(string? authorization)
    {
        const string bearerPrefix = "Bearer ";
        var provided = authorization is not null
            && authorization.StartsWith(bearerPrefix, StringComparison.Ordinal)
                ? authorization[bearerPrefix.Length..]
                : string.Empty;
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        return CryptographicOperations.FixedTimeEquals(providedHash, _expectedTokenHash)
            && provided.Length == Token.Length;
    }

    private bool AllowRequest()
    {
        lock (_rateGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _rateWindowStart >= TimeSpan.FromSeconds(1))
            {
                _rateWindowStart = now;
                _rateWindowCount = 0;
            }

            _rateWindowCount++;
            return _rateWindowCount <= MaxRequestsPerSecond;
        }
    }

    private static void Reject(HttpListenerResponse response, HttpStatusCode status)
    {
        TrySetStatus(response, (int)status);
        TryClose(response);
    }

    private static void TrySetStatus(HttpListenerResponse response, int statusCode)
    {
        try { response.StatusCode = statusCode; } catch { }
    }

    private static void TryClose(HttpListenerResponse response)
    {
        try { response.Close(); } catch { }
    }

    private enum AppendOutcome
    {
        Appended,
        Duplicate,
        OutOfOrder,
        TooLarge,
    }

    private sealed class StreamState
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _text = new();
        private readonly Action<string>? _callback;
        private long _nextSeq;

        public StreamState(Action<string>? callback)
        {
            _callback = callback;
        }

        public long BytesReceived { get; private set; }
        public string Text => _text.ToString();

        public async Task<AppendOutcome> AppendAsync(
            long seq,
            Stream body,
            int maxBytes,
            CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (seq < _nextSeq)
                {
                    await DrainWithLimitAsync(body, maxBytes, ct).ConfigureAwait(false);
                    return AppendOutcome.Duplicate;
                }

                if (seq != _nextSeq)
                    return AppendOutcome.OutOfOrder;

                var outcome = await AppendBodyAsync(body, maxBytes, ct).ConfigureAwait(false);
                if (outcome == AppendOutcome.Appended)
                    _nextSeq++;
                return outcome;
            }
            finally
            {
                _gate.Release();
            }
        }

        public void FlushDecoder()
        {
            Span<char> chars = stackalloc char[8];
            _decoder.Convert(ReadOnlySpan<byte>.Empty, chars, true, out _, out var charsUsed, out _);
            if (charsUsed <= 0)
                return;
            var text = new string(chars[..charsUsed]);
            _text.Append(text);
            _callback?.Invoke(text);
        }

        private async Task<AppendOutcome> AppendBodyAsync(Stream body, int maxBytes, CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            using var memory = new MemoryStream(capacity: Math.Min(maxBytes, 32 * 1024));
            var total = 0;
            try
            {
                while (true)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0)
                        return AppendBufferedBody(memory.ToArray());

                    total += read;
                    if (total > maxBytes)
                    {
                        await DrainWithLimitAsync(body, maxBytes, ct).ConfigureAwait(false);
                        return AppendOutcome.TooLarge;
                    }

                    memory.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private AppendOutcome AppendBufferedBody(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0)
                return AppendOutcome.Appended;

            var charBuffer = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(bytes.Length));
            try
            {
                BytesReceived += bytes.Length;
                _decoder.Convert(
                    bytes,
                    charBuffer,
                    flush: false,
                    out _,
                    out var charsUsed,
                    out _);
                if (charsUsed == 0)
                    return AppendOutcome.Appended;

                var text = new string(charBuffer.AsSpan(0, charsUsed));
                _text.Append(text);
                _callback?.Invoke(text);
                return AppendOutcome.Appended;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(charBuffer);
            }
        }

        private static async Task DrainWithLimitAsync(Stream body, int maxBytes, CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
            var total = 0;
            try
            {
                while (true)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read == 0)
                        return;
                    total += read;
                    if (total > maxBytes)
                        return;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.InfisicalPlugin;

/// <summary>
/// One brokered credential held proxy-side: the value, where it may be
/// used, and how long it lives. Values live only here (host memory) and in
/// the upstream request the broker itself makes — never in the guest
/// environment, files, or logs.
/// </summary>
public sealed record InfisicalBrokerEntry
{
    public required string LeaseId { get; init; }
    public required string Value { get; init; }
    public required string UpstreamBaseUrl { get; init; }
    public required string InjectHeader { get; init; }
    public required string InjectScheme { get; init; }
    public required IReadOnlyList<string> AllowedPaths { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
/// Agent Proxy for brokered Infisical secrets. A loopback-only HTTP server:
/// the guest calls <c>{base}/v1/proxy/{leaseId}/{path}</c> with its own
/// request, and the broker forwards it to the mapped upstream with the
/// credential attached server-side, streaming the upstream response back.
/// The workload uses the credential without ever receiving its value — the
/// sandbox environment carries only the endpoint URL.
/// <para>Authentication to the endpoint is the unguessable lease id in the
/// path (128 bits of randomness or the server lease uuid) plus the
/// loopback-only bind (network position). There is deliberately no token in
/// the environment beyond the endpoint URL itself.</para>
/// <para>Separable from retrieval: the server starts lazily on the first
/// brokered issue and only when the operator enabled it. Direct leases
/// never touch this class.</para>
/// </summary>
public sealed class InfisicalBrokerServer : IDisposable
{
    private const string ProxyPrefix = "/v1/proxy/";

    /// <summary>How long a request waits for a forward slot before the broker answers 503.</summary>
    private const int AcquireTimeoutSeconds = 5;

    private static readonly HashSet<string> AllowedMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE",
    };

    private static readonly HashSet<string> StrippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Proxy-Authorization", "Connection", "Keep-Alive",
        "Transfer-Encoding", "Upgrade", "Trailer", "TE",
    };

    private readonly HttpClient _forward;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly int _maxBodyBytes;
    private readonly int _maxPathChars;
    private readonly SemaphoreSlim _gate;
    private readonly Dictionary<string, InfisicalBrokerEntry> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _inflight = [];
    private readonly object _lock = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>Actual bound port (an ephemeral port when 0 was requested).</summary>
    public int ActualPort { get; private set; }

    public bool Running => _loop is not null && !_loop.IsCompleted;

    public InfisicalBrokerServer(
        HttpClient forwardClient,
        TimeProvider? clock = null,
        ILogger? log = null,
        int maxBodyBytes = 1024 * 1024,
        int maxConcurrency = 16,
        int maxPathChars = 512)
    {
        _forward = forwardClient ?? throw new ArgumentNullException(nameof(forwardClient));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _maxBodyBytes = Math.Max(maxBodyBytes, 1024);
        _maxPathChars = Math.Clamp(maxPathChars, 64, 4096);
        _gate = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 256), Math.Clamp(maxConcurrency, 1, 256));
    }

    /// <summary>
    /// Starts the loopback listener. Idempotent: a running server ignores
    /// repeat starts so options hot-reloads cannot double-bind the port.
    /// </summary>
    public void Start(string bindHost, int bindPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindHost);
        if (Running)
            return;
        lock (_lock)
        {
            if (_listener is not null)
                return;
            // HttpListener cannot report an ephemeral port, so resolve port
            // 0 through a momentary TcpListener probe. The probe/close/bind
            // window can theoretically lose a race for the port; retry a few
            // times and fail loudly rather than serving on a wrong socket.
            HttpListener? listener = null;
            var port = bindPort;
            Exception? lastError = null;
            for (var attempt = 0; attempt < 5 && listener is null; attempt++)
            {
                if (port == 0)
                    port = ProbeFreePort(bindHost);
                try
                {
                    var candidate = new HttpListener();
                    candidate.Prefixes.Add($"http://{bindHost}:{port}/");
                    candidate.Start();
                    listener = candidate;
                }
                catch (HttpListenerException ex)
                {
                    lastError = ex;
                    port = 0;
                }
            }
            if (listener is null)
                throw new InvalidOperationException(
                    $"Infisical broker could not bind {bindHost}:{bindPort} after 5 attempts.", lastError);
            ActualPort = port;
            _listener = listener;
            _cts = new CancellationTokenSource();
            _loop = AcceptLoopAsync(_cts.Token);
            _log.LogInformation(
                "Infisical broker listening on {Host}:{Port} (loopback proxy; values never leave host memory).",
                bindHost, ActualPort);
        }
    }

    private static int ProbeFreePort(string bindHost)
    {
        var address = string.Equals(bindHost, "localhost", StringComparison.OrdinalIgnoreCase)
            ? System.Net.IPAddress.Loopback
            : System.Net.IPAddress.TryParse(bindHost, out var parsed)
                ? parsed
                : System.Net.IPAddress.Loopback;
        using var probe = new System.Net.Sockets.TcpListener(address, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Register(InfisicalBrokerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.LeaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Value);
        lock (_lock)
            _entries[entry.LeaseId] = entry;
        _log.LogInformation(
            "Infisical broker registered endpoint for lease '{LeaseId}'.", entry.LeaseId);
    }

    public bool Unregister(string leaseId)
    {
        lock (_lock)
            return _entries.Remove(leaseId);
    }

    /// <summary>Extends a brokered entry; false when the entry is gone.</summary>
    public bool Extend(string leaseId, DateTimeOffset expiresAt)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(leaseId, out var entry))
                return false;
            _entries[leaseId] = entry with { ExpiresAt = expiresAt };
            return true;
        }
    }

    private InfisicalBrokerEntry? TryGet(string leaseId)
    {
        lock (_lock)
            return _entries.TryGetValue(leaseId, out var entry) ? entry : null;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null)
            return;
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Infisical broker accept failed; continuing.");
                continue;
            }
            // Tracked, never fire-and-forget: Dispose waits for every
            // in-flight request, and the continuation observes faults.
            var pending = HandleAsync(context, ct);
            lock (_lock)
                _inflight.Add(pending);
            _ = pending.ContinueWith(
                t =>
                {
                    lock (_lock)
                        _inflight.Remove(pending);
                    if (t.IsFaulted)
                        _log.LogWarning(t.Exception, "Infisical broker request faulted.");
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var acquired = await _gate.WaitAsync(TimeSpan.FromSeconds(AcquireTimeoutSeconds), ct).ConfigureAwait(false);
        if (!acquired)
        {
            await WriteErrorAsync(context.Response, 503, "broker busy", ct).ConfigureAwait(false);
            return;
        }
        try
        {
            await HandleGatedAsync(context, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Never leak internals (or values) to the guest: a fixed shape.
            _log.LogWarning(ex, "Infisical broker request failed.");
            try { await WriteErrorAsync(context.Response, 502, "broker forward failed", ct).ConfigureAwait(false); }
            catch (Exception) { }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task HandleGatedAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;
        var rawPath = request.Url?.AbsolutePath ?? string.Empty;
        if (rawPath.Length > _maxPathChars)
        {
            await WriteErrorAsync(context.Response, 414, "path too long", ct).ConfigureAwait(false);
            return;
        }
        if (!AllowedMethods.Contains(request.HttpMethod))
        {
            await WriteErrorAsync(context.Response, 405, "method not allowed", ct).ConfigureAwait(false);
            return;
        }
        if (!rawPath.StartsWith(ProxyPrefix, StringComparison.Ordinal))
        {
            await WriteErrorAsync(context.Response, 404, "unknown broker path", ct).ConfigureAwait(false);
            return;
        }
        var remainder = rawPath[ProxyPrefix.Length..];
        var slash = remainder.IndexOf('/');
        var leaseId = slash < 0 ? remainder : remainder[..slash];
        var upstreamPath = slash < 0 ? "/" : remainder[slash..];
        if (string.IsNullOrWhiteSpace(leaseId))
        {
            await WriteErrorAsync(context.Response, 404, "unknown broker path", ct).ConfigureAwait(false);
            return;
        }

        var entry = TryGet(leaseId);
        if (entry is null)
        {
            await WriteErrorAsync(context.Response, 404, "lease revoked or unknown", ct).ConfigureAwait(false);
            return;
        }
        if (_clock.GetUtcNow() >= entry.ExpiresAt)
        {
            Unregister(leaseId);
            _log.LogInformation("Infisical broker expired lease '{LeaseId}'.", leaseId);
            await WriteErrorAsync(context.Response, 410, "lease expired", ct).ConfigureAwait(false);
            return;
        }
        if (entry.AllowedPaths.Count > 0
            && !entry.AllowedPaths.Contains(upstreamPath, StringComparer.Ordinal))
        {
            _log.LogWarning(
                "Infisical broker denied lease '{LeaseId}': path not in the exact-match allowlist.", leaseId);
            await WriteErrorAsync(context.Response, 403, "path not allowed", ct).ConfigureAwait(false);
            return;
        }

        var target = CombineUpstream(entry.UpstreamBaseUrl, upstreamPath, request.Url?.Query ?? string.Empty);
        if (target is null)
        {
            await WriteErrorAsync(context.Response, 400, "bad upstream path", ct).ConfigureAwait(false);
            return;
        }

        byte[]? body = null;
        if (request.HasEntityBody)
        {
            body = await ReadBoundedBodyAsync(request, ct).ConfigureAwait(false);
            if (body is null)
            {
                await WriteErrorAsync(context.Response, 413, "request body too large", ct).ConfigureAwait(false);
                return;
            }
        }

        using var forward = new HttpRequestMessage(new HttpMethod(request.HttpMethod), target);
        foreach (string headerName in request.Headers)
        {
            if (StrippedRequestHeaders.Contains(headerName)
                || string.Equals(headerName, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase)
                || string.Equals(headerName, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            try { forward.Headers.TryAddWithoutValidation(headerName, request.Headers[headerName]); }
            catch (InvalidOperationException) { }
        }
        if (body is not null)
        {
            forward.Content = new ByteArrayContent(body);
            if (!string.IsNullOrEmpty(request.ContentType))
            {
                try { forward.Content.Headers.ContentType =
                    System.Net.Http.Headers.MediaTypeHeaderValue.Parse(request.ContentType); }
                catch (FormatException) { }
            }
        }

        var injected = string.IsNullOrWhiteSpace(entry.InjectScheme)
            ? entry.Value
            : entry.InjectScheme + " " + entry.Value;
        forward.Headers.TryAddWithoutValidation(entry.InjectHeader, injected);

        using var upstream = await _forward.SendAsync(
            forward, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (upstream.RequestMessage?.RequestUri is { } finalUpstream
            && !InfisicalHttpClients.IsSameOrigin(finalUpstream, target))
        {
            // The forward client followed an upstream redirect (only
            // possible with an externally supplied following client —
            // the plugin builds non-following ones). The credential was
            // re-sent off-origin, so fail loudly instead of trusting
            // the body, and leave the entry to expire on its own clock.
            _log.LogError(
                "Infisical broker for lease '{LeaseId}' was redirected off-origin; refusing the upstream response.",
                leaseId);
            await WriteErrorAsync(context.Response, 502, "upstream redirect refused", ct).ConfigureAwait(false);
            return;
        }
        if (InfisicalHttpClients.IsRedirect(upstream.StatusCode))
        {
            // Never follow: the upstream 3xx passes through to the guest
            // untouched (no Location forwarding, no credential attached
            // anywhere else) so the guest sees a truthful status.
            _log.LogDebug(
                "Infisical broker for lease '{LeaseId}' passing through upstream redirect {Status} without following.",
                leaseId, (int)upstream.StatusCode);
        }
        var response = context.Response;
        response.StatusCode = (int)upstream.StatusCode;
        if (upstream.Content.Headers.ContentType is not null)
            response.ContentType = upstream.Content.Headers.ContentType.ToString();
        var responseBody = await ReadBoundedBytesAsync(
            await upstream.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        if (responseBody is null)
        {
            await WriteErrorAsync(context.Response, 502, "upstream response too large", ct).ConfigureAwait(false);
            return;
        }
        response.ContentLength64 = responseBody.Length;
        await response.OutputStream.WriteAsync(responseBody, ct).ConfigureAwait(false);
        response.Close();
        _log.LogDebug(
            "Infisical broker forwarded lease '{LeaseId}' {Method} -> {Status}.",
            leaseId, request.HttpMethod, (int)upstream.StatusCode);
    }

    private async Task<byte[]?> ReadBoundedBodyAsync(HttpListenerRequest request, CancellationToken ct)
    {
        if (request.ContentLength64 > _maxBodyBytes)
            return null;
        using var input = request.InputStream;
        return await ReadBoundedBytesAsync(input, ct).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadBoundedBytesAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var sink = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sink.Write(buffer, 0, read);
            if (sink.Length > _maxBodyBytes)
                return null;
        }
        return sink.ToArray();
    }

    private static Uri? CombineUpstream(string baseUrl, string path, string query)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
            return null;
        var trimmed = path.TrimStart('/');
        var combined = new Uri(baseUri, trimmed + query);
        // The base is a fixed prefix, so the host cannot change; verify
        // anyway so a future refactor cannot turn this into an open proxy.
        if (!string.Equals(combined.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || combined.Scheme != baseUri.Scheme
            || combined.Port != baseUri.Port)
            return null;
        return combined;
    }

    private static async Task WriteErrorAsync(HttpListenerResponse response, int status, string error, CancellationToken ct)
    {
        response.StatusCode = status;
        response.ContentType = "application/json";
        var body = JsonSerializer.SerializeToUtf8Bytes(new { error });
        response.ContentLength64 = body.Length;
        try { await response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false); }
        catch (Exception) { }
        finally { response.Close(); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try { _cts?.Cancel(); } catch (Exception) { }
        try { _listener?.Stop(); } catch (Exception) { }
        try { _listener?.Close(); } catch (Exception) { }
        try { _loop?.GetAwaiter().GetResult(); } catch (Exception) { }
        Task[] pending;
        lock (_lock)
            pending = [.. _inflight];
        foreach (var task in pending)
        {
            try { task.GetAwaiter().GetResult(); } catch (Exception) { }
        }
        _cts?.Dispose();
        _gate.Dispose();
    }
}

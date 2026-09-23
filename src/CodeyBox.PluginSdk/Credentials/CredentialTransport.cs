using System.Text.Json;

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Builds the backend-typed exception a credential plugin surfaces. One
/// delegate shape so the shared transport can raise each backend's own
/// <see cref="CredentialException"/> subclass.
/// </summary>
public delegate CredentialException CredentialExceptionFactory(
    CredentialFailureKind kind,
    string message,
    Exception? inner,
    int? statusCode,
    int? retryAfterSeconds);

/// <summary>
/// Optional per-backend replacement for the default status classification.
/// Called with the HTTP status, the operation name, the already-sanitised
/// server detail, and the parsed Retry-After hint; return an exception to
/// throw, or null to fall back to
/// <see cref="CredentialException.KindForStatus"/>.
/// </summary>
public delegate CredentialException? CredentialStatusOverride(
    int statusCode, string operation, string detail, int? retryAfterSeconds);

/// <summary>
/// The guarded send/read core every credential-provider plugin shares:
/// <see cref="SendAsync"/> enforces the endpoint scheme guard at the sink
/// (credentials travel only to absolute https endpoints, or http on
/// loopback hosts for tests and local backends), never follows redirects
/// or cross-origin hops, distinguishes timeout from caller cancellation,
/// and maps failure statuses to the shared taxonomy after extracting a
/// sanitised server detail and any Retry-After hint.
/// <see cref="ReadJsonAsync"/> bounds the success body before buffering
/// (content-length pre-check plus a capped copy) and requires a JSON
/// object root — or an array root where a backend legitimately lists.
/// One implementation so classification, bounding, and disposal policy
/// cannot drift per backend.
/// </summary>
public sealed class CredentialTransport
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly string _backend;
    private readonly CredentialExceptionFactory _exceptionFactory;
    private readonly IReadOnlyList<string> _errorDetailFields;
    private readonly bool _relayRawErrorText;
    private readonly IReadOnlySet<string>? _errorCodeAllowlist;
    private readonly CredentialStatusOverride? _statusOverride;

    /// <param name="http">A client that does not follow redirects (see <see cref="CredentialHttp.CreateNoRedirectClient"/>).</param>
    /// <param name="backend">Display name used in exception messages (e.g. "Bitwarden").</param>
    /// <param name="exceptionFactory">Builds the backend-typed exception.</param>
    /// <param name="errorDetailFields">JSON error-body fields eligible for relay, in preference order.</param>
    /// <param name="relayRawErrorText">True to relay a sanitised raw body when no field yields detail; false to use the fixed placeholder.</param>
    /// <param name="errorCodeAllowlist">When set, relayed field values must match one of these machine-readable codes.</param>
    /// <param name="statusOverride">Optional per-backend status classification replacing the default mapping.</param>
    /// <param name="clock">Time source for Retry-After date headers; defaults to the system clock.</param>
    public CredentialTransport(
        HttpClient http,
        string backend,
        CredentialExceptionFactory exceptionFactory,
        IReadOnlyList<string> errorDetailFields,
        bool relayRawErrorText,
        IReadOnlySet<string>? errorCodeAllowlist = null,
        CredentialStatusOverride? statusOverride = null,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(backend);
        _backend = backend;
        _exceptionFactory = exceptionFactory ?? throw new ArgumentNullException(nameof(exceptionFactory));
        _errorDetailFields = errorDetailFields ?? throw new ArgumentNullException(nameof(errorDetailFields));
        _relayRawErrorText = relayRawErrorText;
        _errorCodeAllowlist = errorCodeAllowlist;
        _statusOverride = statusOverride;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Sends one credential-backend request. Returns the response only on a
    /// same-origin 2xx; every other outcome — an unreachable backend, a
    /// timeout distinct from caller cancellation, a redirect, a cross-origin
    /// hop, or a failure status — throws the backend-typed exception with
    /// safe fields only. The caller owns disposal of a returned response.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        // The scheme guard sits at the sink, not only at the distant
        // options boundary: whatever caller built this request, credential
        // material never leaves for a plain-http non-loopback or
        // non-absolute endpoint.
        var target = request.RequestUri;
        if (target is not { IsAbsoluteUri: true }
            || (target.Scheme != Uri.UriSchemeHttps
                && !(target.Scheme == Uri.UriSchemeHttp && CredentialOptions.IsLoopbackHost(target.Host))))
        {
            throw _exceptionFactory(
                CredentialFailureKind.Misconfigured,
                $"{_backend} {operation} targets an endpoint that is not an absolute https URL (plain http is allowed only for loopback hosts); check the configured base URL.",
                null, null, null);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw _exceptionFactory(
                CredentialFailureKind.Unreachable, $"{_backend} {operation} timed out.", ex, null, null);
        }
        catch (HttpRequestException ex)
        {
            throw _exceptionFactory(
                CredentialFailureKind.Unreachable, $"{_backend} {operation} could not reach the backend.", ex, null, null);
        }

        if (CredentialHttp.IsRedirect(response.StatusCode))
        {
            // Never follow: the backend's 3xx (and its Location) is
            // untrusted runtime output, and re-sending would carry the
            // bearer token to the redirect target. Fail closed as a backend
            // fault — never a verdict on the item.
            var redirect = (int)response.StatusCode;
            response.Dispose();
            throw _exceptionFactory(
                CredentialFailureKind.InvalidResponse,
                $"{_backend} {operation} returned redirect HTTP {redirect}; refusing to follow.",
                null, redirect, null);
        }

        if (response.RequestMessage?.RequestUri is { } finalUri
            && request.RequestUri is { } originalUri
            && !CredentialHttp.IsSameOrigin(finalUri, originalUri))
        {
            // The handler followed a redirect before this code saw the
            // response (only possible with an externally supplied
            // following client — plugins build non-following ones).
            // The credential may already have been re-sent off-origin,
            // so fail loudly rather than trusting this response.
            var followedStatus = (int)response.StatusCode;
            response.Dispose();
            throw _exceptionFactory(
                CredentialFailureKind.InvalidResponse,
                $"{_backend} {operation} was redirected to another origin; refusing the response.",
                null, followedStatus, null);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = (int)response.StatusCode;
        string detail;
        int? retryAfter;
        try
        {
            detail = await CredentialMessages.ReadServerDetailAsync(
                response, _errorDetailFields, _relayRawErrorText, ct, _errorCodeAllowlist).ConfigureAwait(false);
            retryAfter = CredentialRetryAfter.Parse(response, _clock);
        }
        finally
        {
            response.Dispose();
        }
        throw _statusOverride?.Invoke(status, operation, detail, retryAfter)
            ?? _exceptionFactory(
                CredentialException.KindForStatus(status, retryAfter),
                $"{_backend} {operation} failed with HTTP {status}: {detail}",
                null, status, retryAfter);
    }

    /// <summary>
    /// Reads a success response body as a JSON document, bounded before
    /// buffering by <paramref name="maxBytes"/>. The response is always
    /// disposed. An over-cap, non-JSON, or wrong-root body is a typed
    /// <see cref="CredentialFailureKind.InvalidResponse"/> — the body may
    /// carry secret-adjacent text, so it is never echoed into the message.
    /// Set <paramref name="allowArrayRoot"/> only for endpoints whose
    /// documented success shape is a bare array (e.g. listings).
    /// </summary>
    public async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        string operation,
        int maxBytes,
        CancellationToken ct,
        bool allowArrayRoot = false)
    {
        ArgumentNullException.ThrowIfNull(response);
        byte[] body;
        try
        {
            body = await ReadBoundedAsync(response, maxBytes, ct).ConfigureAwait(false);
        }
        finally
        {
            response.Dispose();
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // The body may contain secret-adjacent text; never echo it.
            throw _exceptionFactory(
                CredentialFailureKind.InvalidResponse,
                $"{_backend} {operation} returned a non-JSON success body.", ex, null, null);
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            && !(allowArrayRoot && doc.RootElement.ValueKind == JsonValueKind.Array))
        {
            doc.Dispose();
            throw _exceptionFactory(
                CredentialFailureKind.InvalidResponse,
                $"{_backend} {operation} returned an unexpected JSON shape.", null, null, null);
        }
        return doc;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw _exceptionFactory(
                CredentialFailureKind.InvalidResponse,
                $"{_backend} response declares {contentLength.Value} bytes, above the {maxBytes}-byte cap.",
                null, null, null);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Cap BEFORE buffering: a lying or missing content-length can never
        // fill host memory. The shared copy stops past the cap; over-cap
        // stays a typed backend failure.
        var (bytes, truncated) = await CredentialBodies.CopyCappedAsync(stream, maxBytes, ct).ConfigureAwait(false);
        if (truncated)
            throw _exceptionFactory(
                CredentialFailureKind.InvalidResponse,
                $"{_backend} response exceeds the {maxBytes}-byte cap.",
                null, null, null);
        return bytes;
    }
}

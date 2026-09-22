using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.DopplerPlugin;

/// <summary>One short-lived service-account identity token: the token itself plus its server-side expiry.</summary>
public sealed record DopplerIdentityToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Minimal Doppler REST client: single-secret fetch, OIDC identity-token
/// exchange, and identity-token revocation against the documented shapes
/// (<c>GET /v3/configs/config/secret</c>,
/// <c>POST /v3/auth/oidc</c>, <c>POST /v3/auth/revoke</c>).
/// <para>Every response body is bounded <em>before</em> buffering
/// (<c>ResponseHeadersRead</c> + content-length pre-check + capped copy), so
/// an unbounded upstream can never fill host memory. Every failure surfaces
/// as <see cref="DopplerException"/> with safe fields only — values and
/// tokens never reach messages, logs, or exceptions. A <c>message</c>
/// echoed by the server is truncated and may name projects, configs, or
/// secret names, never values.</para>
/// </summary>
public sealed class DopplerRestClient
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>Maximum characters kept from a server-echoed error message.</summary>
    public const int MaxServerDetailChars = 200;

    public DopplerRestClient(HttpClient http, TimeProvider? clock = null, ILogger? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Fetches one secret's computed value from a project/config. The
    /// <c>computed</c> field (references resolved) is what a workload
    /// expects; <c>raw</c> would leak unresolved <c>${…}</c> template text.
    /// </summary>
    public async Task<string> GetSecretAsync(
        string apiUrl,
        string token,
        string project,
        string config,
        string secretName,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        var url = new StringBuilder($"{apiUrl.TrimEnd('/')}/v3/configs/config/secret?");
        url.Append("project=").Append(Uri.EscapeDataString(project));
        url.Append("&config=").Append(Uri.EscapeDataString(config));
        url.Append("&name=").Append(Uri.EscapeDataString(secretName));
        using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await SendAsync(request, $"fetch secret '{secretName}'", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, $"fetch secret '{secretName}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                throw new DopplerException(
                    DopplerFailureKind.InvalidResponse,
                    $"Doppler fetch of secret '{secretName}' returned no value object.");
            if (!TryGetString(value, "computed", out var computed) || computed is null)
                throw new DopplerException(
                    DopplerFailureKind.InvalidResponse,
                    $"Doppler fetch of secret '{secretName}' returned no computed value.");
            _log.LogDebug(
                "Doppler fetched secret '{Name}' from project '{Project}' config '{Config}'.",
                secretName, project, config);
            return computed;
        }
    }

    /// <summary>
    /// Exchanges an OIDC token for a short-lived service-account identity
    /// token (<c>POST /v3/auth/oidc</c>). Returns the token and its absolute
    /// server <c>expires_at</c> minus <paramref name="skew"/>.
    /// </summary>
    public async Task<DopplerIdentityToken> ExchangeOidcAsync(
        string apiUrl,
        string identityId,
        string oidcToken,
        TimeSpan skew,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentNullException.ThrowIfNull(oidcToken);
        var url = $"{apiUrl.TrimEnd('/')}/v3/auth/oidc";
        // The body carries the OIDC token; it is built, sent, and dropped
        // here — never logged, never stored, never placed in an exception.
        var body = JsonSerializer.Serialize(new { identity = identityId, token = oidcToken });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await SendAsync(request, "OIDC identity exchange", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, "OIDC identity exchange", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!TryGetString(root, "token", out var token) || string.IsNullOrEmpty(token))
                throw new DopplerException(
                    DopplerFailureKind.InvalidResponse,
                    "Doppler OIDC exchange returned no token.");
            if (!TryGetDateTime(root, "expires_at", out var expiresAt))
                throw new DopplerException(
                    DopplerFailureKind.InvalidResponse,
                    "Doppler OIDC exchange returned no expires_at.");
            _log.LogDebug("Doppler OIDC exchange succeeded; identity token valid until {ExpiresAt}.", expiresAt);
            return new DopplerIdentityToken(token, expiresAt - skew);
        }
    }

    /// <summary>
    /// Revokes a minted identity token (<c>POST /v3/auth/revoke</c>).
    /// Idempotent only for a genuinely absent token (HTTP 404): an
    /// unknown or already-revoked token counts as revoked, matching the
    /// manager's must-be-idempotent contract. Every other failure —
    /// notably 401/403 (rejected credential) and 429 (rate-limited) —
    /// propagates so the sweep retries a revocation that never happened.
    /// </summary>
    public async Task RevokeTokenAsync(string apiUrl, string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentNullException.ThrowIfNull(token);
        var url = $"{apiUrl.TrimEnd('/')}/v3/auth/revoke";
        // The doomed token travels in the body; the endpoint takes no
        // Authorization header, so nothing else authenticates this call.
        var body = JsonSerializer.Serialize(new { token });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        try
        {
            using var response = await SendAsync(request, "revoke identity token", ct).ConfigureAwait(false);
            response.Dispose();
            _log.LogInformation("Doppler revoked identity token.");
        }
        catch (DopplerException ex) when (ex.Kind == DopplerFailureKind.NotFound)
        {
            // Unknown or already-revoked token: revocation is complete by
            // definition. The lease id (not the token) is the logged unit.
            _log.LogInformation("Doppler identity token was already absent; treating revocation as complete.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string operation, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new DopplerException(
                DopplerFailureKind.Unreachable, $"Doppler {operation} timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new DopplerException(
                DopplerFailureKind.Unreachable, $"Doppler {operation} could not reach the backend.", ex);
        }

        if (DopplerHttpClients.IsRedirect(response.StatusCode))
        {
            // Never follow: the backend's 3xx (and its Location) is
            // untrusted runtime output, and re-sending would carry the
            // bearer token to the redirect target. Fail closed as a backend
            // fault — never a verdict on the item.
            var redirect = (int)response.StatusCode;
            response.Dispose();
            throw new DopplerException(
                DopplerFailureKind.InvalidResponse,
                $"Doppler {operation} returned redirect HTTP {redirect}; refusing to follow.",
                redirect);
        }

        if (response.RequestMessage?.RequestUri is { } finalUri
            && request.RequestUri is { } originalUri
            && !DopplerHttpClients.IsSameOrigin(finalUri, originalUri))
        {
            // The handler followed a redirect before this code saw the
            // response (only possible with an externally supplied
            // following client — the plugin builds non-following ones).
            // The credential may already have been re-sent off-origin,
            // so fail loudly rather than trusting this response.
            var followedStatus = (int)response.StatusCode;
            response.Dispose();
            throw new DopplerException(
                DopplerFailureKind.InvalidResponse,
                $"Doppler {operation} was redirected to another origin; refusing the response.",
                followedStatus);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = (int)response.StatusCode;
        var detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
        var retryAfter = ParseRetryAfter(response);
        response.Dispose();
        throw DopplerException.FromStatus(status, operation, detail, retryAfter);
    }

    private async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, string operation, int maxBytes, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await ReadBoundedAsync(response, maxBytes, ct).ConfigureAwait(false);
        }
        catch (DopplerException)
        {
            response.Dispose();
            throw;
        }
        response.Dispose();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // The body may contain secret-adjacent text; never echo it.
            throw new DopplerException(
                DopplerFailureKind.InvalidResponse,
                $"Doppler {operation} returned a non-JSON success body.", ex);
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new DopplerException(
                DopplerFailureKind.InvalidResponse,
                $"Doppler {operation} returned an unexpected JSON shape.");
        }
        return doc;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw new DopplerException(
                DopplerFailureKind.InvalidResponse,
                $"Doppler response declares {contentLength.Value} bytes, above the {maxBytes}-byte cap.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Cap BEFORE buffering: a lying or missing content-length can never
        // fill host memory.
        var buffer = new byte[Math.Min(maxBytes + 1, 64 * 1024)];
        using var sink = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sink.Write(buffer, 0, read);
            if (sink.Length > maxBytes)
                throw new DopplerException(
                    DopplerFailureKind.InvalidResponse,
                    $"Doppler response exceeds the {maxBytes}-byte cap.");
        }
        return sink.ToArray();
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[2049];
            using var sink = new MemoryStream();
            int read;
            while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                sink.Write(buffer, 0, read);
                if (sink.Length > 2048)
                    break;
            }
            text = Encoding.UTF8.GetString(sink.ToArray());
        }
        catch (IOException)
        {
            return "no readable error body";
        }
        catch (HttpRequestException)
        {
            return "no readable error body";
        }
        catch (ObjectDisposedException)
        {
            return "no readable error body";
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "message", "error" })
                {
                    if (doc.RootElement.TryGetProperty(key, out var message)
                        && message.ValueKind == JsonValueKind.String)
                    {
                        var detail = message.GetString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(detail))
                        {
                            // Flatten CR/LF like the raw branch below: this
                            // text flows into exception messages, host logs,
                            // and the lease store, so a newline-bearing
                            // server message must not forge log lines.
                            var flatDetail = detail.Replace('\n', ' ').Replace('\r', ' ');
                            return flatDetail.Length <= MaxServerDetailChars ? flatDetail : flatDetail[..MaxServerDetailChars];
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the truncated raw text (status context only).
        }
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= MaxServerDetailChars ? flat : flat[..MaxServerDetailChars];
    }

    private static int? ParseRetryAfter(HttpResponseMessage response)
    {
        try
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                return (int)Math.Clamp(delta.TotalSeconds, 0, 3600);
            if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
                return (int)Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 0, 3600);
        }
        catch (FormatException)
        {
            // Malformed header: no backoff hint.
        }
        return null;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }
        if (property.ValueKind == JsonValueKind.Null)
            return true;
        return false;
    }

    private static bool TryGetDateTime(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(property.GetString(), out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }
}

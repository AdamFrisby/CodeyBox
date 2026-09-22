using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// Minted machine-account access token: the bearer value plus the server's
/// own expiry. The value never leaves host memory (never logged, never
/// persisted, never embedded in lease handles); only <see cref="ExpiresAt"/>
/// informs lease windows.
/// </summary>
internal sealed record BitwardenAccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Minimal Bitwarden Secrets Manager REST client: machine-account
/// client-credentials token minting against the identity origin
/// (<c>POST /connect/token</c>) plus single-secret reads
/// (<c>GET /secrets-manager/secrets/{id}</c>) and key-to-UUID resolution
/// (<c>POST /secrets-manager/secrets/list</c>) against the API origin.
/// <para>Every response body is bounded <em>before</em> buffering
/// (<c>ResponseHeadersRead</c> + content-length pre-check + capped copy), so
/// an unbounded upstream can never fill host memory. Every failure surfaces
/// as <see cref="BitwardenException"/> with safe fields only — values and
/// tokens never reach messages, logs, or exceptions. A <c>message</c> or
/// <c>error_description</c> echoed by the server is truncated and names
/// operations and identifiers, never values.</para>
/// </summary>
internal sealed class BitwardenRestClient
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>Maximum characters kept from a server-echoed error message.</summary>
    public const int MaxServerDetailChars = 200;

    public BitwardenRestClient(HttpClient http, TimeProvider? clock = null, ILogger? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Mints an access token with the machine-account client-credentials
    /// grant. The client secret travels only in this form body, over https
    /// (or loopback http), to the configured identity origin — never to any
    /// other host, because the client never follows redirects.
    /// </summary>
    public async Task<BitwardenAccessToken> AuthenticateAsync(
        string identityUrl,
        string clientId,
        string clientSecret,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret);
        var url = $"{identityUrl.TrimEnd('/')}/connect/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["grant_type"] = "client_credentials",
                ["scope"] = "api.secrets",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            }),
        };
        using var response = await SendTokenAsync(request, ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, "mint machine-account access token", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!TryGetString(root, "access_token", out var token) || string.IsNullOrEmpty(token))
                throw new BitwardenException(
                    BitwardenFailureKind.InvalidResponse,
                    "Bitwarden identity server returned no access token.");
            var expiresIn = TryGetInt32(root, "expires_in", out var seconds) ? seconds : 3600;
            if (expiresIn <= 0)
                throw new BitwardenException(
                    BitwardenFailureKind.InvalidResponse,
                    "Bitwarden identity server returned no usable token lifetime.");
            var expiresAt = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(expiresIn, 24 * 3600));
            _log.LogDebug("Bitwarden minted a machine-account access token.");
            return new BitwardenAccessToken(token, expiresAt);
        }
    }

    /// <summary>
    /// Reads one secret value by UUID. Only the <c>value</c> is returned;
    /// every other field is dropped without logging. When
    /// <paramref name="expectedProjectId"/> is set, a secret living in any
    /// other project fails loudly instead of serving a wrong-project value.
    /// </summary>
    public async Task<string> GetSecretAsync(
        string apiUrl,
        string accessToken,
        string secretId,
        string? expectedProjectId,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        var url = $"{apiUrl.TrimEnd('/')}/secrets-manager/secrets/{Uri.EscapeDataString(secretId.Trim())}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, $"read secret '{secretId.Trim()}'", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, $"read secret '{secretId.Trim()}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!TryGetString(root, "value", out var value) || value is null)
                throw new BitwardenException(
                    BitwardenFailureKind.InvalidResponse,
                    $"Bitwarden secret '{secretId.Trim()}' returned no value.");
            if (!string.IsNullOrWhiteSpace(expectedProjectId)
                && TryGetString(root, "projectId", out var projectId)
                && !string.IsNullOrWhiteSpace(projectId)
                && !string.Equals(projectId, expectedProjectId.Trim(), StringComparison.Ordinal))
                throw new BitwardenException(
                    BitwardenFailureKind.Misconfigured,
                    $"Bitwarden secret '{secretId.Trim()}' lives in project '{projectId}', not the mapped project; refusing a wrong-project read.");
            if (string.IsNullOrEmpty(value))
                throw new BitwardenException(
                    BitwardenFailureKind.InvalidResponse,
                    $"Bitwarden secret '{secretId.Trim()}' returned an empty value.");
            _log.LogDebug("Bitwarden read secret '{SecretId}'.", secretId.Trim());
            return value;
        }
    }

    /// <summary>
    /// Resolves a secret key to its UUID with an exact (ordinal) match over
    /// the organisation's secret listing. Substring or case-insensitive
    /// matching would let similarly-named secrets shadow each other; exact
    /// match fails loudly instead, and ambiguity resolves only via
    /// <c>SecretId</c>.
    /// </summary>
    public async Task<string> ResolveSecretIdAsync(
        string apiUrl,
        string accessToken,
        string organizationId,
        string key,
        string? projectId,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var url = $"{apiUrl.TrimEnd('/')}/secrets-manager/secrets/list";
        var payload = JsonSerializer.Serialize(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["organizationId"] = organizationId.Trim() });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, "list secrets", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, "list secrets", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
                throw new BitwardenException(
                    BitwardenFailureKind.InvalidResponse,
                    "Bitwarden secret listing returned an unexpected JSON shape.");
            string? match = null;
            var matches = 0;
            foreach (var candidate in data.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                    continue;
                if (!TryGetString(candidate, "key", out var candidateKey)
                    || !string.Equals(candidateKey, key.Trim(), StringComparison.Ordinal))
                    continue;
                if (!string.IsNullOrWhiteSpace(projectId)
                    && TryGetString(candidate, "projectId", out var candidateProject)
                    && !string.IsNullOrWhiteSpace(candidateProject)
                    && !string.Equals(candidateProject, projectId.Trim(), StringComparison.Ordinal))
                    continue;
                if (!TryGetString(candidate, "id", out var id) || string.IsNullOrWhiteSpace(id))
                    continue;
                matches++;
                match ??= id;
            }
            if (matches == 0)
                throw new BitwardenException(
                    BitwardenFailureKind.NotFound,
                    $"Bitwarden secret key '{key.Trim()}' was not found.");
            if (matches > 1)
                throw new BitwardenException(
                    BitwardenFailureKind.Misconfigured,
                    $"Bitwarden secret key '{key.Trim()}' is ambiguous ({matches} matches); use SecretId instead.");
            return match!;
        }
    }

    private async Task<HttpResponseMessage> SendTokenAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await SendAsync(request, "mint machine-account access token", ct).ConfigureAwait(false);
        }
        catch (BitwardenException ex) when (ex.Kind == BitwardenFailureKind.Misconfigured && ex.StatusCode == 400)
        {
            // A 400 from the token endpoint is by definition a rejected
            // grant: bad client id/secret, wrong scope (which this client
            // fixes to api.secrets), or a disabled machine account. That is
            // an authorisation outcome, never operator shape — the body
            // carries the machine-readable code but the classification must
            // not depend on which field the server filled in.
            throw new BitwardenException(
                BitwardenFailureKind.Unauthorized,
                $"Bitwarden mint machine-account access token rejected the machine-account credential: {ex.Message}",
                400);
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
            throw new BitwardenException(
                BitwardenFailureKind.Unreachable, $"Bitwarden {operation} timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new BitwardenException(
                BitwardenFailureKind.Unreachable, $"Bitwarden {operation} could not reach the backend.", ex);
        }

        if (BitwardenHttpClients.IsRedirect(response.StatusCode))
        {
            // Never follow: the backend's 3xx (and its Location) is
            // untrusted runtime output, and re-sending would carry the
            // bearer token to the redirect target. Fail closed as a backend
            // fault — never a verdict on the item.
            var redirect = (int)response.StatusCode;
            response.Dispose();
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                $"Bitwarden {operation} returned redirect HTTP {redirect}; refusing to follow.",
                redirect);
        }

        if (response.RequestMessage?.RequestUri is { } finalUri
            && request.RequestUri is { } originalUri
            && !BitwardenHttpClients.IsSameOrigin(finalUri, originalUri))
        {
            // The handler followed a redirect before this code saw the
            // response (only possible with an externally supplied
            // following client — the plugin builds non-following ones).
            // The credential may already have been re-sent off-origin,
            // so fail loudly rather than trusting this response.
            var followedStatus = (int)response.StatusCode;
            response.Dispose();
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                $"Bitwarden {operation} was redirected to another origin; refusing the response.",
                followedStatus);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = (int)response.StatusCode;
        var detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
        var retryAfter = ParseRetryAfter(response);
        response.Dispose();
        if (status == 400 && IsIdentityCredentialRejection(detail))
            throw new BitwardenException(
                BitwardenFailureKind.Unauthorized,
                $"Bitwarden {operation} rejected the machine-account credential: {detail}",
                status);
        throw BitwardenException.FromStatus(status, operation, detail, retryAfter);
    }

    private static bool IsIdentityCredentialRejection(string detail)
        => detail.Contains("invalid_client", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase);

    private async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, string operation, int maxBytes, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await ReadBoundedAsync(response, maxBytes, ct).ConfigureAwait(false);
        }
        catch (BitwardenException)
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
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                $"Bitwarden {operation} returned a non-JSON success body.", ex);
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                $"Bitwarden {operation} returned an unexpected JSON shape.");
        }
        return doc;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                $"Bitwarden response declares {contentLength.Value} bytes, above the {maxBytes}-byte cap.");
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
                throw new BitwardenException(
                    BitwardenFailureKind.InvalidResponse,
                    $"Bitwarden response exceeds the {maxBytes}-byte cap.");
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
                foreach (var key in new[] { "message", "error_description", "error" })
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

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var parsed))
        {
            value = parsed;
            return true;
        }
        return false;
    }
}

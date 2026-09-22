using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.BitwardenPlugin;

/// <summary>
/// Minted machine-account access token: the bearer value plus the server's
/// own expiry, with the symmetric decryption material held alongside in
/// host memory only. <see cref="AccessTokenKey"/> is the 64-byte key parsed
/// from the single access-token string's <c>:key</c> suffix (null for legacy
/// bare-secret credentials); <see cref="OrganizationKey"/> is recovered by
/// opening the token response's <c>encrypted_payload</c> with that key
/// (null when the server sent none or it did not open — values are then
/// tried directly against <see cref="AccessTokenKey"/>). Nothing here is
/// ever logged, persisted, or embedded in lease handles; only
/// <see cref="ExpiresAt"/> informs lease windows.
/// </summary>
internal sealed class BitwardenAccessToken
{
    public string Token { get; }

    public DateTimeOffset ExpiresAt { get; }

    public byte[]? AccessTokenKey { get; }

    public byte[]? OrganizationKey { get; }

    /// <summary>
    /// Key tried first for secret values: the payload-derived organisation
    /// key when present, else the access-token key. Null when the operator
    /// configured a legacy bare client secret carrying no key material.
    /// </summary>
    public byte[]? EffectiveDecryptionKey => OrganizationKey ?? AccessTokenKey;

    public BitwardenAccessToken(
        string token, DateTimeOffset expiresAt, byte[]? accessTokenKey = null, byte[]? organizationKey = null)
    {
        Token = token;
        ExpiresAt = expiresAt;
        AccessTokenKey = accessTokenKey;
        OrganizationKey = organizationKey;
    }

    /// <summary>
    /// Never renders the bearer value or key material: the synthesized
    /// record-style dump would leak the credential into any log or
    /// exception-data capture.
    /// </summary>
    public override string ToString() => "BitwardenAccessToken (redacted)";

    internal void ClearSecrets()
    {
        if (AccessTokenKey is not null)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(AccessTokenKey);
        if (OrganizationKey is not null)
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(OrganizationKey);
    }
}

/// <summary>
/// Minimal Bitwarden Secrets Manager REST client: machine-account
/// client-credentials token minting against the identity origin
/// (<c>POST /connect/token</c>) plus single-secret reads
/// (<c>GET /secrets/{id}</c>) and key-to-UUID resolution
/// (<c>GET /organizations/{organizationId}/secrets</c>) against the API
/// origin — the routes and response models served by
/// <c>bitwarden/server</c> <c>SecretsController</c> (<c>SecretResponseModel</c>
/// carries <c>value</c> plus a <c>projects</c> array; the organisation
/// listing <c>SecretWithProjectsListResponseModel</c> carries a
/// <c>secrets</c> array whose entries carry no <c>value</c>).
/// <para>Every response body is bounded <em>before</em> buffering
/// (<c>ResponseHeadersRead</c> + content-length pre-check + capped copy), so
/// an unbounded upstream can never fill host memory. Every failure surfaces
/// as <see cref="BitwardenException"/> with safe fields only — HTTP status,
/// the allowlisted machine-readable <c>error</c> code, and operator
/// identifiers — never values, tokens, client secrets, or free-text server
/// prose (a server echoing request content in an error body must not land a
/// credential in logs or the lease store). A <c>value</c> arriving as a
/// Bitwarden CipherString (end-to-end-encrypted) is opened with the
/// machine-account decryption key (see <see cref="BitwardenCrypto"/>); only
/// when no key material is configured, or the envelope does not open, is it
/// refused loudly instead of being served as ciphertext.</para>
/// </summary>
internal sealed class BitwardenRestClient
{
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>Server <c>expires_in</c> fallback when the token response carries none.</summary>
    internal const int DefaultTokenLifetimeSeconds = 3600;

    /// <summary>Sanity cap on an accepted token lifetime; larger values are clamped, not trusted.</summary>
    internal const int MaxAcceptedTokenLifetimeSeconds = 24 * 3600;

    /// <summary>Cap on an error response body kept for error-code extraction.</summary>
    internal const int MaxErrorBodyBytes = 2048;

    /// <summary>
    /// Machine-readable OAuth/API <c>error</c> codes safe to relay into an
    /// exception message. Anything else the server sends — free-text
    /// <c>message</c>/<c>error_description</c>, raw bodies — is untrusted
    /// runtime output that may echo request content (including credentials)
    /// and therefore never reaches messages, logs, or the lease store.
    /// </summary>
    private static readonly HashSet<string> SafeErrorCodes = new(StringComparer.Ordinal)
    {
        "invalid_client",
        "invalid_grant",
        "invalid_request",
        "invalid_scope",
        "unauthorized_client",
        "unsupported_grant_type",
        "access_denied",
        "server_error",
        "temporarily_unavailable",
    };

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
    /// <para>When <paramref name="accessTokenKey"/> carries the 64-byte key
    /// parsed from the single access-token string's <c>:key</c> suffix, the
    /// token response's <c>encrypted_payload</c> (when present) is opened
    /// with it to recover the organisation decryption key; secret values
    /// are then tried against that key first. Null (legacy bare-secret
    /// credentials) mints a bearer-only token that can still serve
    /// plaintext values but refuses CipherString values loudly.</para>
    /// </summary>
    public async Task<BitwardenAccessToken> AuthenticateAsync(
        string identityUrl,
        string clientId,
        string clientSecret,
        int maxResponseBytes,
        byte[]? accessTokenKey = null,
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
            var expiresIn = TryGetInt32(root, "expires_in", out var seconds) ? seconds : DefaultTokenLifetimeSeconds;
            if (expiresIn <= 0)
                throw new BitwardenException(
                    BitwardenFailureKind.InvalidResponse,
                    "Bitwarden identity server returned no usable token lifetime.");
            var expiresAt = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(expiresIn, MaxAcceptedTokenLifetimeSeconds));
            byte[]? organizationKey = null;
            if (accessTokenKey is { Length: BitwardenCrypto.KeySize }
                && TryGetString(root, "encrypted_payload", out var payload)
                && !string.IsNullOrWhiteSpace(payload))
            {
                // A payload that does not open is not fatal: values are
                // still tried directly against the access-token key.
                organizationKey = BitwardenCrypto.DeriveOrganizationKey(payload, accessTokenKey);
            }
            _log.LogDebug("Bitwarden minted a machine-account access token.");
            return new BitwardenAccessToken(token, expiresAt, accessTokenKey, organizationKey);
        }
    }

    /// <summary>
    /// Reads one secret value by UUID (<c>GET /secrets/{id}</c>). Only the
    /// <c>value</c> is returned; every other field is dropped without
    /// logging. When <paramref name="expectedProjectId"/> is set, the
    /// response's <c>projects</c> array must contain it (ordinal match) —
    /// a secret living in any other project, or a response carrying no
    /// usable project linkage at all, fails loudly instead of serving a
    /// wrong-project value. An end-to-end-encrypted <c>value</c>
    /// (Bitwarden CipherString) is opened with the token's decryption key;
    /// when no key is configured, or the envelope does not open, it is
    /// refused loudly: serving ciphertext as a credential would be a silent
    /// integrity failure.
    /// <para>The requested id must be a UUID (the documented
    /// <c>SecretResponseModel.id</c> shape, matching the validated
    /// configured <c>SecretId</c> and the allowlisted listing ids): anything
    /// else is rejected before any network or message use, so
    /// server-controlled text can never reach exception messages or logs
    /// via the <c>read secret '{id}'</c> operation string.</para>
    /// </summary>
    public async Task<string> GetSecretAsync(
        string apiUrl,
        BitwardenAccessToken accessToken,
        string secretId,
        string? expectedProjectId,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiUrl);
        ArgumentNullException.ThrowIfNull(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretId);
        var trimmedId = secretId.Trim();
        if (!Guid.TryParse(trimmedId, out _))
            throw new BitwardenException(
                BitwardenFailureKind.Misconfigured,
                "Bitwarden secret id is not a UUID; check the mapping's SecretId.");
        var url = $"{apiUrl.TrimEnd('/')}/secrets/{Uri.EscapeDataString(trimmedId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        using var response = await SendAsync(request, $"read secret '{trimmedId}'", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, $"read secret '{trimmedId}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!TryGetString(root, "value", out var value) || string.IsNullOrEmpty(value))
                throw new BitwardenException(
                    BitwardenFailureKind.InvalidResponse,
                    $"Bitwarden secret '{trimmedId}' returned no value.");
            if (!ProjectGuardPasses(root, expectedProjectId))
                throw new BitwardenException(
                    BitwardenFailureKind.Misconfigured,
                    $"Bitwarden secret '{trimmedId}' is not in the mapped project; refusing a wrong-project read.");
            if (!BitwardenCrypto.IsCipherString(value))
            {
                _log.LogDebug("Bitwarden read secret '{SecretId}'.", trimmedId);
                return value;
            }
            var key = accessToken.EffectiveDecryptionKey;
            if (key is not null
                && BitwardenCrypto.TryDecryptCipherString(value, key, out var plaintext)
                && !string.IsNullOrEmpty(plaintext))
            {
                _log.LogDebug("Bitwarden read secret '{SecretId}'.", trimmedId);
                return plaintext;
            }
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                $"Bitwarden secret '{trimmedId}' returned an end-to-end-encrypted value this provider could not open; refusing to serve ciphertext.");
        }
    }

    /// <summary>
    /// True when no project expectation is configured, or the secret's
    /// <c>projects</c> array contains the expected project id (ordinal).
    /// Anything else — a different project, or no usable project linkage —
    /// is a loud failure at the call site, never a served value.
    /// </summary>
    private static bool ProjectGuardPasses(JsonElement secret, string? expectedProjectId)
    {
        if (string.IsNullOrWhiteSpace(expectedProjectId))
            return true;
        var expected = expectedProjectId.Trim();
        foreach (var listed in ReadProjectIds(secret))
        {
            if (string.Equals(listed, expected, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Project ids linked to a secret response (<c>projects[].id</c>).</summary>
    private static IEnumerable<string> ReadProjectIds(JsonElement secret)
    {
        if (secret.TryGetProperty("projects", out var projects)
            && projects.ValueKind == JsonValueKind.Array)
        {
            foreach (var project in projects.EnumerateArray())
            {
                if (project.ValueKind == JsonValueKind.Object
                    && TryGetString(project, "id", out var id)
                    && !string.IsNullOrWhiteSpace(id))
                    yield return id.Trim();
            }
        }
    }

    /// <summary>
    /// Resolves a secret key to its UUID with an exact (ordinal) match over
    /// the organisation's secret listing
    /// (<c>GET /organizations/{organizationId}/secrets</c>, whose entries
    /// carry no <c>value</c>). Substring or case-insensitive matching would
    /// let similarly-named secrets shadow each other; exact match fails
    /// loudly instead, and ambiguity resolves only via <c>SecretId</c>.
    /// When <paramref name="projectId"/> is set, only entries linked to
    /// that project (via their <c>projects</c> array) are eligible — an
    /// entry with no usable project linkage cannot satisfy a project
    /// filter, so it is skipped rather than served.
    /// <para>Listing entries are dependency runtime output (less-trusted):
    /// a candidate <c>id</c> that is not a UUID — the documented shape —
    /// is skipped, never accepted, so server-controlled text can never flow
    /// into the follow-up <c>GET /secrets/{id}</c> operation string and from
    /// there into exception messages and host logs.</para>
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
        var trimmedOrg = organizationId.Trim();
        var trimmedKey = key.Trim();
        var projectFilter = string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim();
        var url = $"{apiUrl.TrimEnd('/')}/organizations/{Uri.EscapeDataString(trimmedOrg)}/secrets";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await SendAsync(request, "list secrets", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, "list secrets", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("secrets", out var data)
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
                    || !string.Equals(candidateKey, trimmedKey, StringComparison.Ordinal))
                    continue;
                if (projectFilter is not null && !ListsProject(candidate, projectFilter))
                    continue;
                if (!TryGetString(candidate, "id", out var id) || string.IsNullOrWhiteSpace(id))
                    continue;
                var trimmedId = id.Trim();
                if (!Guid.TryParse(trimmedId, out _))
                    continue;
                matches++;
                match ??= trimmedId;
            }
            if (matches == 0)
                throw new BitwardenException(
                    BitwardenFailureKind.NotFound,
                    $"Bitwarden secret key '{trimmedKey}' was not found.");
            if (matches > 1)
                throw new BitwardenException(
                    BitwardenFailureKind.Misconfigured,
                    $"Bitwarden secret key '{trimmedKey}' is ambiguous ({matches} matches); use SecretId instead.");
            return match!;
        }
    }

    private static bool ListsProject(JsonElement candidate, string projectId)
    {
        foreach (var listed in ReadProjectIds(candidate))
        {
            if (string.Equals(listed, projectId, StringComparison.Ordinal))
                return true;
        }
        return false;
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
                ex,
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
        => string.Equals(detail, "invalid_client", StringComparison.Ordinal)
            || string.Equals(detail, "invalid_grant", StringComparison.Ordinal)
            || string.Equals(detail, "unauthorized_client", StringComparison.Ordinal);

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
        // fill host memory. The shared copy stops past the cap; over-cap
        // stays a typed backend failure.
        var (bytes, truncated) = await CredentialBodies.CopyCappedAsync(stream, maxBytes, ct).ConfigureAwait(false);
        if (truncated)
            throw new BitwardenException(
                BitwardenFailureKind.InvalidResponse,
                $"Bitwarden response exceeds the {maxBytes}-byte cap.");
        return bytes;
    }

    /// <summary>
    /// Extracts the safe part of an error response: the machine-readable
    /// <c>error</c> code, and only when it is on the allowlist. Server
    /// free text (<c>message</c>, <c>error_description</c>) and raw bodies
    /// are untrusted dependency output that may echo request content — a
    /// reflected client secret or bearer token must never reach exception
    /// messages (which the host logs and persists in the lease store) — so
    /// they are never relayed. Anything without an allowlisted code yields
    /// a fixed placeholder; the HTTP status travels separately.
    /// </summary>
    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var (bytes, _) = await CredentialBodies.CopyCappedAsync(stream, MaxErrorBodyBytes, ct).ConfigureAwait(false);
            text = Encoding.UTF8.GetString(bytes);
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
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                var code = (error.GetString() ?? string.Empty).Trim();
                if (SafeErrorCodes.Contains(code))
                    return code;
            }
        }
        catch (JsonException)
        {
            // No allowlisted code: fall through to the fixed placeholder.
        }
        return "no readable error body";
    }

    private int? ParseRetryAfter(HttpResponseMessage response)
        => CredentialRetryAfter.Parse(response, _clock);

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

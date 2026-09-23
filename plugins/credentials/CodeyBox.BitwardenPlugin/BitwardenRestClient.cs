using System.Net.Http.Headers;
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
    /// Never renders the bearer value or key material. Defensive redaction:
    /// if this type ever becomes a record or struct, the synthesized member
    /// dump would leak the credential into any log or exception-data
    /// capture — the override keeps the safe rendering either way.
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
    private readonly CredentialTransport _transport;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>Server <c>expires_in</c> fallback when the token response carries none.</summary>
    internal const int DefaultTokenLifetimeSeconds = 3600;

    /// <summary>Sanity cap on an accepted token lifetime; larger values are clamped, not trusted.</summary>
    internal const int MaxAcceptedTokenLifetimeSeconds = 24 * 3600;

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

    /// <summary>JSON error-body fields carrying the machine-readable code.</summary>
    private static readonly string[] ErrorCodeFields = ["error"];

    public BitwardenRestClient(HttpClient http, TimeProvider? clock = null, ILogger? log = null)
    {
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
        _transport = new CredentialTransport(
            http ?? throw new ArgumentNullException(nameof(http)),
            "Bitwarden",
            BitwardenException.Create,
            ErrorCodeFields,
            relayRawErrorText: false,
            errorCodeAllowlist: SafeErrorCodes,
            statusOverride: ClassifyCredentialRejection,
            clock: _clock);
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
        var doc = await _transport.ReadJsonAsync(response, "mint machine-account access token", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!CredentialJson.TryGetString(root, "access_token", out var token) || string.IsNullOrEmpty(token))
                throw new BitwardenException(
                    CredentialFailureKind.InvalidResponse,
                    "Bitwarden identity server returned no access token.");
            var expiresIn = CredentialJson.TryGetInt32(root, "expires_in", out var seconds) ? seconds : DefaultTokenLifetimeSeconds;
            if (expiresIn <= 0)
                throw new BitwardenException(
                    CredentialFailureKind.InvalidResponse,
                    "Bitwarden identity server returned no usable token lifetime.");
            var expiresAt = _clock.GetUtcNow() + TimeSpan.FromSeconds(Math.Min(expiresIn, MaxAcceptedTokenLifetimeSeconds));
            byte[]? organizationKey = null;
            if (accessTokenKey is { Length: BitwardenCrypto.KeySize }
                && CredentialJson.TryGetString(root, "encrypted_payload", out var payload)
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
                CredentialFailureKind.Misconfigured,
                "Bitwarden secret id is not a UUID; check the mapping's SecretId.");
        var url = $"{apiUrl.TrimEnd('/')}/secrets/{Uri.EscapeDataString(trimmedId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        using var response = await _transport.SendAsync(request, $"read secret '{trimmedId}'", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, $"read secret '{trimmedId}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!CredentialJson.TryGetString(root, "value", out var value) || string.IsNullOrEmpty(value))
                throw new BitwardenException(
                    CredentialFailureKind.InvalidResponse,
                    $"Bitwarden secret '{trimmedId}' returned no value.");
            if (!ProjectGuardPasses(root, expectedProjectId))
                throw new BitwardenException(
                    CredentialFailureKind.Misconfigured,
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
                CredentialFailureKind.InvalidResponse,
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
                    && CredentialJson.TryGetString(project, "id", out var id)
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
        using var response = await _transport.SendAsync(request, "list secrets", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, "list secrets", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("secrets", out var data)
                || data.ValueKind != JsonValueKind.Array)
                throw new BitwardenException(
                    CredentialFailureKind.InvalidResponse,
                    "Bitwarden secret listing returned an unexpected JSON shape.");
            string? match = null;
            var matches = 0;
            foreach (var candidate in data.EnumerateArray())
            {
                if (candidate.ValueKind != JsonValueKind.Object)
                    continue;
                if (!CredentialJson.TryGetString(candidate, "key", out var candidateKey)
                    || !string.Equals(candidateKey, trimmedKey, StringComparison.Ordinal))
                    continue;
                if (projectFilter is not null && !ListsProject(candidate, projectFilter))
                    continue;
                if (!CredentialJson.TryGetString(candidate, "id", out var id) || string.IsNullOrWhiteSpace(id))
                    continue;
                var trimmedId = id.Trim();
                if (!Guid.TryParse(trimmedId, out _))
                    continue;
                matches++;
                match ??= trimmedId;
            }
            if (matches == 0)
                throw new BitwardenException(
                    CredentialFailureKind.NotFound,
                    $"Bitwarden secret key '{trimmedKey}' was not found.");
            if (matches > 1)
                throw new BitwardenException(
                    CredentialFailureKind.Misconfigured,
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
            return await _transport.SendAsync(request, "mint machine-account access token", ct).ConfigureAwait(false);
        }
        catch (BitwardenException ex) when (ex.Kind == CredentialFailureKind.Misconfigured && ex.StatusCode == 400)
        {
            // A 400 from the token endpoint is by definition a rejected
            // grant: bad client id/secret, wrong scope (which this client
            // fixes to api.secrets), or a disabled machine account. That is
            // an authorisation outcome, never operator shape — the body
            // carries the machine-readable code but the classification must
            // not depend on which field the server filled in.
            throw new BitwardenException(
                CredentialFailureKind.Unauthorized,
                $"Bitwarden mint machine-account access token rejected the machine-account credential: {ex.Message}",
                ex,
                400);
        }
    }

    /// <summary>
    /// Bitwarden-specific status classification for the shared transport:
    /// a 400 whose allowlisted error code is a credential rejection
    /// (<c>invalid_client</c>/<c>invalid_grant</c>/<c>unauthorized_client</c>)
    /// is an authorisation outcome, not operator shape.
    /// </summary>
    private static CredentialException? ClassifyCredentialRejection(
        int statusCode, string operation, string detail, int? retryAfterSeconds)
        => statusCode == 400 && IsIdentityCredentialRejection(detail)
            ? new BitwardenException(
                CredentialFailureKind.Unauthorized,
                $"Bitwarden {operation} rejected the machine-account credential: {detail}",
                statusCode)
            : null;

    private static bool IsIdentityCredentialRejection(string detail)
        => string.Equals(detail, "invalid_client", StringComparison.Ordinal)
            || string.Equals(detail, "invalid_grant", StringComparison.Ordinal)
            || string.Equals(detail, "unauthorized_client", StringComparison.Ordinal);
}

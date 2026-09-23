using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Credentials;
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
    private readonly CredentialTransport _transport;
    private readonly ILogger _log;

    /// <summary>JSON error-body fields relayed as detail, in preference order.</summary>
    private static readonly string[] ErrorDetailFields = ["message", "error"];

    public DopplerRestClient(HttpClient http, TimeProvider? clock = null, ILogger? log = null)
    {
        _transport = new CredentialTransport(
            http ?? throw new ArgumentNullException(nameof(http)),
            "Doppler",
            DopplerException.Create,
            ErrorDetailFields,
            relayRawErrorText: true,
            clock: clock);
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
        using var response = await _transport.SendAsync(request, $"fetch secret '{secretName}'", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, $"fetch secret '{secretName}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                throw new DopplerException(
                    CredentialFailureKind.InvalidResponse,
                    $"Doppler fetch of secret '{secretName}' returned no value object.");
            if (!CredentialJson.TryGetString(value, "computed", out var computed) || computed is null)
                throw new DopplerException(
                    CredentialFailureKind.InvalidResponse,
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
        using var response = await _transport.SendAsync(request, "OIDC identity exchange", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, "OIDC identity exchange", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!CredentialJson.TryGetString(root, "token", out var token) || string.IsNullOrEmpty(token))
                throw new DopplerException(
                    CredentialFailureKind.InvalidResponse,
                    "Doppler OIDC exchange returned no token.");
            if (!CredentialJson.TryGetDateTime(root, "expires_at", out var expiresAt))
                throw new DopplerException(
                    CredentialFailureKind.InvalidResponse,
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
            using var response = await _transport.SendAsync(request, "revoke identity token", ct).ConfigureAwait(false);
            response.Dispose();
            _log.LogInformation("Doppler revoked identity token.");
        }
        catch (DopplerException ex) when (ex.Kind == CredentialFailureKind.NotFound)
        {
            // Unknown or already-revoked token: revocation is complete by
            // definition. The lease id (not the token) is the logged unit.
            _log.LogInformation("Doppler identity token was already absent; treating revocation as complete.");
        }
    }

}

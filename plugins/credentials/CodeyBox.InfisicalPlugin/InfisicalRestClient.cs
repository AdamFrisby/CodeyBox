using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeyBox.PluginSdk.Credentials;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.InfisicalPlugin;

/// <summary>One fetched static secret: identity for the lease, value for the sandbox.</summary>
public sealed record InfisicalStaticSecret(string SecretId, long Version, string Value);

/// <summary>One created dynamic lease: server lease identity, expiry, and credential fields.</summary>
public sealed record InfisicalDynamicLease(
    string LeaseId, DateTimeOffset ExpiresAt, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// Minimal Infisical REST client: universal-auth login, static-secret fetch,
/// and dynamic-lease create/renew/revoke against the documented shapes
/// (<c>/api/v1/auth/universal-auth/login</c>, <c>/api/v3/secrets/raw/{key}</c>,
/// <c>/api/v1/dynamic-secrets/leases*</c>).
/// <para>Every response body is bounded <em>before</em> buffering
/// (<c>ResponseHeadersRead</c> + content-length pre-check + capped copy), so
/// an unbounded upstream can never fill host memory. Every failure surfaces
/// as <see cref="InfisicalException"/> with safe fields only — values,
/// tokens, and client secrets never reach messages, logs, or exceptions.
/// A <c>message</c> echoed by the server is truncated and may name keys or
/// lease ids, never values.</para>
/// </summary>
public sealed class InfisicalRestClient
{
    private readonly CredentialTransport _transport;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>JSON error-body fields relayed as detail, in preference order.</summary>
    private static readonly string[] ErrorDetailFields = ["message"];

    // A universal-auth login response is a JSON object with a JWT accessToken — typically
    // a few hundred bytes; bound generously so real tokens fit while still capping the buffer.
    private const int MaxLoginResponseBytes = 4 * 1024;

    public InfisicalRestClient(HttpClient http, TimeProvider? clock = null, ILogger? log = null)
    {
        _transport = new CredentialTransport(
            http ?? throw new ArgumentNullException(nameof(http)),
            "Infisical",
            InfisicalException.Create,
            ErrorDetailFields,
            relayRawErrorText: true,
            clock: clock);
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// Universal-auth login. Returns the bearer token and its absolute
    /// expiry (server <c>expiresIn</c> seconds minus <paramref name="skew"/>).
    /// </summary>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> LoginUniversalAuthAsync(
        string siteUrl, string clientId, string clientSecret, TimeSpan skew, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(clientSecret);
        var url = $"{siteUrl.TrimEnd('/')}/api/v1/auth/universal-auth/login";
        // The body carries the client secret; it is built, sent, and dropped
        // here — never logged, never stored, never placed in an exception.
        var body = JsonSerializer.Serialize(new { clientId, clientSecret });
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await _transport.SendAsync(request, "universal-auth login", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, "universal-auth login", MaxLoginResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!CredentialJson.TryGetString(root, "accessToken", out var token) || string.IsNullOrEmpty(token))
                throw new InfisicalException(
                    CredentialFailureKind.InvalidResponse, "Infisical universal-auth login returned no accessToken.");
            var expiresInSeconds = CredentialJson.TryGetDouble(root, "expiresIn", out var seconds) ? seconds : 0;
            if (expiresInSeconds <= 0)
                throw new InfisicalException(
                    CredentialFailureKind.InvalidResponse, "Infisical universal-auth login returned no expiresIn.");
            _log.LogDebug("Infisical universal-auth login succeeded; token valid for {Seconds}s.", (long)expiresInSeconds);
            return (token, _clock.GetUtcNow() + TimeSpan.FromSeconds(expiresInSeconds) - skew);
        }
    }

    /// <summary>Fetches one static secret by key.</summary>
    public async Task<InfisicalStaticSecret> GetStaticSecretAsync(
        string siteUrl,
        string token,
        string workspaceId,
        string environment,
        string secretKey,
        string secretPath,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        var url = new StringBuilder($"{siteUrl.TrimEnd('/')}/api/v3/secrets/raw/{Uri.EscapeDataString(secretKey)}?");
        url.Append("workspaceId=").Append(Uri.EscapeDataString(workspaceId));
        url.Append("&environment=").Append(Uri.EscapeDataString(environment));
        url.Append("&secretPath=").Append(Uri.EscapeDataString(
            string.IsNullOrWhiteSpace(secretPath) ? "/" : secretPath));
        url.Append("&expandSecretReferences=true");
        using var request = new HttpRequestMessage(HttpMethod.Get, url.ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _transport.SendAsync(request, $"fetch secret '{secretKey}'", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, $"fetch secret '{secretKey}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("secret", out var secret)
                || secret.ValueKind != JsonValueKind.Object)
                throw new InfisicalException(
                    CredentialFailureKind.InvalidResponse,
                    $"Infisical fetch of secret '{secretKey}' returned no secret object.");
            if (!CredentialJson.TryGetString(secret, "secretValue", out var value) || value is null)
                throw new InfisicalException(
                    CredentialFailureKind.InvalidResponse,
                    $"Infisical fetch of secret '{secretKey}' returned no secretValue.");
            CredentialJson.TryGetString(secret, "id", out var id);
            var version = CredentialJson.TryGetInt64(secret, "version", out var v) ? v : 0;
            _log.LogDebug(
                "Infisical fetched secret '{Key}' (id {Id}, version {Version}).",
                secretKey, string.IsNullOrEmpty(id) ? "unknown" : id, version);
            return new InfisicalStaticSecret(id ?? string.Empty, version, value);
        }
    }

    /// <summary>Creates a dynamic-secret lease; returns the server lease id, expiry, and data fields.</summary>
    public async Task<InfisicalDynamicLease> CreateDynamicLeaseAsync(
        string siteUrl,
        string token,
        string projectSlug,
        string environmentSlug,
        string secretPath,
        string dynamicSecretName,
        string ttl,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dynamicSecretName);
        var url = $"{siteUrl.TrimEnd('/')}/api/v1/dynamic-secrets/leases";
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["dynamicSecretName"] = dynamicSecretName,
            ["projectSlug"] = projectSlug,
            ["environmentSlug"] = environmentSlug,
        };
        if (!string.IsNullOrWhiteSpace(secretPath))
            payload["path"] = secretPath;
        if (!string.IsNullOrWhiteSpace(ttl))
            payload["ttl"] = ttl;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _transport.SendAsync(request, $"create dynamic lease '{dynamicSecretName}'", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, $"create dynamic lease '{dynamicSecretName}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("lease", out var lease) || lease.ValueKind != JsonValueKind.Object)
                throw new InfisicalException(
                    CredentialFailureKind.InvalidResponse,
                    $"Infisical create of dynamic lease '{dynamicSecretName}' returned no lease object.");
            if (!CredentialJson.TryGetString(lease, "id", out var leaseId) || string.IsNullOrEmpty(leaseId))
                throw new InfisicalException(
                    CredentialFailureKind.InvalidResponse,
                    $"Infisical create of dynamic lease '{dynamicSecretName}' returned no lease id.");
            var expiresAt = CredentialJson.TryGetDateTime(lease, "expireAt", out var exp) ? exp : (DateTimeOffset?)null;
            if (expiresAt is null)
                throw new InfisicalException(
                    CredentialFailureKind.InvalidResponse,
                    $"Infisical create of dynamic lease '{dynamicSecretName}' returned no expireAt.");
            var data = ReadDataFields(root, $"dynamic lease '{dynamicSecretName}'");
            _log.LogInformation(
                "Infisical created dynamic lease '{LeaseId}' for '{Name}' expiring {ExpiresAt}.",
                leaseId, dynamicSecretName, expiresAt);
            return new InfisicalDynamicLease(leaseId, expiresAt.Value, data);
        }
    }

    /// <summary>Renews a dynamic lease; returns the new server-side expiry.</summary>
    public async Task<DateTimeOffset> RenewDynamicLeaseAsync(
        string siteUrl,
        string token,
        string serverLeaseId,
        string projectSlug,
        string environmentSlug,
        string secretPath,
        string ttl,
        int maxResponseBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverLeaseId);
        var url = $"{siteUrl.TrimEnd('/')}/api/v1/dynamic-secrets/leases/{Uri.EscapeDataString(serverLeaseId)}/renew";
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["projectSlug"] = projectSlug,
            ["environmentSlug"] = environmentSlug,
        };
        if (!string.IsNullOrWhiteSpace(secretPath))
            payload["path"] = secretPath;
        if (!string.IsNullOrWhiteSpace(ttl))
            payload["ttl"] = ttl;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _transport.SendAsync(request, $"renew dynamic lease '{serverLeaseId}'", ct).ConfigureAwait(false);
        var doc = await _transport.ReadJsonAsync(response, $"renew dynamic lease '{serverLeaseId}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("lease", out var lease)
                || !CredentialJson.TryGetDateTime(lease, "expireAt", out var expiresAt))
                throw new InfisicalException(
                    CredentialFailureKind.InvalidResponse,
                    $"Infisical renew of dynamic lease '{serverLeaseId}' returned no expireAt.");
            _log.LogDebug("Infisical renewed dynamic lease '{LeaseId}' to {ExpiresAt}.", serverLeaseId, expiresAt);
            return expiresAt;
        }
    }

    /// <summary>
    /// Revokes a dynamic lease. Idempotent: a 404 (already gone) counts as
    /// revoked, matching the manager's must-be-idempotent contract.
    /// </summary>
    public async Task RevokeDynamicLeaseAsync(
        string siteUrl,
        string token,
        string serverLeaseId,
        string projectSlug,
        string environmentSlug,
        string secretPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverLeaseId);
        var url = $"{siteUrl.TrimEnd('/')}/api/v1/dynamic-secrets/leases/{Uri.EscapeDataString(serverLeaseId)}";
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["projectSlug"] = projectSlug,
            ["environmentSlug"] = environmentSlug,
        };
        if (!string.IsNullOrWhiteSpace(secretPath))
            payload["path"] = secretPath;
        using var request = new HttpRequestMessage(HttpMethod.Delete, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _transport.SendAsync(request, $"revoke dynamic lease '{serverLeaseId}'", ct).ConfigureAwait(false);
            response.Dispose();
            _log.LogInformation("Infisical revoked dynamic lease '{LeaseId}'.", serverLeaseId);
        }
        catch (InfisicalException ex) when (ex.Kind == CredentialFailureKind.NotFound)
        {
            // Already gone server-side: revocation is complete by definition.
            _log.LogInformation(
                "Infisical dynamic lease '{LeaseId}' was already absent; treating revocation as complete.",
                serverLeaseId);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadDataFields(JsonElement root, string where)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return fields.AsReadOnlyDictionary();
        foreach (var property in data.EnumerateObject())
        {
            fields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => property.Value.GetRawText(),
            };
        }
        return fields.AsReadOnlyDictionary();
    }

}

internal static class DictionaryExtensions
{
    internal static IReadOnlyDictionary<TKey, TValue> AsReadOnlyDictionary<TKey, TValue>(
        this Dictionary<TKey, TValue> source) where TKey : notnull
        => new System.Collections.ObjectModel.ReadOnlyDictionary<TKey, TValue>(source);
}

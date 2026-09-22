using System.Net;
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
    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    /// <summary>Maximum characters kept from a server-echoed error message.</summary>
    public const int MaxServerDetailChars = 200;

    public InfisicalRestClient(HttpClient http, TimeProvider? clock = null, ILogger? log = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
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
        using var response = await SendAsync(request, "universal-auth login", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, "universal-auth login", MaxServerDetailChars, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!TryGetString(root, "accessToken", out var token) || string.IsNullOrEmpty(token))
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse, "Infisical universal-auth login returned no accessToken.");
            var expiresInSeconds = TryGetDouble(root, "expiresIn", out var seconds) ? seconds : 0;
            if (expiresInSeconds <= 0)
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse, "Infisical universal-auth login returned no expiresIn.");
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
        using var response = await SendAsync(request, $"fetch secret '{secretKey}'", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, $"fetch secret '{secretKey}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("secret", out var secret)
                || secret.ValueKind != JsonValueKind.Object)
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse,
                    $"Infisical fetch of secret '{secretKey}' returned no secret object.");
            if (!TryGetString(secret, "secretValue", out var value) || value is null)
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse,
                    $"Infisical fetch of secret '{secretKey}' returned no secretValue.");
            TryGetString(secret, "id", out var id);
            var version = TryGetInt64(secret, "version", out var v) ? v : 0;
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
        using var response = await SendAsync(request, $"create dynamic lease '{dynamicSecretName}'", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, $"create dynamic lease '{dynamicSecretName}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("lease", out var lease) || lease.ValueKind != JsonValueKind.Object)
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse,
                    $"Infisical create of dynamic lease '{dynamicSecretName}' returned no lease object.");
            if (!TryGetString(lease, "id", out var leaseId) || string.IsNullOrEmpty(leaseId))
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse,
                    $"Infisical create of dynamic lease '{dynamicSecretName}' returned no lease id.");
            var expiresAt = TryGetDateTime(lease, "expireAt", out var exp) ? exp : (DateTimeOffset?)null;
            if (expiresAt is null)
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse,
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
        using var response = await SendAsync(request, $"renew dynamic lease '{serverLeaseId}'", ct).ConfigureAwait(false);
        var doc = await ReadJsonAsync(response, $"renew dynamic lease '{serverLeaseId}'", maxResponseBytes, ct).ConfigureAwait(false);
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("lease", out var lease)
                || !TryGetDateTime(lease, "expireAt", out var expiresAt))
                throw new InfisicalException(
                    InfisicalFailureKind.InvalidResponse,
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
            using var response = await SendAsync(request, $"revoke dynamic lease '{serverLeaseId}'", ct).ConfigureAwait(false);
            response.Dispose();
            _log.LogInformation("Infisical revoked dynamic lease '{LeaseId}'.", serverLeaseId);
        }
        catch (InfisicalException ex) when (ex.Kind == InfisicalFailureKind.NotFound)
        {
            // Already gone server-side: revocation is complete by definition.
            _log.LogInformation(
                "Infisical dynamic lease '{LeaseId}' was already absent; treating revocation as complete.",
                serverLeaseId);
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
            throw new InfisicalException(
                InfisicalFailureKind.Unreachable, $"Infisical {operation} timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new InfisicalException(
                InfisicalFailureKind.Unreachable, $"Infisical {operation} could not reach the backend.", ex);
        }

        if (InfisicalHttpClients.IsRedirect(response.StatusCode))
        {
            // Never follow: the backend's 3xx (and its Location) is
            // untrusted runtime output, and re-sending would carry the
            // bearer token or client secret to the redirect target. Fail
            // closed as a backend fault — never a verdict on the item.
            var redirect = (int)response.StatusCode;
            response.Dispose();
            throw new InfisicalException(
                InfisicalFailureKind.InvalidResponse,
                $"Infisical {operation} returned redirect HTTP {redirect}; refusing to follow.",
                redirect);
        }

        if (response.RequestMessage?.RequestUri is { } finalUri
            && request.RequestUri is { } originalUri
            && !InfisicalHttpClients.IsSameOrigin(finalUri, originalUri))
        {
            // The handler followed a redirect before this code saw the
            // response (only possible with an externally supplied
            // following client — the plugin builds non-following ones).
            // The credential may already have been re-sent off-origin,
            // so fail loudly rather than trusting this response.
            var followedStatus = (int)response.StatusCode;
            response.Dispose();
            throw new InfisicalException(
                InfisicalFailureKind.InvalidResponse,
                $"Infisical {operation} was redirected to another origin; refusing the response.",
                followedStatus);
        }

        if (response.IsSuccessStatusCode)
            return response;

        var status = (int)response.StatusCode;
        var detail = await ReadErrorDetailAsync(response, ct).ConfigureAwait(false);
        var retryAfter = ParseRetryAfter(response);
        response.Dispose();
        throw InfisicalException.FromStatus(status, operation, detail, retryAfter);
    }

    private async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response, string operation, int maxBytes, CancellationToken ct)
    {
        byte[] body;
        try
        {
            body = await ReadBoundedAsync(response, maxBytes, ct).ConfigureAwait(false);
        }
        catch (InfisicalException)
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
            throw new InfisicalException(
                InfisicalFailureKind.InvalidResponse,
                $"Infisical {operation} returned a non-JSON success body.", ex);
        }
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new InfisicalException(
                InfisicalFailureKind.InvalidResponse,
                $"Infisical {operation} returned an unexpected JSON shape.");
        }
        return doc;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > maxBytes)
            throw new InfisicalException(
                InfisicalFailureKind.InvalidResponse,
                $"Infisical response declares {contentLength.Value} bytes, above the {maxBytes}-byte cap.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        // Cap BEFORE buffering: a lying or missing content-length can never
        // fill host memory. The shared copy stops past the cap; over-cap
        // stays a typed backend failure.
        var (bytes, truncated) = await CredentialBodies.CopyCappedAsync(stream, maxBytes, ct).ConfigureAwait(false);
        if (truncated)
            throw new InfisicalException(
                InfisicalFailureKind.InvalidResponse,
                $"Infisical response exceeds the {maxBytes}-byte cap.");
        return bytes;
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var (bytes, _) = await CredentialBodies.CopyCappedAsync(stream, 2048, ct).ConfigureAwait(false);
            text = Encoding.UTF8.GetString(bytes);
        }
        catch (Exception)
        {
            return "no readable error body";
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                var detail = message.GetString() ?? string.Empty;
                return detail.Length <= MaxServerDetailChars ? detail : detail[..MaxServerDetailChars];
            }
        }
        catch (JsonException)
        {
            // Fall through to the truncated raw text (status context only).
        }
        var flat = text.Replace('\n', ' ').Replace('\r', ' ');
        return flat.Length <= MaxServerDetailChars ? flat : flat[..MaxServerDetailChars];
    }

    private int? ParseRetryAfter(HttpResponseMessage response)
        => CredentialRetryAfter.Parse(response, _clock);

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

    private static bool TryGetDouble(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value);
    }

    private static bool TryGetDateTime(JsonElement element, string name, out DateTimeOffset value)
    {
        value = default;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
            return false;
        var text = property.GetString();
        return !string.IsNullOrEmpty(text)
            && DateTimeOffset.TryParse(text, out value);
    }
}

internal static class DictionaryExtensions
{
    internal static IReadOnlyDictionary<TKey, TValue> AsReadOnlyDictionary<TKey, TValue>(
        this Dictionary<TKey, TValue> source) where TKey : notnull
        => new System.Collections.ObjectModel.ReadOnlyDictionary<TKey, TValue>(source);
}

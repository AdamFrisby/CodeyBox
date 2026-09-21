using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.PlaneWorkSyncPlugin;

/// <summary>
/// Resolves the Plane credential for REST calls. Two modes, selected by
/// configuration rather than by content:
/// <list type="bullet">
/// <item>Static personal access token: read from the env var named by
/// <c>TokenEnvVar</c> on every call (no caching, so host-side rotation
/// propagates without a restart). Sent as <c>X-API-Key</c>.</item>
/// <item>OAuth: when the client-id/secret/refresh-token env vars are all
/// present, the access token is refreshed via the OAuth token endpoint and
/// cached until one minute before expiry. Sent as <c>Authorization: Bearer</c>.
/// Refresh is single-flighted so concurrent callers share one exchange.</item>
/// </list>
/// <para>Values come only from the injected environment reader (production:
/// process environment populated from the host credential chain). Raw values
/// are never logged and never appear in exception messages.</para>
/// </summary>
public sealed class PlaneTokenProvider : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<string, string?> _env;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _refreshAt = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <param name="http">Shared HTTP client (timeout applied by the caller).</param>
    /// <param name="env">Environment reader; defaults to process environment.</param>
    /// <param name="clock">Injectable clock for deterministic tests.</param>
    public PlaneTokenProvider(
        HttpClient http,
        Func<string, string?>? env = null,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _env = env ?? Environment.GetEnvironmentVariable;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when OAuth refresh is fully configured (all three env vars present).</summary>
    public bool IsOAuthConfigured(PlaneWorkSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrWhiteSpace(Read(options.OAuthClientIdEnvVar))
            && !string.IsNullOrWhiteSpace(Read(options.OAuthClientSecretEnvVar))
            && !string.IsNullOrWhiteSpace(Read(options.OAuthRefreshTokenEnvVar));
    }

    /// <summary>
    /// Returns the credential for Plane API calls, applying the scheme Plane
    /// expects for the configured mode: <c>X-API-Key</c> for static tokens,
    /// <c>Bearer</c> for OAuth access tokens.
    /// Throws <see cref="InvalidOperationException"/> when no credential is
    /// configured (operator misconfiguration — message names the env var,
    /// never its value).
    /// </summary>
    public async Task<PlaneCredential> GetCredentialAsync(
        PlaneWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsOAuthConfigured(options))
            return new PlaneCredential(
                Scheme: "Bearer",
                Value: await GetOAuthTokenAsync(options, ct).ConfigureAwait(false));

        var token = Read(options.TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"Plane work sync is not authenticated: environment variable '{options.TokenEnvVar}' is empty. " +
                "Set it from the host credential chain (vault agent, container secret, or static key).");
        return new PlaneCredential(Scheme: "X-API-Key", Value: token);
    }

    private async Task<string> GetOAuthTokenAsync(PlaneWorkSyncOptions options, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_cachedAccessToken is not null && now < _refreshAt)
            return _cachedAccessToken;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = _clock.GetUtcNow();
            if (_cachedAccessToken is not null && now < _refreshAt)
                return _cachedAccessToken;

            var clientId = Read(options.OAuthClientIdEnvVar);
            var clientSecret = Read(options.OAuthClientSecretEnvVar);
            var refreshToken = Read(options.OAuthRefreshTokenEnvVar);
            if (string.IsNullOrWhiteSpace(clientId)
                || string.IsNullOrWhiteSpace(clientSecret)
                || string.IsNullOrWhiteSpace(refreshToken))
                throw new InvalidOperationException(
                    "Plane OAuth is partially configured: client id, secret, and refresh token env vars must all be set.");

            using var request = new HttpRequestMessage(HttpMethod.Post, options.OAuthTokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["refresh_token"] = refreshToken,
                }),
            };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content
                .ReadFromJsonAsync<PlaneOAuthTokenResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
                throw new InvalidOperationException("Plane OAuth refresh returned an empty access token.");
            if (payload.ExpiresInSeconds <= 120)
                throw new InvalidOperationException("Plane OAuth refresh returned an unusable expiry.");

            _cachedAccessToken = payload.AccessToken;
            _refreshAt = now.AddSeconds(payload.ExpiresInSeconds - 60);
            return payload.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private string? Read(string name) =>
        string.IsNullOrWhiteSpace(name) ? null : _env(name);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _refreshLock.Dispose();
    }

    private sealed record PlaneOAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] long ExpiresInSeconds);
}

/// <summary>Credential plus the header scheme Plane expects for it.</summary>
public sealed record PlaneCredential(string Scheme, string Value);

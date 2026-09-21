using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.LinearWorkSyncPlugin;

/// <summary>
/// Resolves the Linear bearer token for GraphQL calls. Two modes, selected by
/// configuration rather than by content:
/// <list type="bullet">
/// <item>Static API key: read from the env var named by
/// <c>TokenEnvVar</c> on every call (no caching, so host-side rotation
/// propagates without a restart).</item>
/// <item>OAuth: when the client-id/secret/refresh-token env vars are all
/// present, the access token is refreshed via the OAuth token endpoint and
/// cached until one minute before expiry. Refresh is single-flighted so
/// concurrent callers share one exchange.</item>
/// </list>
/// <para>Values come only from the injected environment reader (production:
/// process environment populated from the host credential chain). Raw values
/// are never logged and never appear in exception messages.</para>
/// </summary>
public sealed class LinearTokenProvider : IDisposable
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
    public LinearTokenProvider(
        HttpClient http,
        Func<string, string?>? env = null,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _env = env ?? Environment.GetEnvironmentVariable;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when OAuth refresh is fully configured (all three env vars present).</summary>
    public bool IsOAuthConfigured(LinearWorkSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrWhiteSpace(Read(options.OAuthClientIdEnvVar))
            && !string.IsNullOrWhiteSpace(Read(options.OAuthClientSecretEnvVar))
            && !string.IsNullOrWhiteSpace(Read(options.OAuthRefreshTokenEnvVar));
    }

    /// <summary>
    /// Returns the bearer token for Linear API calls. Throws
    /// <see cref="InvalidOperationException"/> when no credential is
    /// configured (operator misconfiguration — message names the env var,
    /// never its value).
    /// </summary>
    public async Task<string> GetTokenAsync(
        LinearWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsOAuthConfigured(options))
            return await GetOAuthTokenAsync(options, ct).ConfigureAwait(false);

        var token = Read(options.TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"Linear work sync is not authenticated: environment variable '{options.TokenEnvVar}' is empty. " +
                "Set it from the host credential chain (vault agent, container secret, or static key).");
        return token;
    }

    private async Task<string> GetOAuthTokenAsync(LinearWorkSyncOptions options, CancellationToken ct)
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
                    "Linear OAuth is partially configured: client id, secret, and refresh token env vars must all be set.");

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
                .ReadFromJsonAsync<LinearOAuthTokenResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
                throw new InvalidOperationException("Linear OAuth refresh returned an empty access token.");
            if (payload.ExpiresInSeconds <= 120)
                throw new InvalidOperationException("Linear OAuth refresh returned an unusable expiry.");

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

    private sealed record LinearOAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] long ExpiresInSeconds);
}

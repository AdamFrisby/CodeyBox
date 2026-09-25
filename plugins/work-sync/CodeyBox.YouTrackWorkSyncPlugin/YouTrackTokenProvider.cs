using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace CodeyBox.YouTrackWorkSyncPlugin;

/// <summary>
/// Resolves the YouTrack credential for REST calls. Two modes, selected by
/// configuration rather than by content:
/// <list type="bullet">
/// <item>Permanent token: read from the env var named by
/// <c>TokenEnvVar</c> on every call (no caching, so host-side rotation
/// propagates without a restart). Sent as <c>Authorization: Bearer
/// perm:…</c>. This is YouTrack's recommended auth for service integrations
/// and works identically on Cloud and self-hosted Server.</item>
/// <item>Hub OAuth2 client credentials: when the client-id and
/// client-secret env vars are both present, an access token is requested
/// from the Hub token endpoint (<see
/// cref="YouTrackWorkSyncOptions.ResolvedOAuthTokenUrl"/>) and cached until
/// one minute before expiry. Refresh is single-flighted so concurrent
/// callers share one exchange.</item>
/// </list>
/// <para>Values come only from the injected environment reader (production:
/// process environment populated from the host credential chain). Raw values
/// are never logged and never appear in exception messages.</para>
/// </summary>
public sealed class YouTrackTokenProvider : IDisposable
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
    public YouTrackTokenProvider(
        HttpClient http,
        Func<string, string?>? env = null,
        TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _env = env ?? Environment.GetEnvironmentVariable;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>True when OAuth is fully configured (client id and secret env vars present).</summary>
    public bool IsOAuthConfigured(YouTrackWorkSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrWhiteSpace(Read(options.OAuthClientIdEnvVar))
            && !string.IsNullOrWhiteSpace(Read(options.OAuthClientSecretEnvVar));
    }

    /// <summary>
    /// Returns the credential for YouTrack API calls as
    /// <c>Authorization: Bearer</c> — the scheme is Bearer for both permanent
    /// tokens and OAuth access tokens. Throws <see
    /// cref="InvalidOperationException"/> when no credential is configured
    /// (operator misconfiguration — the message names the env var, never its
    /// value).
    /// </summary>
    public async Task<YouTrackCredential> GetCredentialAsync(
        YouTrackWorkSyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsOAuthConfigured(options))
        {
            return new YouTrackCredential(
                Scheme: "Bearer",
                Value: await GetOAuthTokenAsync(options, ct).ConfigureAwait(false));
        }

        var token = Read(options.TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"YouTrack work sync is not authenticated: environment variable " +
                $"'{options.TokenEnvVar}' must hold a permanent token (perm:…), or the OAuth " +
                "client id and secret env vars must both be set. " +
                "Provision them from the host credential chain (vault agent, container secret).");
        return new YouTrackCredential(Scheme: "Bearer", Value: token);
    }

    private async Task<string> GetOAuthTokenAsync(YouTrackWorkSyncOptions options, CancellationToken ct)
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
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                throw new InvalidOperationException(
                    "YouTrack OAuth is partially configured: client id and secret env vars must both be set.");

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            };
            if (!string.IsNullOrWhiteSpace(options.OAuthScope))
                form["scope"] = options.OAuthScope;

            using var request = new HttpRequestMessage(HttpMethod.Post, options.ResolvedOAuthTokenUrl)
            {
                Content = new FormUrlEncodedContent(form),
            };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content
                .ReadFromJsonAsync<YouTrackOAuthTokenResponse>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
                throw new InvalidOperationException("YouTrack OAuth refresh returned an empty access token.");
            if (payload.ExpiresInSeconds <= 120)
                throw new InvalidOperationException("YouTrack OAuth refresh returned an unusable expiry.");

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

    private sealed record YouTrackOAuthTokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] long ExpiresInSeconds);
}

/// <summary>Credential plus the header scheme YouTrack expects for it.</summary>
public sealed record YouTrackCredential(string Scheme, string Value);

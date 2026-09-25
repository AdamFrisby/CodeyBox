using System.Text.Json;
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
    /// <summary>Upper bound on the token-endpoint response body (bytes) — a token JSON is small.</summary>
    private const long MaxTokenResponseBytes = 64 * 1024;

    /// <summary>Tokens with less lifetime left than this are rejected as unusable.</summary>
    private const long MinTokenLifetimeSeconds = 120;

    /// <summary>Refresh happens this many seconds before the reported expiry.</summary>
    private const long RefreshSkewSeconds = 60;

    private readonly HttpClient _http;
    private readonly Func<string, string?> _env;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _cachedAccessToken;
    private string? _cacheKey;
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
                "Bearer", await GetOAuthTokenAsync(options, ct).ConfigureAwait(false));
        }

        var token = Read(options.TokenEnvVar);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                $"YouTrack work sync is not authenticated: environment variable " +
                $"'{options.TokenEnvVar}' must hold a permanent token (perm:…), or the OAuth " +
                "client id and secret env vars must both be set. " +
                "Provision them from the host credential chain (vault agent, container secret).");
        return new YouTrackCredential("Bearer", token);
    }

    private async Task<string> GetOAuthTokenAsync(YouTrackWorkSyncOptions options, CancellationToken ct)
    {
        var clientId = Read(options.OAuthClientIdEnvVar);
        var clientSecret = Read(options.OAuthClientSecretEnvVar);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException(
                "YouTrack OAuth is partially configured: client id and secret env vars must both be set.");

        var tokenUrl = options.ResolvedOAuthTokenUrl;
        if (!Uri.TryCreate(tokenUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps
                && !(parsed.Scheme == Uri.UriSchemeHttp && options.AllowUnsafeHttp)))
            throw new InvalidOperationException(
                "YouTrack OAuth token endpoint is not an absolute https URL; " +
                "set OAuthTokenUrl or ApiBaseUrl in the plugin configuration " +
                "(plaintext http:// would post the client secret unencrypted and " +
                "requires the dev-only AllowUnsafeHttp=true opt-in).");

        // The cache is keyed to the endpoint and client the token was minted
        // for: a hot-reload retargeting the integration must never replay a
        // token minted for a different host or credential.
        var cacheKey = parsed.AbsoluteUri + "\n" + clientId;
        var now = _clock.GetUtcNow();
        if (_cachedAccessToken is not null
            && string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal)
            && now < _refreshAt)
            return _cachedAccessToken;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = _clock.GetUtcNow();
            if (_cachedAccessToken is not null
                && string.Equals(_cacheKey, cacheKey, StringComparison.Ordinal)
                && now < _refreshAt)
                return _cachedAccessToken;

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
            };
            if (!string.IsNullOrWhiteSpace(options.OAuthScope))
                form["scope"] = options.OAuthScope;

            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
            {
                Content = new FormUrlEncodedContent(form),
            };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await YouTrackRestClient.ReadBoundedStringAsync(
                response.Content, MaxTokenResponseBytes, timeout.Token).ConfigureAwait(false);
            var payload = JsonSerializer.Deserialize<YouTrackOAuthTokenResponse>(json);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken))
                throw new InvalidOperationException("YouTrack OAuth refresh returned an empty access token.");
            if (payload.ExpiresInSeconds <= MinTokenLifetimeSeconds)
                throw new InvalidOperationException("YouTrack OAuth refresh returned an unusable expiry.");

            _cachedAccessToken = payload.AccessToken;
            _cacheKey = cacheKey;
            _refreshAt = now.AddSeconds(payload.ExpiresInSeconds - RefreshSkewSeconds);
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

/// <summary>
/// Credential plus the header scheme YouTrack expects for it. A plain type,
/// not a record: the generated record <c>ToString</c> would print <see
/// cref="Value"/> — a live secret — into any log or exception text.
/// </summary>
public sealed class YouTrackCredential
{
    public YouTrackCredential(string scheme, string value)
    {
        Scheme = scheme;
        Value = value;
    }

    /// <summary>The Authorization header scheme (always <c>Bearer</c>).</summary>
    public string Scheme { get; }

    /// <summary>The token value — a secret; never log or interpolate it.</summary>
    public string Value { get; }
}

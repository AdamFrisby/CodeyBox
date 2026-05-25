using System.Net;
using CodeyBox.Api;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Covers the OAuth-refresh behaviour added to the subscription quota probes
/// in #109. The probes used to forward an expired access_token verbatim; the
/// provider would 401, the snapshot would become "unknown", and the router's
/// default UnknownPolicy=UseObservedFailures would fall open onto an agent
/// that immediately 429s. These tests assert the refresher detects expiry,
/// posts to the OAuth refresh endpoint, persists the new token to disk, and
/// dedupes concurrent refresh attempts.
/// </summary>
public sealed class OauthCredentialFileRefresherTests : IDisposable
{
    private readonly string _dir;

    public OauthCredentialFileRefresherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cb-refresher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string WriteCreds(string fileName, string json)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, json);
        return path;
    }

    // ── Gemini ───────────────────────────────────────────────────────────────

    private static string GeminiCreds(string access, string refresh, long expiryMs)
        => $$"""
        {
          "access_token": "{{access}}",
          "refresh_token": "{{refresh}}",
          "client_id": "test-client",
          "client_secret": "test-secret",
          "expiry_date": {{expiryMs}}
        }
        """;

    [Fact]
    public async Task Gemini_Refresh_WhenExpired_PostsRefreshAndReturnsNewToken()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old-access", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"new-access","expires_in":3600}""");
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("new-access", token);
        Assert.Single(handler.Requests);
        var form = handler.Requests[0];
        Assert.Contains("grant_type=refresh_token", form);
        Assert.Contains("refresh_token=rt-1", form);
        Assert.Contains("client_id=test-client", form);

        // File on disk now carries the new access_token and a future expiry.
        var updated = File.ReadAllText(path);
        Assert.Contains("\"new-access\"", updated);
        Assert.Contains("\"refresh_token\": \"rt-1\"", updated);
    }

    [Fact]
    public async Task Gemini_Refresh_WhenFresh_DoesNotCallEndpoint()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("good-token", "rt-1", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("good-token", token);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Gemini_Refresh_Failure_LogsOnceWithinSuppressionWindow()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
        var log = new CountingLogger<GeminiOauthCredentialFileRefresher>();
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            log);

        for (int i = 0; i < 5; i++)
            Assert.Null(await refresher.GetAccessTokenAsync());

        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(1, log.WarningCount);
    }

    [Fact]
    public async Task Gemini_Refresh_ConcurrentCalls_PerformSingleRoundTrip()
    {
        var path = WriteCreds("oauth_creds.json",
            GeminiCreds("old", "rt-1", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new GeminiOAuthCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"new-access","expires_in":3600}""");
        // Slow the handler so all callers queue at the refresh gate before the
        // first one's response returns.
        handler.ResponseDelay = TimeSpan.FromMilliseconds(150);
        using var refresher = new GeminiOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<GeminiOauthCredentialFileRefresher>.Instance);

        var calls = Enumerable.Range(0, 10).Select(_ => refresher.GetAccessTokenAsync()).ToArray();
        await Task.WhenAll(calls);

        Assert.All(calls, t => Assert.Equal("new-access", t.Result));
        Assert.Single(handler.Requests);
    }

    // ── Claude ───────────────────────────────────────────────────────────────

    private static string ClaudeCreds(string access, string refresh, long expiresAtMs)
        => $$"""
        { "claudeAiOauth": { "accessToken": "{{access}}", "refreshToken": "{{refresh}}", "expiresAt": {{expiresAtMs}} } }
        """;

    [Fact]
    public async Task Claude_Refresh_WhenExpired_PostsRefreshAndReturnsNewToken()
    {
        var path = WriteCreds(".credentials.json",
            ClaudeCreds("old", "rt-claude", DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            """{"access_token":"sk-ant-new","expires_in":28800}""");
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        var token = await refresher.GetAccessTokenAsync();

        Assert.Equal("sk-ant-new", token);
        Assert.Single(handler.Requests);
        var body = handler.Requests[0];
        Assert.Contains("\"grant_type\":\"refresh_token\"", body);
        Assert.Contains("\"refresh_token\":\"rt-claude\"", body);

        var updated = File.ReadAllText(path);
        Assert.Contains("\"sk-ant-new\"", updated);
        Assert.Contains("\"refreshToken\": \"rt-claude\"", updated);
    }

    [Fact]
    public async Task Claude_Refresh_WhenFresh_DoesNotCallEndpoint()
    {
        var path = WriteCreds(".credentials.json",
            ClaudeCreds("good", "rt-claude", DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds()));
        using var source = new ClaudeCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        using var refresher = new ClaudeOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<ClaudeOauthCredentialFileRefresher>.Instance);

        Assert.Equal("good", await refresher.GetAccessTokenAsync());
        Assert.Empty(handler.Requests);
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build a JWT-shaped access_token with the given expiry claim (signature
    /// payload is irrelevant — the refresher only reads <c>exp</c>).
    /// </summary>
    private static string CodexAccessJwt(long expSeconds)
    {
        static string Encode(string json)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        var header = Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Encode($$"""{"exp":{{expSeconds}},"sub":"u","account_id":"acct-1"}""");
        return $"{header}.{payload}.";
    }

    private static string CodexCreds(string accessJwt, string refresh, string accountId)
        => $$"""
        {
          "tokens": {
            "id_token": "id-1",
            "access_token": "{{accessJwt}}",
            "refresh_token": "{{refresh}}",
            "account_id": "{{accountId}}"
          },
          "last_refresh": "2024-01-01T00:00:00Z"
        }
        """;

    [Fact]
    public async Task Codex_Refresh_WhenExpired_PostsRefreshAndReturnsNewToken()
    {
        var expiredExp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        var freshExp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var path = WriteCreds("auth.json", CodexCreds(CodexAccessJwt(expiredExp), "rt-codex", "acct-1"));
        using var source = new CodexCredentialFileSource(path, watch: false);
        var newJwt = CodexAccessJwt(freshExp);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK,
            $$"""{"access_token":"{{newJwt}}","refresh_token":"rt-codex-2","expires_in":3600}""");
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance);

        var (token, accountId) = await refresher.GetTokensAsync();

        Assert.Equal(newJwt, token);
        Assert.Equal("acct-1", accountId);
        Assert.Single(handler.Requests);

        var updated = File.ReadAllText(path);
        Assert.Contains(newJwt, updated);
        Assert.Contains("rt-codex-2", updated);
    }

    [Fact]
    public async Task Codex_Refresh_WhenFresh_DoesNotCallEndpoint()
    {
        var freshExp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var jwt = CodexAccessJwt(freshExp);
        var path = WriteCreds("auth.json", CodexCreds(jwt, "rt-codex", "acct-1"));
        using var source = new CodexCredentialFileSource(path, watch: false);
        var handler = new RefresherCapturingHandler(HttpStatusCode.OK, "{}");
        using var refresher = new CodexOauthCredentialFileRefresher(
            source,
            new RefresherFakeHttpClientFactory("agent-quota", handler),
            NullLogger<CodexOauthCredentialFileRefresher>.Instance);

        var (token, accountId) = await refresher.GetTokensAsync();

        Assert.Equal(jwt, token);
        Assert.Equal("acct-1", accountId);
        Assert.Empty(handler.Requests);
    }
}

internal sealed class RefresherFakeHttpClientFactory : IHttpClientFactory
{
    private readonly string _clientName;
    private readonly HttpMessageHandler _handler;
    public RefresherFakeHttpClientFactory(string clientName, HttpMessageHandler handler)
    {
        _clientName = clientName;
        _handler = handler;
    }
    public HttpClient CreateClient(string name)
    {
        if (name != _clientName)
            throw new InvalidOperationException($"Unexpected client name '{name}'; expected '{_clientName}'");
        return new HttpClient(_handler, disposeHandler: false);
    }
}

internal sealed class RefresherCapturingHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;
    private readonly string _body;
    public List<string> Requests { get; } = new();
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;
    private readonly object _gate = new();

    public RefresherCapturingHandler(HttpStatusCode status, string body)
    {
        _status = status;
        _body = body;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Capture the request body as a string now — the refresher disposes the
        // HttpRequestMessage as soon as SendAsync returns, taking the content
        // stream with it.
        var bodyText = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        lock (_gate) Requests.Add(bodyText);
        if (ResponseDelay > TimeSpan.Zero)
            await Task.Delay(ResponseDelay, ct).ConfigureAwait(false);
        return new HttpResponseMessage(_status)
        {
            Content = new StringContent(_body),
        };
    }
}

internal sealed class CountingLogger<T> : ILogger<T>
{
    public int WarningCount;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Warning) Interlocked.Increment(ref WarningCount);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

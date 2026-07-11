using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClaudeSmokeProbe"/> using a fake HTTP handler.
/// Verifies request shape, auth header selection, status-code classification,
/// timeout handling, and credential-absent short-circuit.
/// </summary>
public sealed class ClaudeSmokeProbeTests
{
    private static AgentCredential OAuthCred(string token = "test-oauth-token") =>
        new(AgentKind.Claude,
            new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token },
            new Dictionary<string, string>());

    private static AgentCredential ApiKeyCred(string key = "test-api-key") =>
        new(AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = key },
            new Dictionary<string, string>());

    private static AgentCredential EmptyCred() =>
        new(AgentKind.Claude,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    private static ClaudeSmokeProbe BuildProbe(
        HttpMessageHandler handler,
        TimeProvider? timeProvider = null,
        Func<TimeSpan>? maxRetryDelayProvider = null) =>
        new(new SmokeFakeHttpClientFactory("agent-smoke", handler),
            NullLogger<ClaudeSmokeProbe>.Instance,
            timeProvider,
            maxRetryDelayProvider);

    private static void AssertOAuthUsageRetryRequests(RetryAfterSequenceHandler handler, string token)
    {
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri(ClaudeSmokeProbe.OAuthUsageEndpoint), request.RequestUri);
            Assert.Equal($"Bearer {token}", request.Authorization);
        });
    }

    // ── Endpoint URL ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Probe_PostsToMessagesEndpoint()
    {
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), CancellationToken.None);
        Assert.Equal(new Uri(ClaudeSmokeProbe.MessagesEndpoint), captured);
    }

    // ── Authorization header ──────────────────────────────────────────────────

    [Fact]
    public async Task OAuthToken_ProbesUsageEndpoint_NotRawMessages()
    {
        // A subscription OAuth token is validated via the OAuth-native usage
        // endpoint (Bearer), NOT a raw /v1/messages inference call (ToS/account risk).
        Uri? captured = null;
        string? auth = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
        {
            captured = req.RequestUri;
            auth = req.Headers.Authorization?.ToString();
        });
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred("my-oauth"), CancellationToken.None);
        Assert.Equal(new Uri(ClaudeSmokeProbe.OAuthUsageEndpoint), captured);
        Assert.Equal("Bearer my-oauth", auth);
        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task ApiKey_SendsXApiKeyHeader()
    {
        string? xApiKey = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            xApiKey = req.Headers.TryGetValues("x-api-key", out var vals) ? string.Join("", vals) : null);
        await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("my-api-key"), CancellationToken.None);
        Assert.Equal("my-api-key", xApiKey);
    }

    [Fact]
    public async Task GuardAgainstRawMessagesWithOAuthBearer_OverAllOAuthShapes()
    {
        // Account-safety regression guard: under no OAuth credential shape may the
        // smoke probe ever construct a POST https://api.anthropic.com/v1/messages
        // request with an Authorization: Bearer header — that pattern is what risks
        // Anthropic terminating the subscription account. The fix routes OAuth via
        // /api/oauth/usage instead; this test re-runs every OAuth-bearing shape we
        // recognise so a future refactor that re-introduces the misuse fails here.
        var oauthOnly = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string> { ["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth-only" },
            new Dictionary<string, string>());
        var oauthPlusApiKey = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string>
            {
                ["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth-pref",
                ["ANTHROPIC_API_KEY"] = "fallback-key",
            },
            new Dictionary<string, string>());

        foreach (var cred in new[] { oauthOnly, oauthPlusApiKey })
        {
            HttpRequestMessage? captured = null;
            var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req);
            await BuildProbe(handler).SmokeTestAsync(cred, CancellationToken.None);

            Assert.NotNull(captured);
            var isMessagesEndpoint = string.Equals(
                captured!.RequestUri?.AbsoluteUri,
                ClaudeSmokeProbe.MessagesEndpoint,
                StringComparison.Ordinal);
            var isBearer = string.Equals(
                captured.Headers.Authorization?.Scheme,
                "Bearer",
                StringComparison.OrdinalIgnoreCase);

            // The forbidden combination is BOTH at once.
            Assert.False(isMessagesEndpoint && isBearer,
                $"Smoke probe constructed POST {ClaudeSmokeProbe.MessagesEndpoint} with a Bearer Authorization header — this is exactly the subscription-OAuth misuse that risks Anthropic terminating the account. See ClaudeSmokeProbe.cs.");
        }
    }

    [Fact]
    public async Task OAuthPresent_UsesUsageEndpoint_NotRawMessages_EvenWithApiKey()
    {
        // When an OAuth subscription token is present it takes precedence: validate
        // via the usage endpoint, never POST /v1/messages (with the OAuth token or
        // by falling through to the API key).
        Uri? captured = null;
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string>
            {
                ["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth-tok",
                ["ANTHROPIC_API_KEY"] = "api-key",
            },
            new Dictionary<string, string>());

        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        var result = await BuildProbe(handler).SmokeTestAsync(cred, CancellationToken.None);
        Assert.Equal(new Uri(ClaudeSmokeProbe.OAuthUsageEndpoint), captured);
        Assert.NotEqual(new Uri(ClaudeSmokeProbe.MessagesEndpoint), captured);
        Assert.True(result.Ok);
    }

    // ── Status-code classification ─────────────────────────────────────────────

    [Fact]
    public async Task Http200_ReturnsOk()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Http401_ReturnsFail_Auth()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Http403_ReturnsFail_Auth()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.Forbidden, "", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Http500_ReturnsFail_Transient()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task Http429_ReturnsFail_WithStatusCode()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.TooManyRequests, "", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("429", result.FailureReason);
    }

    [Fact]
    public async Task OAuthUsage429_WithRetryAfter_WaitsAndRetries()
    {
        var time = new CapturingDelayTimeProvider(DateTimeOffset.UtcNow);
        var handler = new RetryAfterSequenceHandler(
            new RetryAfterResponse(HttpStatusCode.TooManyRequests, "", TimeSpan.FromSeconds(11)),
            new RetryAfterResponse(HttpStatusCode.OK, "{}", null));

        var result = await BuildProbe(handler, time).SmokeTestAsync(OAuthCred("oauth"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, handler.CallCount);
        AssertOAuthUsageRetryRequests(handler, "oauth");
        var delay = Assert.Single(time.Delays);
        Assert.True(delay >= TimeSpan.FromSeconds(11), $"delay was {delay}");
    }

    [Fact]
    public async Task OAuthUsage503_WithRetryAfter_WaitsAndRetries()
    {
        var time = new CapturingDelayTimeProvider(DateTimeOffset.UtcNow);
        var handler = new RetryAfterSequenceHandler(
            new RetryAfterResponse(HttpStatusCode.ServiceUnavailable, "", TimeSpan.FromSeconds(7)),
            new RetryAfterResponse(HttpStatusCode.OK, "{}", null));

        var result = await BuildProbe(handler, time).SmokeTestAsync(OAuthCred("oauth"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(time.Delays));
    }

    [Fact]
    public async Task OAuthUsage429_WithLargeRetryAfter_CapsDelayFromOptions()
    {
        var time = new CapturingDelayTimeProvider(DateTimeOffset.UtcNow);
        var handler = new RetryAfterSequenceHandler(
            new RetryAfterResponse(HttpStatusCode.TooManyRequests, "", TimeSpan.FromMinutes(10)),
            new RetryAfterResponse(HttpStatusCode.OK, "{}", null));

        var result = await BuildProbe(
                handler,
                time,
                () => TimeSpan.FromSeconds(30))
            .SmokeTestAsync(OAuthCred("oauth"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(time.Delays));
    }

    // ── Network error ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NetworkException_ReturnsFail_Transient()
    {
        var handler = new SmokeThrowingHandler(new HttpRequestException("network failure"));
        var result = await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    // ── Timeout ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_ReturnsFail_Timeout()
    {
        var handler = new SmokeHangingHandler();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), cts.Token);
        Assert.False(result.Ok);
        Assert.Equal("timeout", result.FailureReason);
    }

    // ── No token ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoToken_ReturnsFail_WithoutHttpCall()
    {
        int calls = 0;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => calls++);
        var result = await BuildProbe(handler).SmokeTestAsync(EmptyCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("no token", result.FailureReason);
        Assert.Equal(0, calls);
    }

    // ── Duration ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Duration_IsPopulated()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(ApiKeyCred("k"), CancellationToken.None);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }
}

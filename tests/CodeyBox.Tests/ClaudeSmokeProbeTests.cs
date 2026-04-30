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

    private static ClaudeSmokeProbe BuildProbe(HttpMessageHandler handler) =>
        new(new SmokeFakeHttpClientFactory("agent-smoke", handler),
            NullLogger<ClaudeSmokeProbe>.Instance);

    // ── Endpoint URL ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Probe_PostsToMessagesEndpoint()
    {
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.Equal(new Uri(ClaudeSmokeProbe.MessagesEndpoint), captured);
    }

    // ── Authorization header ──────────────────────────────────────────────────

    [Fact]
    public async Task OAuthToken_SendsBearerHeader()
    {
        string? auth = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            auth = req.Headers.Authorization?.ToString());
        await BuildProbe(handler).SmokeTestAsync(OAuthCred("my-oauth"), CancellationToken.None);
        Assert.Equal("Bearer my-oauth", auth);
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
    public async Task OAuthTokenPreferredOverApiKey()
    {
        string? auth = null;
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string>
            {
                ["CLAUDE_CODE_OAUTH_TOKEN"] = "oauth-tok",
                ["ANTHROPIC_API_KEY"] = "api-key",
            },
            new Dictionary<string, string>());

        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            auth = req.Headers.Authorization?.ToString());
        await BuildProbe(handler).SmokeTestAsync(cred, CancellationToken.None);
        Assert.Equal("Bearer oauth-tok", auth);
    }

    // ── Status-code classification ─────────────────────────────────────────────

    [Fact]
    public async Task Http200_ReturnsOk()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.True(result.Ok);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Http401_ReturnsFail_Auth()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Http403_ReturnsFail_Auth()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.Forbidden, "", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Http500_ReturnsFail_Transient()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task Http429_ReturnsFail_WithStatusCode()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.TooManyRequests, "", _ => { });
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("429", result.FailureReason);
    }

    // ── Network error ─────────────────────────────────────────────────────────

    [Fact]
    public async Task NetworkException_ReturnsFail_Transient()
    {
        var handler = new SmokeThrowingHandler(new HttpRequestException("network failure"));
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    // ── Timeout ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancellation_ReturnsFail_Timeout()
    {
        var handler = new SmokeHangingHandler();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), cts.Token);
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
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.True(result.Duration >= TimeSpan.Zero);
    }
}

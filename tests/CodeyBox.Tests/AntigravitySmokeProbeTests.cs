using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AntigravitySmokeProbe"/> using a fake HTTP handler.
/// </summary>
public sealed class AntigravitySmokeProbeTests
{
    private static AgentCredential OAuthCred(string accessToken)
    {
        var env = new Dictionary<string, string>
        {
            [AntigravityConstants.OAuthCredsEnvVar] = $$$"""{"auth_method":"consumer","token":{"access_token":"{{{accessToken}}}","refresh_token":"rt","expiry":"2099-01-01T00:00:00Z"}}"""
        };
        return new(AgentKind.Antigravity, env, new Dictionary<string, string>());
    }

    private static AgentCredential FlatOAuthCred(string accessToken)
    {
        var env = new Dictionary<string, string>
        {
            [AntigravityConstants.OAuthCredsEnvVar] = $$$"""{"access_token":"{{{accessToken}}}"}"""
        };
        return new(AgentKind.Antigravity, env, new Dictionary<string, string>());
    }

    private static AgentCredential EmptyCred() =>
        new(AgentKind.Antigravity,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    private static AgentCredential OAuthCredMissingAccessToken() =>
        new(AgentKind.Antigravity,
            new Dictionary<string, string>
            {
                [AntigravityConstants.OAuthCredsEnvVar] = """{"auth_method":"consumer","token":{"refresh_token":"rt"}}"""
            },
            new Dictionary<string, string>());

    private static AntigravitySmokeProbe BuildProbe(HttpMessageHandler handler) =>
        new(new SmokeFakeHttpClientFactory("agent-smoke", handler),
            NullLogger<AntigravitySmokeProbe>.Instance);

    [Fact]
    public void Kind_IsAntigravity()
    {
        Assert.Equal(AgentKind.Antigravity, new AntigravitySmokeProbe(
            new SmokeFakeHttpClientFactory("agent-smoke", new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { })),
            NullLogger<AntigravitySmokeProbe>.Instance).Kind);
    }

    [Fact]
    public async Task OAuthProbe_PostsToLoadCodeAssistEndpoint()
    {
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        await BuildProbe(handler).SmokeTestAsync(OAuthCred("oauth-token"), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(new Uri(AntigravitySmokeProbe.LoadCodeAssistEndpoint), captured);
    }

    [Fact]
    public async Task OAuthProbe_SendsBearerHeader()
    {
        string? authHeader = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            authHeader = req.Headers.Authorization?.ToString());
        await BuildProbe(handler).SmokeTestAsync(OAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.Equal("Bearer agy-oauth-token", authHeader);
    }

    [Fact]
    public async Task OAuthProbe_SendsBearerHeader_FlatShape()
    {
        string? authHeader = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            authHeader = req.Headers.Authorization?.ToString());
        await BuildProbe(handler).SmokeTestAsync(FlatOAuthCred("flat-oauth-token"), CancellationToken.None);
        Assert.Equal("Bearer flat-oauth-token", authHeader);
    }

    [Fact]
    public async Task OAuthToken_Http200_ReturnsOk()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { }))
            .SmokeTestAsync(OAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task OAuthToken_Http401_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { }))
            .SmokeTestAsync(OAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task OAuthToken_Http403_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Forbidden, "", _ => { }))
            .SmokeTestAsync(OAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task OAuthToken_Http500_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { }))
            .SmokeTestAsync(OAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task OAuthNetworkException_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeThrowingHandler(new HttpRequestException("timeout")))
            .SmokeTestAsync(OAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task OAuthCancellation_ReturnsFail_Timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await BuildProbe(new SmokeHangingHandler())
            .SmokeTestAsync(OAuthCred("agy-oauth-token"), cts.Token);
        Assert.False(result.Ok);
        Assert.Equal("timeout", result.FailureReason);
    }

    [Fact]
    public async Task NoToken_ReturnsFail_WithoutHttpCall()
    {
        int calls = 0;
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => calls++))
            .SmokeTestAsync(EmptyCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("no token", result.FailureReason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OAuthCredsJson_MissingAccessToken_FallsThroughToNoToken()
    {
        int calls = 0;
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => calls++))
            .SmokeTestAsync(OAuthCredMissingAccessToken(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("no token", result.FailureReason);
        Assert.Equal(0, calls);
    }

    // ── ExtractAccessToken edge cases ──────────────────────────────────────

    [Fact]
    public void ExtractAccessToken_MalformedJson_ReturnsNull()
    {
        var result = AntigravitySmokeProbe.ExtractAccessToken("not valid json at all");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractAccessToken_AccessTokenIsNonString_ReturnsNull()
    {
        var json = """{"access_token":12345,"refresh_token":"rt"}""";
        var result = AntigravitySmokeProbe.ExtractAccessToken(json);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractAccessToken_EmptyString_ReturnsNull()
    {
        var result = AntigravitySmokeProbe.ExtractAccessToken("");
        Assert.Null(result);
    }
}

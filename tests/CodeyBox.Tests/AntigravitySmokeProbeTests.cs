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

    private static AgentCredential NestedAccessTokenOnlyCred(string accessToken)
    {
        var env = new Dictionary<string, string>
        {
            [AntigravityConstants.OAuthCredsEnvVar] = $$$"""{"auth_method":"consumer","token":{"access_token":"{{{accessToken}}}","expiry":"2099-01-01T00:00:00Z"}}"""
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
        await BuildProbe(handler).SmokeTestAsync(FlatOAuthCred("oauth-token"), CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(new Uri(AntigravitySmokeProbe.LoadCodeAssistEndpoint), captured);
    }

    [Fact]
    public async Task OAuthProbe_SendsBearerHeader()
    {
        string? authHeader = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            authHeader = req.Headers.Authorization?.ToString());
        await BuildProbe(handler).SmokeTestAsync(FlatOAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.Equal("Bearer agy-oauth-token", authHeader);
    }

    [Fact]
    public async Task OAuthProbe_SendsJsonLoadCodeAssistBody()
    {
        string? body = null;
        string? contentType = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
        {
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            contentType = req.Content.Headers.ContentType?.MediaType;
        });
        await BuildProbe(handler).SmokeTestAsync(FlatOAuthCred("flat-oauth-token"), CancellationToken.None);
        Assert.Equal("""{"metadata":{"pluginType":"GEMINI"}}""", body);
        Assert.Equal("application/json", contentType);
    }

    [Fact]
    public async Task RefreshableOAuthBundle_StillValidatesAccessTokenWithHttpCall()
    {
        var calls = 0;
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => calls++))
            .SmokeTestAsync(OAuthCred("valid-access-token"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NestedAccessTokenOnlyOAuthBundle_Http200_ReturnsOk()
    {
        string? authHeader = null;
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            authHeader = req.Headers.Authorization?.ToString()))
            .SmokeTestAsync(NestedAccessTokenOnlyCred("nested-access-token"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Bearer nested-access-token", authHeader);
    }

    [Fact]
    public async Task OAuthToken_Http200_ReturnsOk()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { }))
            .SmokeTestAsync(FlatOAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task OAuthToken_Http401_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { }))
            .SmokeTestAsync(FlatOAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task OAuthToken_Http403_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Forbidden, "", _ => { }))
            .SmokeTestAsync(FlatOAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task OAuthToken_Http500_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { }))
            .SmokeTestAsync(FlatOAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Transient, result.Category);
    }

    [Fact]
    public async Task OAuthToken_Http400_ReturnsFail_Unknown()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.BadRequest, "", _ => { }))
            .SmokeTestAsync(FlatOAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("HTTP 400", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Unknown, result.Category);
    }

    [Fact]
    public async Task OAuthNetworkException_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeThrowingHandler(new HttpRequestException("timeout")))
            .SmokeTestAsync(FlatOAuthCred("agy-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Transient, result.Category);
    }

    [Fact]
    public async Task OAuthCancellation_ReturnsFail_Timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await BuildProbe(new SmokeHangingHandler())
            .SmokeTestAsync(FlatOAuthCred("agy-oauth-token"), cts.Token);
        Assert.False(result.Ok);
        Assert.Equal("timeout", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Transient, result.Category);
    }

    [Fact]
    public async Task NoToken_ReturnsFail_WithoutHttpCall()
    {
        int calls = 0;
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => calls++))
            .SmokeTestAsync(EmptyCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("no token", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OAuthCredsJson_MissingAccessTokenWithRefreshToken_ReturnsPersistentFailureWithoutHttpCall()
    {
        int calls = 0;
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => calls++))
            .SmokeTestAsync(OAuthCredMissingAccessToken(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("no token", result.FailureReason);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
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

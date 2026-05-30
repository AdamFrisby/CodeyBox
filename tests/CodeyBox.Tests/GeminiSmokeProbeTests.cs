using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GeminiSmokeProbe"/> using a fake HTTP handler.
/// </summary>
public sealed class GeminiSmokeProbeTests
{
    private static AgentCredential ValidCred(string key = "AIza-test") =>
        new(AgentKind.Gemini,
            new Dictionary<string, string> { ["GEMINI_API_KEY"] = key },
            new Dictionary<string, string>());

    private static AgentCredential OAuthCred(string accessToken, string? apiKey = null)
    {
        var env = new Dictionary<string, string>
        {
            ["CODEYBOX_GEMINI_OAUTH_CREDS_JSON"] = $$"""{"access_token":"{{accessToken}}","refresh_token":"rt","expiry_date":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}}}"""
        };
        if (!string.IsNullOrEmpty(apiKey))
            env["GEMINI_API_KEY"] = apiKey!;
        return new(AgentKind.Gemini, env, new Dictionary<string, string>());
    }

    private static AgentCredential EmptyCred() =>
        new(AgentKind.Gemini,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    private static AgentCredential OAuthCredMissingAccessToken() =>
        new(AgentKind.Gemini,
            new Dictionary<string, string>
            {
                ["CODEYBOX_GEMINI_OAUTH_CREDS_JSON"] = """{"refresh_token":"rt","expiry_date":9999999999999}"""
            },
            new Dictionary<string, string>());

    private static GeminiSmokeProbe BuildProbe(HttpMessageHandler handler) =>
        new(new SmokeFakeHttpClientFactory("agent-smoke", handler),
            NullLogger<GeminiSmokeProbe>.Instance);

    [Fact]
    public async Task Probe_PostsToGenerateContentEndpoint()
    {
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        await BuildProbe(handler).SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.Equal(new Uri(GeminiSmokeProbe.GenerateContentEndpoint), captured);
    }

    [Fact]
    public async Task ValidCred_SendsXGoogApiKeyHeader()
    {
        string? headerValue = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            headerValue = req.Headers.TryGetValues("x-goog-api-key", out var vals)
                ? string.Join("", vals) : null);
        await BuildProbe(handler).SmokeTestAsync(ValidCred("my-gemini-key"), CancellationToken.None);
        Assert.Equal("my-gemini-key", headerValue);
    }

    [Fact]
    public async Task Http200_ReturnsOk()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { }))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Http401_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { }))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Http403_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Forbidden, "", _ => { }))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Http500_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { }))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task NetworkException_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeThrowingHandler(new HttpRequestException("timeout")))
            .SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task Cancellation_ReturnsFail_Timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await BuildProbe(new SmokeHangingHandler())
            .SmokeTestAsync(ValidCred(), cts.Token);
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
    public async Task OAuthToken_SendsBearerHeader()
    {
        string? authHeader = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            authHeader = req.Headers.Authorization?.ToString());
        await BuildProbe(handler).SmokeTestAsync(OAuthCred("gemini-oauth-token"), CancellationToken.None);
        Assert.Equal("Bearer gemini-oauth-token", authHeader);
    }

    [Fact]
    public async Task OAuthToken_Http200_ReturnsOk()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { }))
            .SmokeTestAsync(OAuthCred("gemini-oauth-token"), CancellationToken.None);
        Assert.True(result.Ok);
    }

    [Fact]
    public async Task OAuthToken_Http401_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { }))
            .SmokeTestAsync(OAuthCred("gemini-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task PrefersApiKey_OverOAuthToken()
    {
        string? headerValue = null;
        // Credential has both GEMINI_API_KEY and CODEYBOX_GEMINI_OAUTH_CREDS_JSON.
        // API key must win (sent as x-goog-api-key, NOT Bearer).
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
        {
            req.Headers.TryGetValues("x-goog-api-key", out var vals);
            headerValue = vals?.FirstOrDefault();
        });
        await BuildProbe(handler).SmokeTestAsync(OAuthCred("oauth-token", apiKey: "my-api-key"), CancellationToken.None);
        Assert.Equal("my-api-key", headerValue);
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

    [Fact]
    public async Task OAuthNetworkException_ReturnsFail_Transient()
    {
        var result = await BuildProbe(new SmokeThrowingHandler(new HttpRequestException("timeout")))
            .SmokeTestAsync(OAuthCred("gemini-oauth-token"), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task OAuthCancellation_ReturnsFail_Timeout()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await BuildProbe(new SmokeHangingHandler())
            .SmokeTestAsync(OAuthCred("gemini-oauth-token"), cts.Token);
        Assert.False(result.Ok);
        Assert.Equal("timeout", result.FailureReason);
    }

    // ── ExtractAccessToken edge cases ──────────────────────────────────────

    [Fact]
    public void ExtractAccessToken_MalformedJson_ReturnsNull()
    {
        var result = GeminiSmokeProbe.ExtractAccessToken("not valid json at all");
        Assert.Null(result);
    }

    [Fact]
    public void ExtractAccessToken_AccessTokenIsNonString_ReturnsNull()
    {
        var json = """{"access_token":12345,"refresh_token":"rt"}""";
        var result = GeminiSmokeProbe.ExtractAccessToken(json);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractAccessToken_EmptyString_ReturnsNull()
    {
        var result = GeminiSmokeProbe.ExtractAccessToken("");
        Assert.Null(result);
    }
}

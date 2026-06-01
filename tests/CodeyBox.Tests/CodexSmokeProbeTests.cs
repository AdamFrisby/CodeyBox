using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Codex;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CodexSmokeProbe"/> using a fake HTTP handler.
/// </summary>
public sealed class CodexSmokeProbeTests
{
    private static AgentCredential ValidCred(string key = "sk-test") =>
        new(AgentKind.Codex,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = key },
            new Dictionary<string, string>());

    private static AgentCredential OAuthCred(string accessToken = "ChatGPT-OAuth-Test", string? accountId = "acc_test") =>
        new(AgentKind.Codex,
            new Dictionary<string, string>
            {
                ["CODEX_AUTH_JSON"] = BuildAuthJson(accessToken, accountId, apiKey: null),
            },
            new Dictionary<string, string>());

    private static AgentCredential EmptyCred() =>
        new(AgentKind.Codex,
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

    private static string BuildAuthJson(string? accessToken, string? accountId, string? apiKey)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(apiKey))
                writer.WriteString("OPENAI_API_KEY", apiKey);
            writer.WritePropertyName("tokens");
            writer.WriteStartObject();
            if (!string.IsNullOrEmpty(accessToken))
                writer.WriteString("access_token", accessToken);
            if (!string.IsNullOrEmpty(accountId))
                writer.WriteString("account_id", accountId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static CodexSmokeProbe BuildProbe(HttpMessageHandler handler) =>
        new(new SmokeFakeHttpClientFactory("agent-smoke", handler),
            NullLogger<CodexSmokeProbe>.Instance);

    [Fact]
    public async Task Probe_PostsToCompletionsEndpoint()
    {
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        await BuildProbe(handler).SmokeTestAsync(ValidCred(), CancellationToken.None);
        Assert.Equal(new Uri(CodexSmokeProbe.CompletionsEndpoint), captured);
    }

    [Fact]
    public async Task ValidCred_SendsBearerToken()
    {
        string? auth = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            auth = req.Headers.Authorization?.ToString());
        await BuildProbe(handler).SmokeTestAsync(ValidCred("my-key"), CancellationToken.None);
        Assert.Equal("Bearer my-key", auth);
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
        var result = await BuildProbe(new SmokeThrowingHandler(new HttpRequestException("net error")))
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

    // ── OAuth subscription path ───────────────────────────────────────────────

    [Fact]
    public async Task OAuthOnlyCred_HitsOAuthUsageEndpoint_NotApiOpenAi()
    {
        // Regression: with OAuth-only creds (CODEX_AUTH_JSON / access_token,
        // no OPENAI_API_KEY) the probe used to fail "no token", forcing the
        // router to reject codex on every routing decision even though the
        // OAuth quota probe sees 100% via the ChatGPT backend. The OAuth smoke
        // path must validate against the same usage endpoint the quota probe
        // already uses successfully.
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        var result = await BuildProbe(handler).SmokeTestAsync(OAuthCred(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(captured);
        Assert.Equal(new Uri(CodexSmokeProbe.OAuthUsageEndpoint), captured);
        // Explicitly assert we did NOT route the OAuth bearer at the
        // API-key inference endpoint (which would 401 and is unsafe).
        Assert.NotEqual("api.openai.com", captured!.Host);
        Assert.Equal("chatgpt.com", captured.Host);
    }

    [Fact]
    public async Task OAuthOnlyCred_SendsBearerTokenAndAccountIdHeader()
    {
        string? auth = null;
        string? accountId = null;
        HttpMethod? method = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
        {
            auth = req.Headers.Authorization?.ToString();
            method = req.Method;
            if (req.Headers.TryGetValues("ChatGPT-Account-Id", out var values))
                accountId = string.Join(",", values);
        });

        await BuildProbe(handler).SmokeTestAsync(OAuthCred("the-oauth-token", "acc_123"), CancellationToken.None);

        Assert.Equal("Bearer the-oauth-token", auth);
        Assert.Equal("acc_123", accountId);
        // The WHAM usage endpoint is GET — never POST inference shapes when
        // we only have a subscription OAuth token (account-safety).
        Assert.Equal(HttpMethod.Get, method);
    }

    [Fact]
    public async Task OAuthCred_WithoutAccountId_StillSendsBearer()
    {
        string? auth = null;
        bool hasAccount = false;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
        {
            auth = req.Headers.Authorization?.ToString();
            hasAccount = req.Headers.Contains("ChatGPT-Account-Id");
        });

        await BuildProbe(handler).SmokeTestAsync(OAuthCred("tok", accountId: null), CancellationToken.None);

        Assert.Equal("Bearer tok", auth);
        Assert.False(hasAccount);
    }

    [Fact]
    public async Task OAuthPath_Http401_ReturnsFail_Auth()
    {
        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { }))
            .SmokeTestAsync(OAuthCred(), CancellationToken.None);
        Assert.False(result.Ok);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task ApiKeyTakesPrecedenceOverOAuth_WhenBothPresent()
    {
        // When OPENAI_API_KEY env var is set the probe must prefer it
        // (cheaper / faster than hitting the WHAM usage endpoint).
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req => captured = req.RequestUri);
        var cred = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string>
            {
                ["OPENAI_API_KEY"] = "sk-from-env",
                ["CODEX_AUTH_JSON"] = BuildAuthJson("the-oauth-token", "acc_123", apiKey: null),
            },
            new Dictionary<string, string>());

        await BuildProbe(handler).SmokeTestAsync(cred, CancellationToken.None);

        Assert.Equal(new Uri(CodexSmokeProbe.CompletionsEndpoint), captured);
        Assert.Equal("api.openai.com", captured!.Host);
    }

    [Fact]
    public async Task ApiKeyEmbeddedInAuthJson_UsesApiKeyPath()
    {
        // CodexAgentRunner.ResolveOpenAiApiKey also reads OPENAI_API_KEY from
        // inside CODEX_AUTH_JSON; mirror that here so the smoke gate accepts
        // the same shape the runner would happily run with.
        string? auth = null;
        Uri? captured = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
        {
            auth = req.Headers.Authorization?.ToString();
            captured = req.RequestUri;
        });
        var cred = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string>
            {
                ["CODEX_AUTH_JSON"] = BuildAuthJson(accessToken: null, accountId: null, apiKey: "sk-embedded"),
            },
            new Dictionary<string, string>());

        await BuildProbe(handler).SmokeTestAsync(cred, CancellationToken.None);

        Assert.Equal("Bearer sk-embedded", auth);
        Assert.Equal(new Uri(CodexSmokeProbe.CompletionsEndpoint), captured);
    }

    [Fact]
    public async Task MalformedAuthJson_ReturnsFail_NoToken_WithoutHttpCall()
    {
        int calls = 0;
        var cred = new AgentCredential(
            AgentKind.Codex,
            new Dictionary<string, string> { ["CODEX_AUTH_JSON"] = "{not-json" },
            new Dictionary<string, string>());

        var result = await BuildProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => calls++))
            .SmokeTestAsync(cred, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("no token", result.FailureReason);
        Assert.Equal(0, calls);
    }
}

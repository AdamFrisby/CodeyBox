using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the per-provider <see cref="IAgentModelListProbe"/> implementations.
/// Verifies endpoint URL, auth header selection, response parsing, and that
/// network errors return a failure result rather than throwing.
/// </summary>
public sealed class AgentModelListProbeTests
{
    // ── Claude ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Claude_OAuthOnly_DeclinesWithoutHttpCall()
    {
        // A subscription OAuth token must NOT be used for a raw /v1/models call:
        // the official Claude Code client does not hit that endpoint, so an
        // OAuth Bearer there is raw-API access outside the legitimate client
        // shape (same account-termination risk as /v1/messages). The probe
        // returns a failure result and makes no HTTP call.
        int calls = 0;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK,
            """{"data":[{"id":"claude-opus-4-7"}]}""",
            _ => calls++);

        var probe = new ClaudeModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => ("oauth-tok", null),
            NullLogger<ClaudeModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal(ClaudeModelListProbe.OAuthDeclinedReason, result.FailureReason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Claude_OAuthAndApiKey_UsesApiKeyPath_NotOAuth()
    {
        // When both are present the API-key path wins — never an OAuth Bearer
        // against /v1/models.
        Uri? capturedUri = null;
        string? capturedAuth = null;
        string? xApiKey = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK,
            """{"data":[{"id":"claude-opus-4-7"}]}""",
            req =>
            {
                capturedUri = req.RequestUri;
                capturedAuth = req.Headers.Authorization?.ToString();
                xApiKey = req.Headers.TryGetValues("x-api-key", out var v) ? string.Join("", v) : null;
            });

        var probe = new ClaudeModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => ("oauth-tok", "ak-456"),
            NullLogger<ClaudeModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal(new Uri(ClaudeModelListProbe.ModelsEndpoint), capturedUri);
        Assert.Null(capturedAuth);
        Assert.Equal("ak-456", xApiKey);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task Claude_UsesXApiKey_WhenApiKeyOnly()
    {
        string? xApiKey = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, """{"data":[]}""",
            req => xApiKey = req.Headers.TryGetValues("x-api-key", out var v) ? string.Join("", v) : null);

        var probe = new ClaudeModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => (null, "ak-123"),
            NullLogger<ClaudeModelListProbe>.Instance);

        await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal("ak-123", xApiKey);
    }

    [Fact]
    public async Task Claude_HttpError_ReturnsFailureResult()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { });
        var probe = new ClaudeModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => (null, "ak-401"),
            NullLogger<ClaudeModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("401", result.FailureReason);
    }

    [Fact]
    public async Task Claude_NetworkException_DoesNotThrow()
    {
        var handler = new SmokeThrowingHandler(new HttpRequestException("connection refused"));
        var probe = new ClaudeModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => (null, "ak-net"),
            NullLogger<ClaudeModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task Claude_GuardAgainstRawV1ModelsWithOAuthBearer_OverAllOAuthShapes()
    {
        // Account-safety regression guard: a subscription OAuth token must never
        // be sent as an Authorization: Bearer to https://api.anthropic.com/v1/models
        // either — the official Claude Code client doesn't call that endpoint, so
        // the misuse pattern is identical to /v1/messages and carries the same
        // account-termination risk. Cover every OAuth-bearing credential shape
        // so a future refactor re-enabling the OAuth path fails here.
        foreach (var creds in new (string? OAuth, string? ApiKey)[]
        {
            ("oauth-only", null),
            ("oauth-pref", "ak-fallback"),
        })
        {
            HttpRequestMessage? captured = null;
            var handler = new SmokeCapturingHandler(HttpStatusCode.OK,
                """{"data":[]}""",
                req => captured = req);

            var probe = new ClaudeModelListProbe(
                new SmokeFakeHttpClientFactory("agent-modellist", handler),
                () => creds,
                NullLogger<ClaudeModelListProbe>.Instance);

            await probe.GetModelListAsync(CancellationToken.None);

            // The forbidden combination is /v1/models AND a Bearer Authorization.
            // Either: no HTTP call at all (preferred — OAuth-only is declined),
            // or, when an API key is also present, the request uses x-api-key.
            if (captured is null) continue;
            var isModelsEndpoint = string.Equals(
                captured.RequestUri?.AbsoluteUri,
                ClaudeModelListProbe.ModelsEndpoint,
                StringComparison.Ordinal);
            var isBearer = string.Equals(
                captured.Headers.Authorization?.Scheme,
                "Bearer",
                StringComparison.OrdinalIgnoreCase);
            Assert.False(isModelsEndpoint && isBearer,
                $"ClaudeModelListProbe constructed GET {ClaudeModelListProbe.ModelsEndpoint} with a Bearer Authorization header — this is the same subscription-OAuth raw-API misuse pattern as /v1/messages. See ClaudeModelListProbe.cs.");
        }
    }

    [Fact]
    public async Task Claude_NoCredential_ReturnsFailure()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { });
        var probe = new ClaudeModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => (null, null),
            NullLogger<ClaudeModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal("no credential configured", result.FailureReason);
    }

    [Fact]
    public void Claude_ParseResponse_HandlesUnknownShape()
    {
        var result = ClaudeModelListProbe.ParseResponse("""{"unexpected":true}""");
        Assert.NotNull(result.FailureReason);
    }

    // ── Codex ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Codex_OAuth_HitsWhamEndpoint()
    {
        Uri? capturedUri = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK,
            """{"data":[{"id":"gpt-5.5"}]}""",
            req => capturedUri = req.RequestUri);

        var probe = new CodexModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => ("oauth-tok", "acct-1", null),
            NullLogger<CodexModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal(new Uri(CodexModelListProbe.OAuthModelsEndpoint), capturedUri);
        Assert.Null(result.FailureReason);
        Assert.Contains("gpt-5.5", result.ModelIds);
    }

    [Fact]
    public async Task Codex_ApiKey_HitsOpenAiEndpoint_WithBearer()
    {
        Uri? capturedUri = null;
        string? capturedAuth = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK,
            """{"data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"}]}""",
            req => { capturedUri = req.RequestUri; capturedAuth = req.Headers.Authorization?.ToString(); });

        var probe = new CodexModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => (null, null, "ak-xyz"),
            NullLogger<CodexModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal(new Uri(CodexModelListProbe.ApiModelsEndpoint), capturedUri);
        Assert.Equal("Bearer ak-xyz", capturedAuth);
        Assert.Equal(new[] { "gpt-4o", "gpt-4o-mini" }, result.ModelIds);
    }

    [Fact]
    public async Task Codex_OAuth_AddsAccountIdHeader()
    {
        string? acct = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, """{"data":[]}""",
            req => acct = req.Headers.TryGetValues("ChatGPT-Account-Id", out var v) ? string.Join("", v) : null);

        var probe = new CodexModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => ("oauth-tok", "acct-42", null),
            NullLogger<CodexModelListProbe>.Instance);

        await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal("acct-42", acct);
    }

    [Fact]
    public void Codex_ParseResponse_HandlesWhamModelsShape()
    {
        var result = CodexModelListProbe.ParseResponse("""{"models":[{"slug":"gpt-5"},{"slug":"o1-preview"}]}""");
        Assert.Null(result.FailureReason);
        Assert.Equal(new[] { "gpt-5", "o1-preview" }, result.ModelIds);
    }

    // ── Gemini ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gemini_OAuth_HitsQuotaEndpoint_AndReadsBucketModelIds()
    {
        Uri? capturedUri = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK,
            """{"buckets":[{"modelId":"gemini-2.5-flash"},{"modelId":"gemini-3.1-flash-lite"}]}""",
            req => capturedUri = req.RequestUri);

        var probe = new GeminiModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => ("oauth-tok", null),
            NullLogger<GeminiModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal(new Uri(GeminiModelListProbe.OAuthQuotaEndpoint), capturedUri);
        Assert.Null(result.FailureReason);
        Assert.Equal(new[] { "gemini-2.5-flash", "gemini-3.1-flash-lite" }, result.ModelIds);
    }

    [Fact]
    public async Task Gemini_ApiKey_HitsModelsEndpoint_AndStripsPrefix()
    {
        Uri? capturedUri = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK,
            """{"models":[{"name":"models/gemini-2.0-flash"},{"name":"models/gemini-1.5-pro"}]}""",
            req => capturedUri = req.RequestUri);

        var probe = new GeminiModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => (null, "ak-xyz"),
            NullLogger<GeminiModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.NotNull(capturedUri);
        Assert.Equal("/v1beta/models", capturedUri!.AbsolutePath);
        Assert.Contains("key=", capturedUri.Query);
        Assert.Null(result.FailureReason);
        Assert.Equal(new[] { "gemini-2.0-flash", "gemini-1.5-pro" }, result.ModelIds);
    }

    [Fact]
    public async Task Gemini_NoCredential_ReturnsFailure()
    {
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { });
        var probe = new GeminiModelListProbe(
            new SmokeFakeHttpClientFactory("agent-modellist", handler),
            () => (null, null),
            NullLogger<GeminiModelListProbe>.Instance);

        var result = await probe.GetModelListAsync(CancellationToken.None);
        Assert.Equal("no credential configured", result.FailureReason);
    }

}

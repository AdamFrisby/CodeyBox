using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Agents.Opencode;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Validates that every smoke probe and the orchestrator-side fallbacks
/// classify failures as transient vs persistent — the classification benched
/// gemini for hours despite 100% quota because every auth/credential failure
/// was treated as "transient: try later" and silently retried forever.
/// </summary>
public sealed class SmokeFailureCategoryTests
{
    // ── Gemini ────────────────────────────────────────────────────────────────

    private static AgentCredential GeminiApiKey(string key = "AIza-test") =>
        new(AgentKind.Gemini,
            new Dictionary<string, string> { ["GEMINI_API_KEY"] = key },
            new Dictionary<string, string>());

    private static AgentCredential GeminiOAuth(string accessToken) =>
        new(AgentKind.Gemini,
            new Dictionary<string, string>
            {
                ["CODEYBOX_GEMINI_OAUTH_CREDS_JSON"] =
                    $$"""{"access_token":"{{accessToken}}","refresh_token":"rt","expiry_date":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()}}}""",
            },
            new Dictionary<string, string>());

    private static AgentCredential GeminiEmpty() =>
        new(AgentKind.Gemini, new Dictionary<string, string>(), new Dictionary<string, string>());

    private static GeminiSmokeProbe BuildGeminiProbe(
        HttpMessageHandler handler,
        IGeminiOAuthTokenSource? oauthTokenSource = null) =>
        new(new SmokeFakeHttpClientFactory("agent-smoke", handler),
            NullLogger<GeminiSmokeProbe>.Instance,
            oauthTokenSource);

    [Fact]
    public async Task Gemini_Http401_IsPersistent()
    {
        var probe = BuildGeminiProbe(new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { }));
        var result = await probe.SmokeTestAsync(GeminiApiKey(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Equal("auth", result.FailureReason);
    }

    [Fact]
    public async Task Gemini_Http403_IsPersistent()
    {
        var probe = BuildGeminiProbe(new SmokeCapturingHandler(HttpStatusCode.Forbidden, "", _ => { }));
        var result = await probe.SmokeTestAsync(GeminiApiKey(), CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task Gemini_Http500_IsTransient()
    {
        var probe = BuildGeminiProbe(new SmokeCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { }));
        var result = await probe.SmokeTestAsync(GeminiApiKey(), CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Transient, result.Category);
    }

    [Fact]
    public async Task Gemini_NetworkException_IsTransient()
    {
        var probe = BuildGeminiProbe(new SmokeThrowingHandler(new HttpRequestException("connect refused")));
        var result = await probe.SmokeTestAsync(GeminiApiKey(), CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Transient, result.Category);
        Assert.Contains("transient", result.FailureReason);
    }

    [Fact]
    public async Task Gemini_NoToken_IsPersistent()
    {
        // No GEMINI_API_KEY and no OAuth bundle — the operator must authorize
        // gemini (or set the env var) before the smoke can pass. The previous
        // default of an ambiguous-looking transient-style reason masked the fact
        // that retrying will never succeed.
        var probe = BuildGeminiProbe(new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { }));
        var result = await probe.SmokeTestAsync(GeminiEmpty(), CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
        Assert.Contains("no token", result.FailureReason);
    }

    [Fact]
    public async Task Gemini_OtherHttpStatus_IsUnknown()
    {
        // A 418 or 451 or other oddball status should not get tagged as
        // persistent (we don't actually know) and should not get tagged as
        // transient (it isn't a 5xx); the retry loop continues but the alert
        // does not fire.
        var probe = BuildGeminiProbe(new SmokeCapturingHandler((HttpStatusCode)418, "", _ => { }));
        var result = await probe.SmokeTestAsync(GeminiApiKey(), CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Unknown, result.Category);
    }

    [Fact]
    public async Task Gemini_Cancellation_IsTransient()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var probe = BuildGeminiProbe(new SmokeHangingHandler());
        var result = await probe.SmokeTestAsync(GeminiApiKey(), cts.Token);

        Assert.Equal(SmokeFailureCategory.Transient, result.Category);
    }

    // ── Gemini OAuth refresh hook ─────────────────────────────────────────────

    private sealed class FakeOAuthTokenSource : IGeminiOAuthTokenSource
    {
        public string? Token { get; init; }
        public Exception? Throw { get; init; }
        public int Calls { get; private set; }

        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Token);
        }
    }

    [Fact]
    public async Task Gemini_OAuthRefresher_FreshToken_IsUsedOverCredentialBundle()
    {
        // The credential bundle's access_token is what gemini-cli last wrote;
        // it rotates ~hourly and is typically stale. The injected refresher
        // returns the just-minted token — that's what the smoke must use, or
        // the bench loops forever even though the agent is usable.
        string? sentBearer = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            sentBearer = req.Headers.Authorization?.Parameter);
        var source = new FakeOAuthTokenSource { Token = "fresh-refreshed-token" };
        var probe = BuildGeminiProbe(handler, source);

        var result = await probe.SmokeTestAsync(GeminiOAuth("stale-on-disk-token"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("fresh-refreshed-token", sentBearer);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task Gemini_OAuthRefresher_ReturnsNull_FallsBackToCredentialBundle()
    {
        // Refresher null = the operator hasn't configured client_id/secret AND
        // no host gemini CLI for fallback — but we still have the on-disk
        // bundle. Try it: it may still be in-window. (If it's also expired,
        // we'll surface a persistent auth failure further along — also fine.)
        string? sentBearer = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            sentBearer = req.Headers.Authorization?.Parameter);
        var source = new FakeOAuthTokenSource { Token = null };
        var probe = BuildGeminiProbe(handler, source);

        var result = await probe.SmokeTestAsync(GeminiOAuth("on-disk-token"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("on-disk-token", sentBearer);
    }

    [Fact]
    public async Task Gemini_OAuthRefresher_Throws_DoesNotPropagate_FallsBackToCredentialBundle()
    {
        string? sentBearer = null;
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", req =>
            sentBearer = req.Headers.Authorization?.Parameter);
        var source = new FakeOAuthTokenSource { Throw = new InvalidOperationException("refresh boom") };
        var probe = BuildGeminiProbe(handler, source);

        var result = await probe.SmokeTestAsync(GeminiOAuth("on-disk-token"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("on-disk-token", sentBearer);
    }

    [Fact]
    public async Task Gemini_ApiKey_PrefersApiKey_OverRefresher()
    {
        // GEMINI_API_KEY beats OAuth — the refresher should not be invoked at
        // all in that case (it's an OAuth-only path). Belt-and-braces: a stray
        // refresh call on the API-key path would burn quota / cache an extra
        // round-trip per pickup.
        var handler = new SmokeCapturingHandler(HttpStatusCode.OK, "{}", _ => { });
        var source = new FakeOAuthTokenSource { Token = "should-not-be-used" };
        var probe = BuildGeminiProbe(handler, source);

        var result = await probe.SmokeTestAsync(GeminiApiKey("my-key"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(0, source.Calls);
    }

    // ── Other probes ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Claude_Http401_IsPersistent()
    {
        var probe = new ClaudeSmokeProbe(
            new SmokeFakeHttpClientFactory("agent-smoke",
                new SmokeCapturingHandler(HttpStatusCode.Unauthorized, "", _ => { })),
            NullLogger<ClaudeSmokeProbe>.Instance);
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task Claude_NoToken_IsPersistent()
    {
        var probe = new ClaudeSmokeProbe(
            new SmokeFakeHttpClientFactory("agent-smoke",
                new SmokeCapturingHandler(HttpStatusCode.OK, "", _ => { })),
            NullLogger<ClaudeSmokeProbe>.Instance);
        var cred = new AgentCredential(AgentKind.Claude,
            new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task Codex_Http500_IsTransient()
    {
        var probe = new CodexSmokeProbe(
            new SmokeFakeHttpClientFactory("agent-smoke",
                new SmokeCapturingHandler(HttpStatusCode.InternalServerError, "", _ => { })),
            NullLogger<CodexSmokeProbe>.Instance);
        var cred = new AgentCredential(AgentKind.Codex,
            new Dictionary<string, string> { ["OPENAI_API_KEY"] = "sk-test" },
            new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Transient, result.Category);
    }

    [Fact]
    public async Task Cursor_NoToken_IsPersistent()
    {
        var probe = new CursorSmokeProbe(NullLogger<CursorSmokeProbe>.Instance);
        var cred = new AgentCredential(AgentKind.Cursor,
            new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    [Fact]
    public async Task Opencode_NoToken_IsPersistent()
    {
        var probe = new OpencodeSmokeProbe(NullLogger<OpencodeSmokeProbe>.Instance);
        var cred = new AgentCredential(AgentKind.Opencode,
            new Dictionary<string, string>(), new Dictionary<string, string>());

        var result = await probe.SmokeTestAsync(cred, CancellationToken.None);

        Assert.Equal(SmokeFailureCategory.Persistent, result.Category);
    }

    // ── Registry surfaces classification in the exclusion reason ──────────────

    [Fact]
    public void Registry_PersistentSmokeFailure_TagsReason()
    {
        var reg = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        reg.MarkSmokeResult(
            AgentKind.Gemini,
            new AgentSmokeResult(false, "auth", TimeSpan.Zero, SmokeFailureCategory.Persistent));

        var av = reg.GetAvailability(AgentKind.Gemini);

        Assert.False(av.Available);
        Assert.NotNull(av.Reason);
        Assert.Contains("persistent", av.Reason);
        Assert.Contains("auth", av.Reason);
    }

    [Fact]
    public void Registry_TransientSmokeFailure_TagsReason()
    {
        var reg = new AgentAvailabilityRegistry(
            new AvailabilityOptions(),
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);
        reg.MarkSmokeResult(
            AgentKind.Gemini,
            new AgentSmokeResult(false, "timeout", TimeSpan.Zero, SmokeFailureCategory.Transient));

        var av = reg.GetAvailability(AgentKind.Gemini);

        Assert.Contains("transient", av.Reason);
    }

    // ── Webhook payload carries category ──────────────────────────────────────

    [Fact]
    public void WebhookDetails_DefaultsToUnknown_WhenCategoryNotSet()
    {
        // Legacy producers that don't set Category get a non-null,
        // forward-compatible default so consumers don't need a null check.
        var d = new AgentSmokeFailedDetails { AgentKind = "gemini", Reason = "auth" };
        Assert.Equal(SmokeFailureCategory.Unknown, d.Category);
    }

    [Fact]
    public void WebhookDetails_CanCarryPersistentCategory()
    {
        var d = new AgentSmokeFailedDetails
        {
            AgentKind = "gemini",
            Reason = "auth",
            Category = SmokeFailureCategory.Persistent,
        };
        Assert.Equal(SmokeFailureCategory.Persistent, d.Category);
    }
}

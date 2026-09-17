using CodeyBox.Agents.Continue;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ContinueQuotaFailureDetector"/>: the recorded real
/// @continuedev/cli 1.5.47 failure shape (the $0-spend-limit paid-model
/// refusal → LimitReached), the deliberate non-matches (reviewed-code prose
/// that must not gate dispatch), and the operator-extensible pattern hook
/// (<c>CodeyBox:QuotaFailurePatterns:continue</c>).
/// </summary>
public sealed class ContinueQuotaFailureDetectorTests
{
    // Recorded real shape: $0-spend-limit key against a paid model (exit 0,
    // envelope on stdout).
    private const string SpendLimitStdout =
        "{\"status\":\"error\",\"message\":\"403 Key limit exceeded (total limit). " +
        "Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058\"}";

    [Fact]
    public void Kind_IsContinue()
    {
        Assert.Equal(AgentKind.Continue, new ContinueQuotaFailureDetector().Kind);
    }

    [Fact]
    public void Detect_SpendLimitRefusal_ReturnsLimitReached()
    {
        var detection = new ContinueQuotaFailureDetector().Detect(null, SpendLimitStdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_OnboardingGateEnvelope_ReturnsNull()
    {
        // The first-run onboarding-gate interceptor failure is an
        // environment/provisioning signal, not quota/auth state — it must
        // not park the member on a quota backoff.
        const string stdout =
            "{\"status\":\"error\",\"message\":\"The request failed and the interceptors did not return an alternative response\"}";

        Assert.Null(new ContinueQuotaFailureDetector().Detect(null, stdout));
    }

    [Fact]
    public void Detect_RateLimitShape_ReturnsRateLimitExceeded()
    {
        var detection = new ContinueQuotaFailureDetector().Detect(
            "Error: API Error: 429 Rate limit exceeded, retry after 12s",
            null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection.Kind);
    }

    [Fact]
    public void Detect_AuthShape_ReturnsUnauthorized()
    {
        var detection = new ContinueQuotaFailureDetector().Detect(
            null,
            "{\"status\":\"error\",\"message\":\"Authentication failed: invalid_api_key\"}");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_ReviewedQuotaProse_ReturnsNull()
    {
        // Model output about quota code under review must not gate dispatch:
        // bare "quota"/"401" mentions without an exhaustion verb stay
        // unmatched.
        Assert.Null(new ContinueQuotaFailureDetector().Detect(
            null,
            "Review the user quota handler for 401 responses in quota.ts"));
    }

    [Fact]
    public void Detect_HealthyReply_ReturnsNull()
    {
        Assert.Null(new ContinueQuotaFailureDetector().Detect(null, "HELLO-CN-OK"));
        Assert.Null(new ContinueQuotaFailureDetector().Detect(null, null));
    }

    [Fact]
    public void Detect_OperatorPatterns_AppendedAfterDefaults()
    {
        // Operator rows extend the list without shadowing the built-ins.
        var detector = new ContinueQuotaFailureDetector(
            [new QuotaFailurePattern("Monthly spend cap hit", QuotaFailureKind.LimitReached)]);

        var custom = detector.Detect(null, "Monthly spend cap hit for this key");
        Assert.NotNull(custom);
        Assert.Equal(QuotaFailureKind.LimitReached, custom.Kind);

        var builtin = detector.Detect(null, SpendLimitStdout);
        Assert.NotNull(builtin);
        Assert.Equal(QuotaFailureKind.LimitReached, builtin.Kind);
    }

    [Fact]
    public void Detect_BlankOperatorPatterns_BehavesLikeDefaults()
    {
        var detector = new ContinueQuotaFailureDetector(
            [new QuotaFailurePattern("", QuotaFailureKind.LimitReached)]);

        Assert.NotNull(detector.Detect(null, SpendLimitStdout));
    }
}

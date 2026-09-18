using CodeyBox.Agents.Crush;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CrushQuotaFailureDetector"/> over recorded real
/// @charmland/crush 0.95.0 output: the paid-model spend-limit refusal
/// (exit 1, styled <c>ERROR</c> block on stderr), the bare-machine
/// provider-missing shape, and the unknown-model config error that must
/// NOT classify as quota. Both streams are scanned because the markers are
/// human rendering, not a stream-guaranteed envelope.
/// </summary>
public sealed class CrushQuotaFailureDetectorTests
{
    [Fact]
    public void Kind_IsCrush()
    {
        Assert.Equal(AgentKind.Crush, new CrushQuotaFailureDetector().Kind);
    }

    [Fact]
    public void Detect_SpendLimitRefusal_ReturnsLimitReached()
    {
        // Recorded real shape ($0-spend-limit key against a paid model):
        // exit 1 with the provider body relayed verbatim in the ERROR block.
        const string stderr =
            "Agent processing failed: failed to start agent processing stream: forbidden: Key limit exceeded (total limit).\n" +
            "Manage it using https://openrouter.ai/workspaces/default/keys/64835ad0564749843e34c8e2e6d1482456dad7a14fbbd107a5a174ea87fc6058.";

        var detection = new CrushQuotaFailureDetector().Detect(stderr, string.Empty);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_NoProvidersConfigured_ReturnsUnauthorized()
    {
        // Recorded real shape (bare machine, no key): the CLI hard-fails
        // before any provider call, so this is the credential recovery path.
        const string stderr =
            "No providers configured - please run 'crush' to set up a provider interactively.";

        var detection = new CrushQuotaFailureDetector().Detect(stderr, string.Empty);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_UnknownModelId_ReturnsNull()
    {
        // Recorded real shape (unknown -m id): a configuration error, not
        // quota — parking it as quota would burn backoff on a typo.
        const string stderr =
            "Failed to override models: large model \"openrouter/no-such-model-xyz\" not found.";

        Assert.Null(new CrushQuotaFailureDetector().Detect(stderr, string.Empty));
    }

    [Fact]
    public void Detect_RateLimitShape_ReturnsRateLimitExceeded()
    {
        var detection = new CrushQuotaFailureDetector().Detect("error 429: rate limit exceeded for model", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection.Kind);
    }

    [Fact]
    public void Detect_AuthShape_ReturnsUnauthorized()
    {
        var detection = new CrushQuotaFailureDetector().Detect("Authentication failed: invalid_api_key", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_ReviewedQuotaProse_ReturnsNull()
    {
        // Bare numbers and quota discussion from reviewed repository
        // content must not gate dispatch.
        Assert.Null(new CrushQuotaFailureDetector().Detect("the 401 page discusses the quota dashboard", "retry after 402 seconds"));
        Assert.Null(new CrushQuotaFailureDetector().Detect("hello-crush-ok", string.Empty));
    }

    [Fact]
    public void Detect_HealthyReply_ReturnsNull()
    {
        Assert.Null(new CrushQuotaFailureDetector().Detect(string.Empty, "hello-crush-ok\n"));
        Assert.Null(new CrushQuotaFailureDetector().Detect(null, null));
        Assert.Null(new CrushQuotaFailureDetector().Detect(string.Empty, string.Empty));
    }

    [Fact]
    public void Detect_OperatorPatterns_AppendedAfterDefaults()
    {
        var detector = new CrushQuotaFailureDetector(
            [new QuotaFailurePattern("custom-spend-cap", QuotaFailureKind.LimitReached)]);

        var detection = detector.Detect("provider hit custom-spend-cap today", null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
        // Defaults still apply ahead of the appended rows.
        Assert.NotNull(detector.Detect("Key limit exceeded (total limit)", null));
    }

    [Fact]
    public void Detect_BlankOperatorPatterns_BehavesLikeDefaults()
    {
        var detector = new CrushQuotaFailureDetector(
            [new QuotaFailurePattern(string.Empty, QuotaFailureKind.LimitReached)]);

        Assert.NotNull(detector.Detect("Key limit exceeded (total limit)", null));
        Assert.Null(detector.Detect("nothing matching here", null));
    }
}

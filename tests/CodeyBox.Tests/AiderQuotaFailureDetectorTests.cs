using CodeyBox.Agents.Aider;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AiderQuotaFailureDetector"/> over the litellm relay
/// shapes aider 0.86.2 prints verbatim on stdout. Both streams are scanned
/// (aider's one-shot output is stdout-first), anchored patterns keep reviewed
/// repository content from gating dispatch, and operator extras append after
/// the built-ins.
/// </summary>
public sealed class AiderQuotaFailureDetectorTests
{
    [Fact]
    public void Kind_IsAider()
    {
        Assert.Equal(AgentKind.Aider, new AiderQuotaFailureDetector().Kind);
    }

    [Fact]
    public void RecordedAuthRelay_DetectsUnauthorized()
    {
        // Live stdout from the aider 0.86.2 auth probe (bogus OpenRouter key).
        const string stdout =
            "litellm.AuthenticationError: AuthenticationError: OpenrouterException - \n" +
            "{\"error\":{\"message\":\"Missing Authentication header\",\"code\":401}}\n" +
            "The API provider is not able to authenticate you. Check your API key.\n";

        var detection = new AiderQuotaFailureDetector().Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void RateLimitRelay_DetectsRateLimitExceeded()
    {
        const string stdout =
            "litellm.RateLimitError: RateLimitError: OpenrouterException - \n" +
            "{\"error\":{\"message\":\"Rate limit exceeded\",\"code\":429}}\n";

        var detection = new AiderQuotaFailureDetector().Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void BillingExhaustion_DetectsLimitReached()
    {
        const string stdout =
            "litellm.AuthenticationError: OpenrouterException - " +
            "{\"error\":{\"message\":\"insufficient credits\",\"code\":402}}\n";

        var detection = new AiderQuotaFailureDetector().Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void HealthyRunWithAppliedEdit_DetectsNothing()
    {
        // A clean run must not fabricate a quota signal: no meter exists, so
        // the detector only fires on provider refusal shapes.
        const string stdout =
            "Aider v0.86.2\n" +
            "Model: openrouter/nvidia/nemotron-3.5-lightning:free with whole edit format\n" +
            "Tokens: 766 sent, 905 received.\n" +
            "Applied edit to pong2.txt\n";

        Assert.Null(new AiderQuotaFailureDetector().Detect(null, stdout));
    }

    [Fact]
    public void NullAndEmpty_DetectsNothing()
    {
        var detector = new AiderQuotaFailureDetector();
        Assert.Null(detector.Detect(null, null));
        Assert.Null(detector.Detect("", ""));
    }

    [Fact]
    public void ReviewedCodeMentioning429_DetectsNothing()
    {
        // Anchored patterns (never a bare number): model output discussing
        // retry counts or reviewing quota code must not park dispatch.
        const string stdout =
            "Added retry logic: up to 429 attempts with backoff in client.py\n" +
            "Tokens: 100 sent, 50 received.\n";

        Assert.Null(new AiderQuotaFailureDetector().Detect(null, stdout));
    }

    [Fact]
    public void OperatorExtraPattern_AppendedAfterDefaults()
    {
        var detector = new AiderQuotaFailureDetector(
            [new QuotaFailurePattern("custom-provider-cap", QuotaFailureKind.LimitReached)]);

        var detection = detector.Detect(null, "provider says custom-provider-cap hit");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void EmptyExtras_BehavesLikeDefaults()
    {
        var detector = new AiderQuotaFailureDetector([]);

        var detection = detector.Detect(
            null,
            "litellm.AuthenticationError: AuthenticationError: OpenrouterException");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }
}

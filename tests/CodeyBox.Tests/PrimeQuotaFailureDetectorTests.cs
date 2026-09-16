using CodeyBox.Agents.Prime;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PrimeQuotaFailureDetector"/> over the failure shapes
/// recorded against prime-agent 0.9.5 with real OpenRouter runs. Both
/// streams are scanned (terminal JSON frames live on stdout, the pre-session
/// failure on stderr), anchored patterns keep reviewed repository content
/// from gating dispatch, and operator extras append after the built-ins.
/// </summary>
public sealed class PrimeQuotaFailureDetectorTests
{
    [Fact]
    public void Kind_IsPrime()
    {
        Assert.Equal(AgentKind.Prime, new PrimeQuotaFailureDetector().Kind);
    }

    [Fact]
    public void RecordedBadKey_DetectsUnauthorized()
    {
        // Live errorMessage from the prime-agent 0.9.5 bad-key probe. The
        // "Run /login" trailer rides every provider error, so auth rows sit
        // after the quota rows.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\"," +
            "\"errorMessage\":\"401 User not found.\\n\\nRun /login to update credentials.\"}}\n";

        var detection = new PrimeQuotaFailureDetector().Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void RecordedZeroLimitKey_DetectsLimitReached_NotUnauthorized()
    {
        // Live errorMessage from the $0-limit probe: a 403 quota refusal
        // that ALSO carries the "/login" trailer. The billing row must win
        // over the trailing auth vocabulary.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\"," +
            "\"errorMessage\":\"403 Key limit exceeded (total limit). Manage it using https://openrouter.ai/\\n\\nRun /login to update credentials.\"}}\n";

        var detection = new PrimeQuotaFailureDetector().Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void RecordedMissingKeyOnStderr_DetectsUnauthorized()
    {
        // Live stderr from the no-key probe (exit 0, no JSON error event).
        const string stderr =
            "No API key found for the selected model.\n" +
            "\n" +
            "Use /login to log into a provider via OAuth or API key. See:\n";

        var detection = new PrimeQuotaFailureDetector().Detect(stderr, "{\"type\":\"session\",\"version\":3}\n");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void RateLimitRelay_DetectsRateLimitExceeded()
    {
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"stopReason\":\"error\"," +
            "\"errorMessage\":\"429 Too Many Requests: rate limit exceeded\"}}\n";

        var detection = new PrimeQuotaFailureDetector().Detect(null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void HealthyRunWithUsage_DetectsNothing()
    {
        // A clean run must not fabricate a quota signal: no meter exists, so
        // the detector only fires on provider refusal shapes.
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"cwd\":\"/work\"}\n" +
            "{\"type\":\"message_end\",\"message\":{\"model\":\"m\"," +
            "\"usage\":{\"input\":1185,\"output\":104,\"cacheRead\":4352},\"stopReason\":\"stop\"}}\n" +
            "{\"type\":\"agent_end\",\"messages\":[]}\n";

        Assert.Null(new PrimeQuotaFailureDetector().Detect(null, stdout));
    }

    [Fact]
    public void NullAndEmpty_DetectsNothing()
    {
        var detector = new PrimeQuotaFailureDetector();
        Assert.Null(detector.Detect(null, null));
        Assert.Null(detector.Detect("", ""));
    }

    [Fact]
    public void ReviewedCodeMentioningQuota_DetectsNothing()
    {
        // Anchored patterns (never a bare "quota" or number): model output
        // reviewing quota code must not park dispatch.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"content\":[{\"type\":\"text\"," +
            "\"text\":\"the quota field defaults to 403 in client.py\"}],\"stopReason\":\"stop\"}}\n";

        Assert.Null(new PrimeQuotaFailureDetector().Detect(null, stdout));
    }

    [Fact]
    public void OperatorExtraPattern_AppendedAfterDefaults()
    {
        var detector = new PrimeQuotaFailureDetector(
            [new QuotaFailurePattern("custom-provider-cap", QuotaFailureKind.LimitReached)]);

        var detection = detector.Detect(null, "provider says custom-provider-cap hit");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void EmptyExtras_BehavesLikeDefaults()
    {
        var detector = new PrimeQuotaFailureDetector([]);

        var detection = detector.Detect(
            null,
            "{\"errorMessage\":\"401 User not found.\"}");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }
}

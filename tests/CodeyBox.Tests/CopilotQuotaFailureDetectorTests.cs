using CodeyBox.Agents;
using CodeyBox.Agents.Copilot;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class CopilotQuotaFailureDetectorTests
{
    private static readonly TimeSpan ResetAtAssertSkew = TimeSpan.FromSeconds(10);

    private readonly CopilotQuotaFailureDetector _detector = new();

    /// <summary>
    /// Verbatim shape of the September 2026 BYOK incident: the Console Go
    /// endpoint throughput-limited the request, the Copilot CLI relayed the
    /// provider 429 after its own short retry budget, and the item terminated
    /// as a generic failure instead of parking for quota retry. Every variant
    /// below must classify as a transient rate limit — never null (which the
    /// orchestrator records as failureKind "other" and hard-fails).
    /// </summary>
    public static IEnumerable<object[]> ProviderRateLimitSamples()
    {
        yield return new object[]
        {
            "agent exited 1",
            "Failed to get response from the AI model; retried 5 times (total retry wait time: 91.99 seconds). Last error: 429 Error from provider (Console Go): Upstream request failed: [rate_limit_exceeded] Rate limit exceeded. Please retry after a brief wait.",
        };
        yield return new object[]
        {
            "agent exited 1",
            "429 Error from provider (Console Go): Upstream request failed",
        };
        yield return new object[]
        {
            "agent exited 1",
            "API Error: 429 — rate_limit_exceeded",
        };
        yield return new object[]
        {
            "agent exited 1",
            "HTTP 429 Too Many Requests from provider endpoint",
        };
        yield return new object[]
        {
            "agent exited 1: provider refused the request",
            "request failed with status 429, please retry after a brief wait",
        };
    }

    [Theory]
    [MemberData(nameof(ProviderRateLimitSamples))]
    public void Detect_ProviderRateLimitWordings_ClassifyAsRateLimitExceeded(string summary, string stderr)
    {
        var viaStderr = _detector.Detect(stderr: stderr, stdout: null);
        Assert.NotNull(viaStderr);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, viaStderr!.Kind);

        var viaStdout = _detector.Detect(stderr: summary, stdout: stderr);
        Assert.NotNull(viaStdout);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, viaStdout!.Kind);
    }

    [Fact]
    public void Classify_ProviderRateLimitWordings_AreQuotaNotOther()
    {
        const string stderr =
            "Failed to get response from the AI model; retried 5 times (total retry wait time: 91.99 seconds). Last error: 429 Error from provider (Console Go): Upstream request failed: [rate_limit_exceeded] Rate limit exceeded. Please retry after a brief wait.";
        var classifier = new CompositeQuotaFailureClassifier([_detector]);

        var classification = classifier.Classify(AgentKind.Copilot, stderr, stdout: null);

        Assert.Equal(QuotaFailureClassificationKind.Quota, classification.Kind);
        Assert.NotNull(classification.Detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, classification.Detection!.Kind);
    }

    [Fact]
    public void Classify_ProviderRateLimitWordings_AreSoftRateLimitInSharedClassifier()
    {
        const string stderr =
            "Failed to get response from the AI model; retried 5 times (total retry wait time: 91.99 seconds). Last error: 429 Error from provider (Console Go): Upstream request failed: [rate_limit_exceeded] Rate limit exceeded. Please retry after a brief wait.";

        var classification = AgentFailureClassifier.Classify(stderr);

        Assert.Equal(AgentFailureKind.QuotaExhausted, classification.Kind);
        Assert.Equal(AgentQuotaFailureKind.SoftRateLimit, classification.QuotaFailure);
    }

    [Theory]
    [InlineData("Error: quota exceeded for this billing period")]
    [InlineData("Provider returned quota exhausted for the current window")]
    [InlineData("usage_limit hit for model; upgrade or wait")]
    [InlineData("Monthly usage limit reached for this account")]
    [InlineData("insufficient credits to complete the request")]
    public void Detect_HardQuotaPatterns_ClassifyAsLimitReached(string stderr)
    {
        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_UsageLimitProseWithoutExhaustionVerb_ReturnsNull()
    {
        Assert.Null(_detector.Detect(
            stderr: "Review the usage limit handling in src/quota/Limiter.cs",
            stdout: null));
    }

    [Fact]
    public void Detect_RetryAfterHeaderEcho_IsHonouredAsResetAt()
    {
        const string stderr =
            "429 Error from provider (Console Go): Upstream request failed: [rate_limit_exceeded] Rate limit exceeded. Retry-After: 120";

        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalSeconds, 120 - ResetAtAssertSkew.TotalSeconds, 120 + ResetAtAssertSkew.TotalSeconds);
    }

    [Fact]
    public void Detect_CompactResetTail_PreferredOverRetryAfterHeader()
    {
        const string stderr =
            "rate_limit_exceeded; reset after 5m. Retry-After: 600";

        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalSeconds, 300 - ResetAtAssertSkew.TotalSeconds, 300 + ResetAtAssertSkew.TotalSeconds);
    }

    [Fact]
    public void Detect_BriefWaitWithoutDuration_YieldsNullResetAtForDefaultBackoff()
    {
        const string stderr =
            "Failed to get response from the AI model; retried 5 times (total retry wait time: 91.99 seconds). Last error: 429 Error from provider (Console Go): Upstream request failed: [rate_limit_exceeded] Rate limit exceeded. Please retry after a brief wait.";

        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.Null(detection.ResetAt);
    }

    /// <summary>
    /// The classifier must not swallow genuine work failures: none of these
    /// carry a 429 anchor or a rate-limit token, so a real agent failure
    /// (bad model id, oversized prompt, refusal) still terminates the item
    /// instead of parking it for a quota window that will never clear it.
    /// </summary>
    [Theory]
    [InlineData("agent exited 1", "Error: model 'unknown-xyz' not found at provider endpoint")]
    [InlineData("agent exited 1", "Failed to get response from the AI model: context window exceeded by prompt")]
    [InlineData("agent exited 1", "Error: prompt exceeds MAX_ARG_STRLEN; rework prompt too large")]
    [InlineData("agent exited 1", "ordinary model refusal: cannot complete that task")]
    [InlineData("agent exited 1", "processed 429 files successfully")]
    [InlineData("agent exited 1", "build failed in 429ms: error CS1002")]
    public void Detect_GenuineFailures_ReturnNull(string summary, string stderr)
    {
        Assert.Null(_detector.Detect(stderr: stderr, stdout: null));
        Assert.Null(_detector.Detect(stderr: summary, stdout: stderr));
    }

    [Fact]
    public void Detect_EmptyInputs_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: null, stdout: null));
        Assert.Null(_detector.Detect(stderr: string.Empty, stdout: string.Empty));
    }

    [Fact]
    public void Kind_IsCopilot()
    {
        Assert.Equal(AgentKind.Copilot, _detector.Kind);
    }
}

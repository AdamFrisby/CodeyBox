using CodeyBox.Agents.Omp;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="OmpQuotaFailureDetector"/>: the live-verified omp
/// 18.2.2 failure shapes (paid-model spend-limit refusal on stdout, missing
/// key on stderr), operator-extensible patterns, and non-matching prose.
/// </summary>
public sealed class OmpQuotaFailureDetectorTests
{
    [Fact]
    public void Kind_IsOmp()
    {
        Assert.Equal(AgentKind.Omp, new OmpQuotaFailureDetector().Kind);
    }

    [Fact]
    public void Detect_SpendLimitRefusal_ReturnsLimitReached()
    {
        // Recorded real refusal (paid model on a $0-spend-limit key): the
        // provider body rides verbatim in errorMessage (key-id tail
        // redacted — the pattern only needs the refusal sentence).
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"error\"," +
            "\"errorMessage\":\"403 Key limit exceeded (total limit).\"}}";

        var detection = new OmpQuotaFailureDetector().Detect(stderr: string.Empty, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection.Kind);
    }

    [Fact]
    public void Detect_MissingKeyStderrCrash_ReturnsUnauthorized()
    {
        // Recorded real pre-session failure: the bun crash line on stderr,
        // session header only on stdout.
        const string stderr = "error: No API key found for anthropic.";

        var detection = new OmpQuotaFailureDetector().Detect(stderr: stderr, stdout: string.Empty);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection.Kind);
    }

    [Fact]
    public void Detect_HealthyRun_ReturnsNull()
    {
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"," +
            "\"usage\":{\"input\":19200,\"output\":45}}}";

        Assert.Null(new OmpQuotaFailureDetector().Detect(stderr: string.Empty, stdout: stdout));
    }

    [Fact]
    public void Detect_EmptyStreams_ReturnsNull()
    {
        Assert.Null(new OmpQuotaFailureDetector().Detect(stderr: null, stdout: null));
        Assert.Null(new OmpQuotaFailureDetector().Detect(stderr: string.Empty, stdout: string.Empty));
    }

    [Fact]
    public void Detect_OperatorPatterns_AppendedAfterDefaults()
    {
        var detector = new OmpQuotaFailureDetector(
            [new QuotaFailurePattern("custom exhausted marker", QuotaFailureKind.LimitReached)]);

        // Defaults still fire first.
        Assert.NotNull(detector.Detect(stderr: "No API key found for x", stdout: string.Empty));
        // The operator pattern fires too.
        var custom = detector.Detect(stderr: string.Empty, stdout: "run hit the custom exhausted marker today");
        Assert.NotNull(custom);
        Assert.Equal(QuotaFailureKind.LimitReached, custom.Kind);
    }

    [Fact]
    public void Detect_UnrelatedProseWithBareNumbers_ReturnsNull()
    {
        // Bare status codes in reviewed prose must not gate dispatch — only
        // provider-shaped phrases match.
        const string stdout = "the endpoint returned 403 in the test fixture for retry logic";

        Assert.Null(new OmpQuotaFailureDetector().Detect(stderr: string.Empty, stdout: stdout));
    }
}

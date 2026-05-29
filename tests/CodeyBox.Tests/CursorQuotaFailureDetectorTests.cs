using CodeyBox.Agents.Cursor;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Synthetic-stderr / synthetic-stdout pattern tests for
/// <see cref="CursorQuotaFailureDetector"/>.
///
/// <para>Per the operator's feedback-vendor-api-drift preference (reactive
/// over speculative), the patterns are a conservative allowlist; real failure
/// shapes will be added as they appear in production audit logs. The
/// assertions here use synthetic strings, NOT real recordings — the operator
/// has explicitly accepted this trade-off.</para>
/// </summary>
public sealed class CursorQuotaFailureDetectorTests
{
    private readonly CursorQuotaFailureDetector _detector = new();

    [Fact]
    public void Kind_IsCursor()
    {
        Assert.Equal(AgentKind.Cursor, _detector.Kind);
    }

    [Theory]
    [InlineData("Error: usage limit reached for this billing period")]
    [InlineData("ERROR: monthly quota exceeded")]
    [InlineData("HTTP 402 Payment Required")]
    public void Detect_LimitShapes_ClassifyAsLimitReached(string stderr)
    {
        var detection = _detector.Detect(stderr, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Theory]
    [InlineData("rate limit exceeded; retry after 30m")]
    [InlineData("rate_limit_exceeded")]
    [InlineData("HTTP 429 Too Many Requests")]
    public void Detect_RateLimitShapes_ClassifyAsRateLimit(string stderr)
    {
        var detection = _detector.Detect(stderr, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_AuthShape_ClassifyAsUnauthorized()
    {
        var detection = _detector.Detect(stderr: "HTTP 401 Unauthorized: token expired", stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_StdoutMatch_AlsoCounts()
    {
        var detection = _detector.Detect(stderr: null, stdout: "Error: rate limit reached");
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_NonQuotaError_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: "compilation failed: missing semicolon", stdout: null));
    }

    [Fact]
    public void Detect_EmptyInputs_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: null, stdout: null));
        Assert.Null(_detector.Detect(stderr: "", stdout: ""));
    }

    [Fact]
    public void Detect_ResetTimeExtracted_WhenPresentInText()
    {
        var detection = _detector.Detect(stderr: "rate limit exceeded; retry after 1h30m", stdout: null);
        Assert.NotNull(detection);
        Assert.NotNull(detection!.ResetAt);
        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalMinutes, 89, 91);
    }

    [Theory]
    [InlineData("You're out of usage. Switch to Auto, or ask your admin to increase your limit to continue.")]
    [InlineData("Error: out of usage")]
    [InlineData("Please Switch to Auto to keep working.")]
    [InlineData("ask your admin to increase your limit")]
    public void Detect_CursorOutOfUsageShapes_ClassifyAsLimitReached(string stderr)
    {
        // Regression for the cursor subscription-exhausted bug: previously this
        // stderr fell through detection, the failure was classified as "other",
        // and the work item hard-failed instead of failing over to the next
        // eligible class member.
        var detection = _detector.Detect(stderr, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_OperatorAdditionalPatterns_AreAppendedAfterDefaults()
    {
        var detector = new CursorQuotaFailureDetector(
            additionalPatterns: [new QuotaFailurePattern("cursor-vNext-marker", QuotaFailureKind.LimitReached)]);

        // Built-in defaults still classified.
        Assert.NotNull(detector.Detect("rate_limit_exceeded", stdout: null));

        // Operator-supplied pattern also classified.
        var detection = detector.Detect("error: cursor-vNext-marker", stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_AdditionalPatternsNullOrEmpty_BehavesLikeDefaultDetector()
    {
        var nullCase = new CursorQuotaFailureDetector(additionalPatterns: null);
        var emptyCase = new CursorQuotaFailureDetector(additionalPatterns: Array.Empty<QuotaFailurePattern>());

        Assert.NotNull(nullCase.Detect("rate_limit_exceeded", stdout: null));
        Assert.NotNull(emptyCase.Detect("rate_limit_exceeded", stdout: null));
        Assert.Null(nullCase.Detect("cursor-vNext-marker", stdout: null));
        Assert.Null(emptyCase.Detect("cursor-vNext-marker", stdout: null));
    }
}

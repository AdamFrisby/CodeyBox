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
}

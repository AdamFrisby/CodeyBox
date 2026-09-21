using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pattern-coverage tests for <see cref="DevinQuotaFailureDetector"/>. The
/// devin-specific strings come from the 3000.11.1 binary's status wording;
/// the CLI surfaces failures as <c>Error: …</c> on stderr (verified), so both
/// streams are scanned.
/// </summary>
public sealed class DevinQuotaFailureDetectorTests
{
    private readonly DevinQuotaFailureDetector _detector = new();

    [Fact]
    public void Kind_IsDevin()
    {
        Assert.Equal(AgentKind.Devin, _detector.Kind);
    }

    [Theory]
    [InlineData("Error: Quota exhausted")]
    [InlineData("Error: Usage limit reached")]
    [InlineData("Error: Usage paused")]
    [InlineData("Purchase on-demand usage or turn on auto-reload, or wait for your quota to reset.")]
    [InlineData("account is out of ACUs")]
    [InlineData("HTTP 402 Payment Required")]
    public void Detect_QuotaShapes_ClassifyAsLimitReached(string stderr)
    {
        var detection = _detector.Detect(stderr, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Theory]
    [InlineData("rate limit exceeded; retry after 30m")]
    [InlineData("HTTP 429 Too Many Requests")]
    public void Detect_RateLimitShapes_ClassifyAsRateLimit(string stderr)
    {
        var detection = _detector.Detect(stderr, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Theory]
    [InlineData("Error: Not logged in. Run `devin auth login` to authenticate.")]
    [InlineData("Error: Devin needs authentication")]
    [InlineData("Authentication required")]
    [InlineData("HTTP 401 Unauthorized")]
    public void Detect_AuthShapes_ClassifyAsUnauthorized(string stderr)
    {
        var detection = _detector.Detect(stderr, stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_StdoutMatch_AlsoCounts()
    {
        var detection = _detector.Detect(stderr: null, stdout: "Error: Quota exhausted");
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_NormalAssistantOutput_ReturnsNull()
    {
        // Model output discussing quota in prose must not gate dispatch —
        // patterns are anchored to the CLI's own status wording.
        var detection = _detector.Detect(
            stderr: null,
            stdout: "The function checks whether the user's quota is sufficient before calling the API.");
        Assert.Null(detection);
    }

    [Fact]
    public void Detect_Empty_ReturnsNull()
    {
        Assert.Null(_detector.Detect(null, null));
        Assert.Null(_detector.Detect("", ""));
    }

    [Fact]
    public void Detect_RateLimitPlusQuota_PrefersRateLimit()
    {
        // Rate-limit rows precede quota rows in DefaultPatterns so a refusal
        // carrying both parks on the retriable classification.
        var detection = _detector.Detect(
            "Error: Quota exhausted; rate limit exceeded", stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void AdditionalPatterns_AppendToDefaults()
    {
        var detector = new DevinQuotaFailureDetector(
            [new QuotaFailurePattern("custom tenant throttle", QuotaFailureKind.RateLimitExceeded)]);

        var detection = detector.Detect("custom tenant throttle engaged", stdout: null);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);

        // Defaults still apply.
        Assert.NotNull(detector.Detect("Error: Quota exhausted", stdout: null));
    }
}

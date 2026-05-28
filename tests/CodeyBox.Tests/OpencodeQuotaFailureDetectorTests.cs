using CodeyBox.Agents.Opencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class OpencodeQuotaFailureDetectorTests
{
    private static readonly string SubscriptionLimitFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "Opencode",
        "opencode-subscription-limit.redacted.txt");

    private readonly OpencodeQuotaFailureDetector _detector = new();

    // --- LimitReached patterns ------------------------------------------------

    /// <summary>
    /// Each opencode stderr shape we recognise as a quota-exhaustion signal.
    /// Mirrors the prod <c>Patterns</c> table; if a row is removed or mistyped
    /// in production, the corresponding test row here will fail. Each entry's
    /// sample text deliberately wraps the literal pattern in realistic-looking
    /// surrounding context so we exercise the substring match rather than
    /// regex semantics that the production code does not use.
    /// </summary>
    public static IEnumerable<object[]> LimitReachedSamples()
    {
        yield return new object[] { "Error: HTTP 402 from upstream", "HTTP 402" };
        yield return new object[] { "Status: 402 Payment Required (subscription cap)", "402 Payment Required" };
        yield return new object[] { "Error: insufficient credits for this request", "insufficient credits" };
        yield return new object[] { "Plan limit reached. Try again later.", "limit reached" };
        yield return new object[] { "Provider returned: quota exceeded for this month", "quota exceeded" };
        yield return new object[] { "monthly quota exhausted", "quota exhausted" };
        yield return new object[] { "Account quota reached for tier 'go'", "quota reached" };
        // "monthly quota" alone (without "quota exhausted" earlier in the text)
        // must still match — operator-supplied phrasings vary.
        yield return new object[] { "Your monthly quota for DeepSeek has been used", "monthly quota" };
    }

    [Theory]
    [MemberData(nameof(LimitReachedSamples))]
    public void Detect_LimitReachedPatterns_AllClassifyAsLimitReached(string stderr, string pattern)
    {
        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        // Loop sanity: keep the pattern token in scope so a future contributor
        // changing the surrounding text accidentally also has to update the
        // pattern, surfacing intent.
        Assert.Contains(pattern, stderr, StringComparison.OrdinalIgnoreCase);
    }

    // --- Unauthorized patterns ------------------------------------------------

    [Theory]
    [InlineData("401 Unauthorized: token rejected")]
    [InlineData("API Error: 401 — credentials invalid")]
    public void Detect_UnauthorizedPatterns_ClassifyAsUnauthorized(string stderr)
    {
        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    // --- Anti-false-positive: ensure HTTP-status anchoring isn't reverted ----
    // The production detector intentionally anchors patterns with HTTP-status
    // text (e.g. "HTTP 402", "401 Unauthorized") rather than bare numerics or
    // single words. opencode prompts can carry repository content under
    // review; an agent reviewing rate-limit code (or a stack trace that
    // mentions 402 in passing) must NOT be misclassified as quota-exhausted.
    // If a future edit relaxes "HTTP 402" back to bare "402", these tests
    // catch that regression.

    [Theory]
    [InlineData("Server returned a 402 Bad Request (this is a code review of the limit handler)")]
    [InlineData("The function handles 402 errors by retrying with backoff")]
    [InlineData("HTTP/1.1 402 BadGateway — should never happen for our flow")]
    public void Detect_Bare402WithoutHttpAnchor_DoesNotFalsePositive(string code)
    {
        // None of these contain "HTTP 402" or "402 Payment Required" — they
        // are arbitrary mentions of the number 402 in code/prose. Detector
        // must not flag.
        Assert.Null(_detector.Detect(stderr: code, stdout: null));
    }

    [Theory]
    [InlineData("Discussing Unauthorized access patterns in section 4.3")]
    [InlineData("// throw new UnauthorizedAccessException when token missing")]
    public void Detect_Unauthorized_WithoutHttpAnchor_DoesNotFalsePositive(string code)
    {
        // "Unauthorized" alone (not "401 Unauthorized" / "API Error: 401")
        // must not trip the detector.
        Assert.Null(_detector.Detect(stderr: code, stdout: null));
    }

    [Theory]
    [InlineData("Review the quota mechanism in src/quota/Limiter.cs")]
    [InlineData("Quota tracking is implemented per-tenant")]
    [InlineData("// TODO: add quota field to user record")]
    public void Detect_QuotaWord_WithoutVerbOfExhaustion_DoesNotFalsePositive(string code)
    {
        // The bare word "quota" must require a verb of exhaustion
        // (exceeded / exhausted / reached) or the "monthly quota" phrase to
        // match — otherwise reviewing-quota-code text would gate dispatch.
        Assert.Null(_detector.Detect(stderr: code, stdout: null));
    }

    // --- Source selection / cross-stream / shape edges -----------------------

    [Fact]
    public void Detect_StdoutSource_Recognised()
    {
        var detection = _detector.Detect(stderr: null, stdout: "Error: HTTP 402 Payment Required");

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_NoMatch_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: "ordinary model refusal", stdout: null));
    }

    [Fact]
    public void Detect_EmptyInputs_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: null, stdout: null));
        Assert.Null(_detector.Detect(stderr: string.Empty, stdout: string.Empty));
    }

    [Fact]
    public void Kind_IsOpencode()
    {
        Assert.Equal(AgentKind.Opencode, _detector.Kind);
    }

    // --- OpenCode subscription rolling-window stderr (upstream fixture) -------

    [Fact]
    public void Detect_FiveHourUsageLimitFixture_ClassifiesAsLimitReachedWithResetAt()
    {
        var stderr = File.ReadAllText(SubscriptionLimitFixturePath);

        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);

        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalHours, 5.35, 5.45);
    }

    [Fact]
    public void Detect_WeeklyUsageLimitReached_ParsesResetAt()
    {
        const string stderr =
            "weekly usage limit reached. It will reset in 2 hours 30 minutes.";

        var detection = _detector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);

        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalMinutes, 149, 151);
    }

    [Fact]
    public void Detect_UnrelatedStderr_DoesNotClassifyAsQuota()
    {
        Assert.Null(_detector.Detect(stderr: "npm: command not found", stdout: null));
    }
}

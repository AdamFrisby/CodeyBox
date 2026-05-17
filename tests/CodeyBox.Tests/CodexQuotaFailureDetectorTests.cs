using CodeyBox.Agents.Codex;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class CodexQuotaFailureDetectorTests
{
    private readonly CodexQuotaFailureDetector _detector = new();

    private const string CodexQuotaStreamJson =
        """{"msg":{"type":"error","message":"You hit your usage limit. Try again after 5m17s."}}""";

    [Fact]
    public void Detect_CodexStreamJsonStdout_ClassifiesLimitReached()
    {
        var detection = _detector.Detect(stderr: null, stdout: CodexQuotaStreamJson);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);

        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalSeconds, 315, 319);
    }

    [Fact]
    public void Detect_StderrUsageLimit_ClassifiesLimitReached()
    {
        var detection = _detector.Detect(stderr: "You hit your usage limit. Try again after 5m.", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_AuthError_ClassifiesUnauthorized()
    {
        var detection = _detector.Detect(stderr: "API Error: 401 unauthorized", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_NonQuotaError_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: "ordinary model error", stdout: null));
    }

    [Fact]
    public void Detect_ClaudeRateLimitText_ReturnsNull()
    {
        // Codex detector must not match claude's rate_limit_exceeded —
        // it would mask a real codex-vs-claude routing decision.
        Assert.Null(_detector.Detect(stderr: "rate_limit_exceeded", stdout: null));
    }
}

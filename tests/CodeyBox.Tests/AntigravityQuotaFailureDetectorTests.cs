using CodeyBox.Agents.Antigravity;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AntigravityQuotaFailureDetectorTests
{
    private readonly AntigravityQuotaFailureDetector _detector = new();

    [Fact]
    public void Detect_WeeklyLockoutWithStructuredReset_ParksUntilLockoutEnd()
    {
        // The AI Pro weekly cap surfaces with a structured quota_metadata.lockout_until
        // alongside human-readable text. A 7-day lockout must NOT churn the breaker;
        // it must surface the absolute reset so the work item parks WaitingForQuotaReset
        // until then.
        var lockoutUntil = DateTimeOffset.UtcNow.AddDays(6).AddHours(23);
        var stdout =
            "{\"type\":\"result\",\"status\":\"error\","
            + "\"quota_metadata\":{\"lockout_until\":\"" + lockoutUntil.ToString("o") + "\"},"
            + "\"error\":{\"message\":\"Weekly limit reached — account locked until " + lockoutUntil.ToString("o") + "\"}}";

        var detection = _detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        Assert.InRange(detection.ResetAt!.Value, lockoutUntil.AddMinutes(-1), lockoutUntil.AddMinutes(1));
    }

    [Fact]
    public void Detect_ResourceExhaustedStderr_ClassifiesRateLimit()
    {
        var detection = _detector.Detect(stderr: "RESOURCE_EXHAUSTED reset after 1h17m", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_AbsoluteLockoutInPlainText_StillExtractsReset()
    {
        var when = DateTimeOffset.UtcNow.AddHours(48);
        var stdout = $"agent_error: account locked until {when:o} due to weekly cap";

        var detection = _detector.Detect(stderr: null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_Unauthorized_FlagsAsUnauthorized()
    {
        var detection = _detector.Detect(stderr: "API Error: 401 invalid token", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.Unauthorized, detection!.Kind);
    }

    [Fact]
    public void Detect_GenericRuntimeError_ReturnsNull()
    {
        var stdout = """{"type":"result","status":"error","error":{"message":"compilation failed: missing semicolon"}}""";
        Assert.Null(_detector.Detect(stderr: null, stdout));
    }

    [Fact]
    public void Detect_EmptyStreams_ReturnsNull()
    {
        Assert.Null(_detector.Detect(stderr: null, stdout: null));
        Assert.Null(_detector.Detect(stderr: "", stdout: ""));
    }
}

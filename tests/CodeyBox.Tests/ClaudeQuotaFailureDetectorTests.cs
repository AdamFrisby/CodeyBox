using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class ClaudeQuotaFailureDetectorTests
{
    private readonly ClaudeQuotaFailureDetector _detector = new();

    private const string ClaudeQuotaStreamJson =
        """
        {"type":"system","subtype":"init","session_id":"s1"}
        {"type":"result","subtype":"error","is_error":true,"result":"Error: rate_limit_exceeded — please retry after 30m"}
        """;

    [Fact]
    public void Detect_ClaudeStreamJsonStdout_ClassifiesRateLimit()
    {
        var detection = _detector.Detect(stderr: null, stdout: ClaudeQuotaStreamJson);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);

        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalMinutes, 29, 31);
    }

    [Fact]
    public void Detect_StderrOnly_RegressionStillWorks()
    {
        var detection = _detector.Detect(stderr: "API Error: rate_limit_exceeded reset after 1h", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_SuccessfulStreamJson_ReturnsNull()
    {
        var stdout = """
            {"type":"system","subtype":"init","session_id":"s1"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"}]}}
            {"type":"result","subtype":"success","result":"ok"}
            """;

        Assert.Null(_detector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_GenericStreamJsonError_NotQuota_ReturnsNull()
    {
        var stdout = """{"type":"result","subtype":"error","is_error":true,"result":"compilation failed: missing semicolon"}""";

        Assert.Null(_detector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_GeminiStreamShape_ReturnsNull()
    {
        var stdout =
            """{"type":"result","status":"error","error":{"message":"[API Error: You have exhausted your capacity on this model.]"}}""";

        // Claude detector must not silently match Gemini-shaped errors — that
        // would defeat the per-provider boundary the composite enforces.
        Assert.Null(_detector.Detect(stderr: null, stdout: stdout));
    }
}

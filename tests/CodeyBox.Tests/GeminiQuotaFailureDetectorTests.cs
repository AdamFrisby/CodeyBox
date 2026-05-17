using CodeyBox.Agents.Gemini;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class GeminiQuotaFailureDetectorTests
{
    private readonly GeminiQuotaFailureDetector _detector = new();

    // Real captured shape from gemini-cli @ 23:50 UTC, 2026-05-10:
    private const string GeminiQuotaStreamJson =
        """{"type":"result","timestamp":"2026-05-10T23:50:25.686Z","status":"error","error":{"type":"unknown","message":"[API Error: You have exhausted your capacity on this model. Your quota will reset after 21h41m24s.]"}}""";

    [Fact]
    public void Detect_GeminiStreamJsonStdout_ClassifiesQuotaAndExtractsReset()
    {
        var detection = _detector.Detect(stderr: null, stdout: GeminiQuotaStreamJson);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);

        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalMinutes, 21 * 60 + 41 - 1, 21 * 60 + 41 + 1);
    }

    [Fact]
    public void Detect_ResourceExhaustedStderr_ClassifiesRateLimit()
    {
        var detection = _detector.Detect(stderr: "RESOURCE_EXHAUSTED reset after 20h31m6s", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_QuotaExceededInUnstructuredOutput_ClassifiesRateLimit()
    {
        var stdout = """
            {this is not valid json}
            {"type":"result","status":"success","result":"ok"}
            quota exceeded retry after 10m
            """;

        var detection = _detector.Detect(stderr: null, stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
    }

    [Fact]
    public void Detect_StreamJsonWithMalformedLines_StillFindsQuotaError()
    {
        var stdout = """
            {this is not valid json}
            {"type":"system","subtype":"init"}
            """ + "\n" + GeminiQuotaStreamJson;

        var detection = _detector.Detect(stderr: null, stdout: stdout);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_GenericStreamJsonError_NotQuota_ReturnsNull()
    {
        var stdout = """{"type":"result","status":"error","error":{"type":"runtime","message":"compilation failed: missing semicolon"}}""";

        Assert.Null(_detector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_CodexStreamShape_ReturnsNull()
    {
        var stdout =
            """{"msg":{"type":"error","message":"You hit your usage limit. Try again after 5m17s."}}""";

        // Gemini must not silently match Codex-shaped wrapped errors.
        Assert.Null(_detector.Detect(stderr: null, stdout: stdout));
    }
}

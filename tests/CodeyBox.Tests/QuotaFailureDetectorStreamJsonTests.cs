using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Stream-json detection: verifies that <see cref="QuotaFailureDetector"/>
/// classifies quota errors emitted as structured stdout events by each
/// agent CLI, and that it parses the reset interval into
/// <see cref="WorkItem.QuotaResetAt"/> via <see cref="QuotaDetection.ResetAt"/>.
/// Regression for the WI 098ed66a misclassification (gemini stream-json error
/// in stdout, not stderr).
/// </summary>
public sealed class QuotaFailureDetectorStreamJsonTests
{
    // Real captured shape from gemini-cli @ 23:50 UTC, 2026-05-10:
    private const string GeminiQuotaStreamJson =
        """{"type":"result","timestamp":"2026-05-10T23:50:25.686Z","status":"error","error":{"type":"unknown","message":"[API Error: You have exhausted your capacity on this model. Your quota will reset after 21h41m24s.]"}}""";

    // Claude Code stream-json error result event:
    private const string ClaudeQuotaStreamJson =
        """
        {"type":"system","subtype":"init","session_id":"s1"}
        {"type":"result","subtype":"error","is_error":true,"result":"Error: rate_limit_exceeded — please retry after 30m"}
        """;

    // Codex CLI emits errors wrapped in {"msg":{"type":"error","message":"..."}}:
    private const string CodexQuotaStreamJson =
        """{"msg":{"type":"error","message":"You hit your usage limit. Try again after 5m17s."}}""";

    [Fact]
    public void Detect_GeminiStreamJsonStdout_ClassifiesQuotaAndExtractsReset()
    {
        var detection = QuotaFailureDetector.Detect(stderr: null, stdout: GeminiQuotaStreamJson);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);

        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalMinutes, 21 * 60 + 41 - 1, 21 * 60 + 41 + 1);
    }

    [Fact]
    public void Detect_ClaudeStreamJsonStdout_ClassifiesRateLimit()
    {
        var detection = QuotaFailureDetector.Detect(stderr: null, stdout: ClaudeQuotaStreamJson);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);

        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalMinutes, 29, 31);
    }

    [Fact]
    public void Detect_CodexStreamJsonStdout_ClassifiesLimitReached()
    {
        var detection = QuotaFailureDetector.Detect(stderr: null, stdout: CodexQuotaStreamJson);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);

        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        // 5m17s = 317 seconds — assert window of 315–319.
        Assert.InRange(diff.TotalSeconds, 315, 319);
    }

    [Fact]
    public void Detect_GenericStreamJsonError_NotQuota_ReturnsNull()
    {
        // Non-quota agent error must not be misclassified as quota.
        var stdout = """{"type":"result","status":"error","error":{"type":"runtime","message":"compilation failed: missing semicolon"}}""";

        Assert.Null(QuotaFailureDetector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_SuccessfulStreamJson_ReturnsNull()
    {
        var stdout = """
            {"type":"system","subtype":"init","session_id":"s1"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"done"}]}}
            {"type":"result","subtype":"success","result":"ok"}
            """;

        Assert.Null(QuotaFailureDetector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_StreamJsonWithMalformedLines_StillFindsQuotaError()
    {
        // A real stream often has well-formed and malformed lines interleaved
        // (truncated buffers, debug prints). Detection must keep parsing.
        var stdout = """
            {this is not valid json}
            {"type":"system","subtype":"init"}
            """ + "\n" + GeminiQuotaStreamJson;

        var detection = QuotaFailureDetector.Detect(stderr: null, stdout: stdout);
        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
    }

    [Fact]
    public void Detect_StderrOnly_RegressionStillWorks()
    {
        // The pre-existing stderr path must keep working — stderr-only
        // quota errors (e.g. claude HTTP 429) were the original mode.
        var detection = QuotaFailureDetector.Detect(stderr: "API Error: rate_limit_exceeded reset after 1h", stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_ResetIntervalWithoutResetPrefix_ParsesViaTryAgain()
    {
        // "try again after Xh Ym Zs" is an alternate phrasing — should parse.
        var stdout = """{"type":"result","status":"error","error":{"message":"hit your usage limit. try again after 2h30m"}}""";

        var detection = QuotaFailureDetector.Detect(stderr: null, stdout: stdout);
        Assert.NotNull(detection);
        Assert.NotNull(detection!.ResetAt);
        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalMinutes, 149, 151);
    }
}

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

    [Fact]
    public void Detect_ClaudeRateLimitEventOverageRejected_ClassifiesQuotaWithEpochReset()
    {
        // Captured shape from the operator queue on 2026-05-16: the run
        // streams an init row, a rate_limit_event with overageStatus=rejected,
        // then a partial assistant row before the CLI exits 1. Before the fix,
        // the classifier returned null because no error message text matched.
        const long resetsAtEpoch = 1778937600L;
        var rateLimitLine =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":__EPOCH__,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false}}"""
                .Replace("__EPOCH__", resetsAtEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var stdout =
            """{"type":"system","subtype":"init","session_id":"abc"}""" + "\n" +
            rateLimitLine + "\n" +
            """{"type":"assistant","message":{"model":"claude-opus-4-7","stop_reason":null}}""";

        var detection = QuotaFailureDetector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetsAtEpoch), detection.ResetAt);
    }

    [Fact]
    public void Detect_ClaudeRateLimitEventStatusExceeded_ClassifiesQuota()
    {
        // status=exceeded on its own is enough — the base window is gone even
        // if overage info isn't present.
        var stdout =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"exceeded","resetsAt":1778937600,"rateLimitType":"five_hour"}}""";

        var detection = QuotaFailureDetector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
    }

    [Fact]
    public void Detect_ClaudeRateLimitEventAllowedNoOverageBlock_ReturnsNull()
    {
        // A healthy heartbeat — base allowed, no overage rejection. Without
        // any other failure signal in the stream, this must not be classified
        // as quota (would mask real errors).
        var stdout =
            """{"type":"system","subtype":"init","session_id":"x"}""" + "\n" +
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1778937600,"rateLimitType":"five_hour","overageStatus":"allowed","isUsingOverage":false}}""" + "\n" +
            """{"type":"result","subtype":"success","result":"ok"}""";

        Assert.Null(QuotaFailureDetector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_StreamJsonWithoutRateLimitEvent_ExitOne_NotQuota()
    {
        // Regression: a stream with no rate_limit_event and no error keywords
        // (just init + assistant) must stay unclassified — the surrounding
        // pipeline treats this as a generic failure ("other"), not quota.
        var stdout =
            """{"type":"system","subtype":"init","session_id":"y"}""" + "\n" +
            """{"type":"assistant","message":{"model":"claude-opus-4-7","stop_reason":null}}""";

        Assert.Null(QuotaFailureDetector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_CodexStderr429TooManyRequests_ClassifiesRateLimit()
    {
        // codex CLI prints the raw HTTP status to stderr before exiting 1.
        const string stderr = "HTTP 429 Too Many Requests\nplease try again after 1h";

        var detection = QuotaFailureDetector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        Assert.InRange(diff.TotalMinutes, 59, 61);
    }

    [Fact]
    public void Detect_GeminiStderrQuotaExhaustedWithResetAfter_ClassifiesQuota()
    {
        // gemini-cli reports per-account exhaustion as "QUOTA_EXHAUSTED" in
        // stderr, with the wait expressed as "reset after 7h8m8s".
        const string stderr = "[ERROR] QUOTA_EXHAUSTED: model quota will reset after 7h8m8s";

        var detection = QuotaFailureDetector.Detect(stderr: stderr, stdout: null);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.LimitReached, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        var diff = detection.ResetAt!.Value - DateTimeOffset.UtcNow;
        // 7h8m8s = 428m + 8s — assert window of ±1 minute.
        Assert.InRange(diff.TotalMinutes, 427, 429);
    }
}

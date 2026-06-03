using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using Serilog;
using Serilog.Events;

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
    public void Detect_RateLimitEvent_FiveHourRejected_ClassifiesAsQuotaWithResetAt()
    {
        // Captured shape from the 2026-06-02/03 incident (work item 36a7ff0c):
        // claude exhausts its 5-hour subscription window mid-run and emits a
        // rate_limit_event with status=rejected. Before this branch the
        // QuotaClassifier returned null for this shape — the orchestrator
        // recorded failureKind="other" and hard-Failed the item instead of
        // parking it as WaitingForQuotaReset. resetsAt below is a future unix
        // timestamp (2026-07-01) chosen to outlast the test's lifetime.
        const long ResetsAtUnixSeconds = 1782000000L;
        var stdout =
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\",\"resetsAt\":"
            + ResetsAtUnixSeconds
            + ",\"rateLimitType\":\"five_hour\",\"overageStatus\":\"rejected\"}}";

        var detection = _detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.NotNull(detection.ResetAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(ResetsAtUnixSeconds), detection.ResetAt);
    }

    [Theory]
    [InlineData("seven_day")]
    [InlineData("weekly")]
    public void Detect_RateLimitEvent_WeeklyRejected_ClassifiesAsQuota(string rateLimitType)
    {
        const long ResetsAtUnixSeconds = 1782000000L;
        var stdout =
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\",\"resetsAt\":"
            + ResetsAtUnixSeconds
            + ",\"rateLimitType\":\"" + rateLimitType + "\"}}";

        var detection = _detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(ResetsAtUnixSeconds), detection.ResetAt);
    }

    [Fact]
    public void Detect_RateLimitEvent_AllowedStatus_ReturnsNull()
    {
        // The CLI also emits rate_limit_event lines for non-rejection telemetry
        // (status=allowed). Those must not classify as quota failures.
        var stdout =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1782000000,"rateLimitType":"five_hour"}}""";

        Assert.Null(_detector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_RateLimitEvent_UnknownRateLimitType_ReturnsNull()
    {
        // Defensive: only quota-scope window types are treated as terminal.
        // Unknown rateLimitType values must not silently classify as quota.
        var stdout =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1782000000,"rateLimitType":"per_minute"}}""";

        Assert.Null(_detector.Detect(stderr: null, stdout: stdout));
    }

    [Fact]
    public void Detect_RateLimitEvent_MissingResetsAt_StillClassifiesAsQuota()
    {
        // A missing/malformed resetsAt must not lose the quota signal — the
        // orchestrator falls back to its own quota-reset policy when ResetAt
        // is null.
        var stdout =
            """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"five_hour"}}""";

        var detection = _detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.Null(detection.ResetAt);
    }

    [Fact]
    public void Detect_RateLimitEvent_StringFormResetsAt_StillExtractsResetAt()
    {
        // Some CLI versions / proxies serialize resetsAt as a JSON string. The
        // detector must accept the string-form so the quota-reset timer is
        // armed at the actual reset (rather than the orchestrator's default
        // pause). A regression that only matched the number branch would
        // silently degrade NextQuotaRetryAt for half the wire shapes.
        const long ResetsAtUnixSeconds = 1782000000L;
        var stdout =
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\",\"resetsAt\":\""
            + ResetsAtUnixSeconds
            + "\",\"rateLimitType\":\"five_hour\"}}";

        var detection = _detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(ResetsAtUnixSeconds), detection.ResetAt);
    }

    [Fact]
    public void Detect_RateLimitEvent_OutOfRangeUnixSeconds_StillClassifiesAsQuotaWithNullReset()
    {
        // DateTimeOffset.FromUnixTimeSeconds throws on values outside its
        // representable range. A malformed payload must NOT lose the quota
        // signal (the orchestrator still needs to park as WaitingForQuotaReset),
        // but ResetAt must come through as null so the resolver falls back to
        // the configured default pause window instead of crashing the
        // classification. A regression that propagated the throw would
        // re-introduce the "hard-Fail on unparseable resetsAt" failure mode
        // for any CLI variant that emits a bogus value.
        const long OutOfRangeUnixSeconds = long.MaxValue;
        var stdout =
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\",\"resetsAt\":"
            + OutOfRangeUnixSeconds
            + ",\"rateLimitType\":\"five_hour\"}}";

        var detection = _detector.Detect(stderr: null, stdout: stdout);

        Assert.NotNull(detection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, detection!.Kind);
        Assert.Null(detection.ResetAt);
    }

    [Fact]
    public void ScopeStdoutForQuotaDetection_PreservesRateLimitEventBesideTerminalError()
    {
        // When a terminal stream-json error coexists with a rate_limit_event
        // rejection, the narrowing step must preserve the *parseable*
        // rate_limit_event line — not just stray substrings — so Detect's
        // subsequent JSON parse still finds the rejection and surfaces resetsAt.
        // A plausible regression here would be to truncate or corrupt the JSON
        // line while leaving the words "rate_limit_event" / "compilation failed"
        // in place; substring checks would pass but classification would fall
        // back to "other" and the orchestrator would hard-fail the item.
        const long resetsAt = 1782000000L;
        var stdout =
            "{\"type\":\"result\",\"subtype\":\"error\",\"is_error\":true,\"result\":\"compilation failed\"}\n"
            + "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"rejected\",\"resetsAt\":"
            + resetsAt + ",\"rateLimitType\":\"five_hour\"}}";

        var scoped = _detector.ScopeStdoutForQuotaDetection(stdout);

        Assert.NotNull(scoped);
        Assert.Contains("rate_limit_event", scoped);
        Assert.Contains("compilation failed", scoped);

        // Round-trip the scoped output through Detect (the exact next step the
        // composite classifier takes after scoping) to prove the
        // rate_limit_event line survived intact and yields the expected
        // RateLimitExceeded verdict with the parsed ResetAt — not just that
        // the marker substring is preserved.
        var scopedDetection = _detector.Detect(stderr: null, stdout: scoped);

        Assert.NotNull(scopedDetection);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, scopedDetection!.Kind);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(resetsAt), scopedDetection.ResetAt);
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

    [Fact]
    public void Detect_Claude401Stderr_NoLongerClassifiedAsQuotaEvent()
    {
        // Claude 401 is intentionally not classified as a quota failure — it
        // is dominated by the shared-OAuth refresh race and recording it as
        // Unauthorized would pin Claude as unavailable for the breaker window.
        // See ClaudeQuotaFailureDetector class summary.
        Assert.Null(_detector.Detect(stderr: "API Error: 401 unauthorized", stdout: null));
    }

    [Fact]
    public void Detect_Claude401StreamJson_NoLongerClassifiedAsQuotaEvent()
    {
        var stdout = """{"type":"result","subtype":"error","is_error":true,"result":"API Error: 401 — your token expired"}""";
        Assert.Null(_detector.Detect(stderr: null, stdout: stdout));
    }

    [Theory]
    [InlineData("API Error: 401 unauthorized", null)]
    [InlineData(null, "API Error: 401 — token expired")]
    [InlineData(null, """{"type":"result","subtype":"error","is_error":true,"result":"API Error: 401"}""")]
    [InlineData("rate_limit_exceeded but mentions API Error: 401", null)]
    public void IsUnauthorizedSignal_DetectsAcrossStreams(string? stderr, string? stdout)
    {
        Assert.True(ClaudeQuotaFailureDetector.IsUnauthorizedSignal(stderr, stdout));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("rate_limit_exceeded", null)]
    [InlineData("", "")]
    [InlineData("API Error: 500", null)]
    public void IsUnauthorizedSignal_ReturnsFalseWhenNo401(string? stderr, string? stdout)
    {
        Assert.False(ClaudeQuotaFailureDetector.IsUnauthorizedSignal(stderr, stdout));
    }
}

/// <summary>
/// Tests for <see cref="ClaudeQuotaFailureDetector.EmitAdvisoryAuditEvents"/>.
/// Mutates the static <see cref="Log.Logger"/>, so shares the
/// <c>GlobalSerilog</c> xUnit collection with other tests that depend on it.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class ClaudeQuotaFailureDetectorAdvisoryAuditTests : IDisposable
{
    private readonly TestSink _sink = new();
    private readonly ClaudeQuotaFailureDetector _detector = new();

    public ClaudeQuotaFailureDetectorAdvisoryAuditTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose() => Log.CloseAndFlush();

    [Fact]
    public void EmitAdvisoryAuditEvents_StderrHas401_EmitsClaudeUnauthorizedAuditEvent()
    {
        _detector.EmitAdvisoryAuditEvents(
            stderr: "API Error: 401 unauthorized", stdout: null,
            phase: "work", sandboxName: "vm-42");

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("agent.claude_unauthorized", ScalarString(evt, "EventName"));
        Assert.Equal(LogEventLevel.Warning, evt.Level);
        Assert.Equal("work", ScalarString(evt, "Phase"));
        Assert.Equal("vm-42", ScalarString(evt, "SandboxName"));
    }

    [Fact]
    public void EmitAdvisoryAuditEvents_Stream401_EmitsClaudeUnauthorizedAuditEvent()
    {
        _detector.EmitAdvisoryAuditEvents(
            stderr: null,
            stdout: """{"type":"result","subtype":"error","is_error":true,"result":"API Error: 401 token expired"}""",
            phase: "audit", sandboxName: null);

        var evt = Assert.Single(_sink.Events);
        Assert.Equal("agent.claude_unauthorized", ScalarString(evt, "EventName"));
        Assert.Equal("audit", ScalarString(evt, "Phase"));
        Assert.Equal("(unknown)", ScalarString(evt, "SandboxName"));
    }

    [Fact]
    public void EmitAdvisoryAuditEvents_No401Present_DoesNotEmitAnyEvent()
    {
        _detector.EmitAdvisoryAuditEvents(
            stderr: "rate_limit_exceeded reset after 1h", stdout: null,
            phase: "work", sandboxName: "vm-1");

        Assert.Empty(_sink.Events);
    }

    [Fact]
    public void EmitAdvisoryAuditEvents_EmptyInput_DoesNotEmitAnyEvent()
    {
        _detector.EmitAdvisoryAuditEvents(
            stderr: null, stdout: null, phase: "work", sandboxName: null);
        _detector.EmitAdvisoryAuditEvents(
            stderr: "", stdout: "", phase: "work", sandboxName: null);

        Assert.Empty(_sink.Events);
    }

    private static string? ScalarString(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return null;
        return sv.Value as string;
    }
}

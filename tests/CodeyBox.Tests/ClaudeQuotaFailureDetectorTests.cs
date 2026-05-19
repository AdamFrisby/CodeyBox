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

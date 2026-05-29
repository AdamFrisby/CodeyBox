using CodeyBox.Agents.Claude;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for the Claude-detector hooks that scope quota detection in long
/// multi-turn NDJSON buffers: <see cref="ClaudeQuotaFailureDetector.IsTerminalNonQuotaCrash"/>
/// and <see cref="ClaudeQuotaFailureDetector.ScopeStdoutForQuotaDetection"/>.
/// </summary>
public sealed class ClaudeQuotaStreamScopeTests
{
    private readonly ClaudeQuotaFailureDetector _detector = new();

    [Fact]
    public void IsTerminalNonQuotaCrash_Claude400ThinkingBlock_ReturnsTrue()
    {
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"api_error_status":400,"result":"messages: `thinking` blocks in the latest assistant message cannot be modified"}
            """;

        Assert.True(_detector.IsTerminalNonQuotaCrash(stderr: null, stdout));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(499)]
    public void IsTerminalNonQuotaCrash_Other4xxNot429_ReturnsTrue(int status)
    {
        var stdout = $$"""
            {"type":"result","subtype":"error","is_error":true,"api_error_status":{{status}},"result":"some upstream failure"}
            """;

        Assert.True(_detector.IsTerminalNonQuotaCrash(stderr: null, stdout));
    }

    [Fact]
    public void IsTerminalNonQuotaCrash_Claude429_ReturnsFalse()
    {
        // 429 is the only quota signal in the 4xx range — must flow through to Detect().
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"api_error_status":429,"result":"Error: rate_limit_exceeded"}
            """;

        Assert.False(_detector.IsTerminalNonQuotaCrash(stderr: null, stdout));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(529)]
    [InlineData(503)]
    public void IsTerminalNonQuotaCrash_5xx_ReturnsFalse(int status)
    {
        // 5xx is not in scope — these are transient/overloaded shapes that
        // belong to provider recovery, not the non-quota crash short-circuit.
        var stdout = $$"""
            {"type":"result","subtype":"error","is_error":true,"api_error_status":{{status}},"result":"overloaded"}
            """;

        Assert.False(_detector.IsTerminalNonQuotaCrash(stderr: null, stdout));
    }

    [Fact]
    public void IsTerminalNonQuotaCrash_StderrOnlySignature_ReturnsTrue()
    {
        // Signature surfaces in stderr (no NDJSON stdout) — e.g. when the runner
        // writes the inner error textually rather than via stream-json.
        var stderr =
            "Error: messages: content: `thinking` blocks in the latest assistant message cannot be modified.";

        Assert.True(_detector.IsTerminalNonQuotaCrash(stderr, stdout: null));
    }

    [Fact]
    public void IsTerminalNonQuotaCrash_RedactedThinkingOnly_ReturnsTrue()
    {
        // The signature has three alternative substrings; this payload exercises
        // only the `redacted_thinking` alternative.
        var stderr = "Error from API: `redacted_thinking` block was modified — request rejected.";

        Assert.True(_detector.IsTerminalNonQuotaCrash(stderr, stdout: null));
    }

    [Fact]
    public void IsTerminalNonQuotaCrash_MessageSignatureWithoutApiStatus_ReturnsTrue()
    {
        // Result has no api_error_status — the signature in `result` must still
        // trigger the crash short-circuit via the message fallback branch.
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"result":"messages: `thinking` blocks in the latest assistant message cannot be modified"}
            """;

        Assert.True(_detector.IsTerminalNonQuotaCrash(stderr: null, stdout));
    }

    [Fact]
    public void IsTerminalNonQuotaCrash_PlainStderr_NoMatch_ReturnsFalse()
    {
        Assert.False(_detector.IsTerminalNonQuotaCrash(stderr: "some unrelated error", stdout: null));
    }

    [Fact]
    public void IsTerminalNonQuotaCrash_EmptyInputs_ReturnsFalse()
    {
        Assert.False(_detector.IsTerminalNonQuotaCrash(stderr: null, stdout: null));
        Assert.False(_detector.IsTerminalNonQuotaCrash(stderr: "", stdout: ""));
    }

    [Fact]
    public void ScopeStdoutForQuotaDetection_UsesTerminalLineOnly()
    {
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"result":"Error: rate_limit_exceeded earlier"}
            {"type":"result","subtype":"error","is_error":true,"api_error_status":400,"result":"messages: `thinking` blocks in the latest assistant message cannot be modified"}
            """;

        var scoped = _detector.ScopeStdoutForQuotaDetection(stdout);

        Assert.NotNull(scoped);
        Assert.Contains("api_error_status\":400", scoped);
        Assert.DoesNotContain("rate_limit_exceeded earlier", scoped);
    }

    [Fact]
    public void ScopeStdoutForQuotaDetection_NonNdjsonText_ReturnsInputUnchanged()
    {
        // Plain stderr-style payload (no leading `{`) must pass through intact
        // so substring-only detectors still see the full buffer.
        var stdout = "API Error: rate_limit_exceeded — please retry after 30m";

        Assert.Same(stdout, _detector.ScopeStdoutForQuotaDetection(stdout));
    }

    [Fact]
    public void ScopeStdoutForQuotaDetection_NdjsonWithoutTerminalError_ReturnsInputUnchanged()
    {
        // NDJSON but no `type:"result"` error line — return the original buffer
        // so downstream substring detection has the full context.
        var stdout = """
            {"type":"system","subtype":"init","session_id":"s1"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"}]}}
            """;

        Assert.Same(stdout, _detector.ScopeStdoutForQuotaDetection(stdout));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  \t")]
    public void ScopeStdoutForQuotaDetection_EmptyOrWhitespace_ReturnsInputAsIs(string? stdout)
    {
        // Whitespace-only payloads must round-trip — narrowing them to null
        // would change orchestrator logging shape for empty crashes.
        Assert.Equal(stdout, _detector.ScopeStdoutForQuotaDetection(stdout));
    }
}

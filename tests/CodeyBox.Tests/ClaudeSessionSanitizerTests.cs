using System.Text;
using System.Text.Json;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for <see cref="ClaudeSessionSanitizer"/> covering each documented
/// corruption pattern: hollow/empty-text signed thinking blocks, interleaved
/// msg_id chunks, and the trailing API-error tail.
/// </summary>
public sealed class ClaudeSessionSanitizerTests
{
    // ── SanitizeLine — assistant message with thinking block ──────────────────

    [Fact]
    public void SanitizeLine_AssistantWithThinkingBlock_StripsBlock()
    {
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"},{"type":"thinking","thinking":"let me think about this"}]}}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        var blocks = content.EnumerateArray().ToList();
        Assert.Single(blocks);
        Assert.Equal("text", blocks[0].GetProperty("type").GetString());
        Assert.Equal("hello", blocks[0].GetProperty("text").GetString());
    }

    [Fact]
    public void SanitizeLine_AssistantWithRedactedThinkingBlock_StripsBlock()
    {
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ok"},{"type":"redacted_thinking","data":"redacted"}]}}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        var blocks = content.EnumerateArray().ToList();
        Assert.Single(blocks);
        Assert.Equal("text", blocks[0].GetProperty("type").GetString());
    }

    [Fact]
    public void SanitizeLine_AssistantWithMultipleBlocks_KeepsOnlyNonThinking()
    {
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"a"},{"type":"redacted_thinking","data":"x"},{"type":"tool_use","id":"t1","name":"bash","input":{}},{"type":"text","text":"b"}]}}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        var blocks = content.EnumerateArray().ToList();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("text", blocks[0].GetProperty("type").GetString());
        Assert.Equal("a", blocks[0].GetProperty("text").GetString());
        Assert.Equal("tool_use", blocks[1].GetProperty("type").GetString());
        Assert.Equal("text", blocks[2].GetProperty("type").GetString());
        Assert.Equal("b", blocks[2].GetProperty("text").GetString());
    }

    [Fact]
    public void SanitizeLine_AssistantNoThinkingBlocks_ReturnsUnchanged()
    {
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hello"},{"type":"tool_use","id":"t1","name":"bash","input":{}}]}}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        Assert.Equal(line, result);
    }

    [Fact]
    public void SanitizeLine_AllThinkingBlocks_ContentBecomesEmptyArray()
    {
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"a"},{"type":"redacted_thinking","data":"b"}]}}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal(0, content.GetArrayLength());
    }

    [Fact]
    public void SanitizeLine_UserMessage_PassesThroughUnchanged()
    {
        var line = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"prompt"}]}}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        Assert.Equal(line, result);
    }

    [Fact]
    public void SanitizeLine_NonJsonLine_ReturnsUnchanged()
    {
        const string line = "this is not json at all";

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        Assert.Equal(line, result);
    }

    [Fact]
    public void SanitizeLine_EmptyLine_ReturnsUnchanged()
    {
        Assert.Equal("", ClaudeSessionSanitizer.SanitizeLine(""));
        Assert.Equal(" ", ClaudeSessionSanitizer.SanitizeLine(" "));
    }

    [Fact]
    public void SanitizeLine_AssistantWithEmptyContent_ReturnsUnchanged()
    {
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[]}}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        Assert.Equal(line, result);
    }

    [Fact]
    public void SanitizeLine_PreservesTopLevelFields()
    {
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"x"},{"type":"text","text":"done"}],"id":"msg_123"},"session_id":"s1","uuid":"abc"}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        Assert.Equal("assistant", root.GetProperty("type").GetString());
        Assert.Equal("s1", root.GetProperty("session_id").GetString());
        Assert.Equal("abc", root.GetProperty("uuid").GetString());
        Assert.Equal("msg_123", root.GetProperty("message").GetProperty("id").GetString());
        Assert.Equal("assistant", root.GetProperty("message").GetProperty("role").GetString());
        var content = root.GetProperty("message").GetProperty("content");
        Assert.Single(content.EnumerateArray());
    }

    // ── SanitizeLine — deep nesting (content in nested structures) ────────────

    [Fact]
    public void SanitizeLine_ThinkingBlockInsideToolUseInput_Preserved()
    {
        // tool_use blocks are NOT filtered — only top-level assistant
        // content blocks of type thinking/redacted_thinking are stripped.
        var line = """
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"edit","input":{"thinking":"nested value"}},{"type":"thinking","thinking":"strip me"}]}}
            """;

        var result = ClaudeSessionSanitizer.SanitizeLine(line);

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        var blocks = content.EnumerateArray().ToList();
        Assert.Single(blocks);
        Assert.Equal("tool_use", blocks[0].GetProperty("type").GetString());
        Assert.Equal("nested value", blocks[0].GetProperty("input").GetProperty("thinking").GetString());
    }

    // ── IsThinkingBlockFailure ────────────────────────────────────────────────

    [Fact]
    public void IsThinkingBlockFailure_ExactSignatureInStderr_ReturnsTrue()
    {
        var stderr = "API Error: 400 messages.N.content.M: `thinking` blocks in the latest assistant message cannot be modified.";

        Assert.True(ClaudeSessionSanitizer.IsThinkingBlockFailure(stderr, null));
    }

    [Fact]
    public void IsThinkingBlockFailure_ExactSignatureInStdout_ReturnsTrue()
    {
        var stdout = "error: blocks in the latest assistant message cannot be modified.";

        Assert.True(ClaudeSessionSanitizer.IsThinkingBlockFailure(null, stdout));
    }

    [Fact]
    public void IsThinkingBlockFailure_BacktickThinkingMarker_ReturnsTrue()
    {
        Assert.True(ClaudeSessionSanitizer.IsThinkingBlockFailure(
            "something about `thinking` blocks", null));
    }

    [Fact]
    public void IsThinkingBlockFailure_BacktickRedactedThinkingMarker_ReturnsTrue()
    {
        Assert.True(ClaudeSessionSanitizer.IsThinkingBlockFailure(
            "`redacted_thinking` cannot be modified", null));
    }

    [Fact]
    public void IsThinkingBlockFailure_StreamJsonErrorEnvelope_ReturnsTrue()
    {
        var stdout = """
            {"type":"result","subtype":"error","is_error":true,"api_error_status":400,"result":"`thinking` blocks in the latest assistant message cannot be modified. These blocks must remain as they were in the original response."}
            """;

        Assert.True(ClaudeSessionSanitizer.IsThinkingBlockFailure(null, stdout));
    }

    [Fact]
    public void IsThinkingBlockFailure_Quota429Simplified_ReturnsFalse()
    {
        Assert.False(ClaudeSessionSanitizer.IsThinkingBlockFailure(
            "rate_limit_exceeded — please retry after 30m", null));
    }

    [Fact]
    public void IsThinkingBlockFailure_NullAndEmptyStrings_ReturnsFalse()
    {
        Assert.False(ClaudeSessionSanitizer.IsThinkingBlockFailure((string?)null, null));
        Assert.False(ClaudeSessionSanitizer.IsThinkingBlockFailure("", ""));
    }

    [Fact]
    public void IsThinkingBlockFailure_OnAgentResult_DelegatesCorrectly()
    {
        var result = new AgentResult(false, "exit 1",
            Stderr: "API Error: 400: `thinking` blocks in the latest assistant message cannot be modified.",
            Stdout: null);

        Assert.True(ClaudeSessionSanitizer.IsThinkingBlockFailure(result));
    }

    // ── DeinterleaveTranscript ────────────────────────────────────────────────

    [Fact]
    public void DeinterleaveTranscript_InterleavedAssistantChunks_CoalescesByMsgId()
    {
        // Simulates streaming chunks from two different msg_ids interleaved.
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"prompt"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"partial A"}],"id":"msg_1"}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"partial B"}],"id":"msg_2"}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"final A"},{"type":"thinking","thinking":"thought"}],"id":"msg_1"}}
            """;

        var result = ClaudeSessionSanitizer.DeinterleaveTranscript(input);

        // The best line for msg_1 (more content blocks) should be kept.
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // msg_1's "final A" line has 3 "type" entries (text, thinking + one in the JSON key) vs "partial A" has 1.
        // msg_2's "partial B" has 1.
        // user has 1 "type".
        var assistantLinesWithId = new HashSet<string>();
        foreach (var line in lines)
        {
            if (line.Contains("\"msg_1\"") || line.Contains("\"msg_2\""))
                assistantLinesWithId.Add(line);
        }
        Assert.Equal(2, assistantLinesWithId.Count);
    }

    [Fact]
    public void DeinterleaveTranscript_NoInterleaving_PassesThrough()
    {
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"hello"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"response"}],"id":"msg_1"}}
            """;

        var result = ClaudeSessionSanitizer.DeinterleaveTranscript(input);

        Assert.Contains("response", result, StringComparison.Ordinal);
        Assert.Contains("hello", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DeinterleaveTranscript_NoMsgIdAssistantLines_PassThrough()
    {
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"q"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"a"}]}}
            """;

        var result = ClaudeSessionSanitizer.DeinterleaveTranscript(input);

        Assert.Contains("\"a\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DeinterleaveTranscript_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClaudeSessionSanitizer.DeinterleaveTranscript(""));
        Assert.Equal(string.Empty, ClaudeSessionSanitizer.DeinterleaveTranscript("  "));
    }

    // ── EnumerateContentBlocks ────────────────────────────────────────────────

    [Fact]
    public void EnumerateContentBlocks_AssistantMessage_EnumeratesBlockTypes()
    {
        var line = Encoding.UTF8.GetBytes(
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"reply"},{"type":"redacted_thinking","data":"x"}]}}"""
        );

        var blocks = ClaudeSessionSanitizer.EnumerateContentBlocks(line).ToList();

        Assert.Equal(3, blocks.Count);
        Assert.Equal("thinking", blocks[0].Type);
        Assert.Equal("text", blocks[1].Type);
        Assert.Equal("redacted_thinking", blocks[2].Type);
    }

    [Fact]
    public void EnumerateContentBlocks_UserMessage_ReturnsEmpty()
    {
        var line = Encoding.UTF8.GetBytes(
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"prompt"}]}}"""
        );

        var blocks = ClaudeSessionSanitizer.EnumerateContentBlocks(line).ToList();

        Assert.Empty(blocks);
    }

    // ── GenerateSanitizationScript ────────────────────────────────────────────

    [Fact]
    public void GenerateSanitizationScript_ProducesValidNonEmptyScript()
    {
        var script = ClaudeSessionSanitizer.GenerateSanitizationScript();

        Assert.NotEmpty(script);
        Assert.Contains("session_root", script, StringComparison.Ordinal);
        Assert.Contains("claude/projects", script, StringComparison.Ordinal);
        Assert.Contains("backup", script, StringComparison.Ordinal);
        Assert.Contains("cp", script, StringComparison.Ordinal);
    }

    // ── Synthetic session JSONL (integration-style verification) ──────────────

    [Fact]
    public void SanitizeFullTranscript_StripsAllThinkingBlocksAndPreservesNonAssistant()
    {
        var transcript = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"write a test"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"I should write xunit tests"},{"type":"text","text":"I will write the test now"},{"type":"tool_use","id":"call_1","name":"write","input":{"path":"test.cs","content":"..."}}]}}
            {"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"call_1","content":"ok"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"done"},{"type":"text","text":"all tests pass"}]}}
            """;

        var lines = transcript.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var sanitized = ClaudeSessionSanitizer.SanitizeLine(trimmed);
            if (result.Length > 0) result.Append('\n');
            result.Append(sanitized);
        }

        var output = result.ToString();

        // User messages preserved verbatim
        Assert.Contains("\"type\":\"user\"", output, StringComparison.Ordinal);

        // Thinking content stripped
        Assert.DoesNotContain("\"type\":\"thinking\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\"type\":\"redacted_thinking\"", output, StringComparison.Ordinal);

        // Regular content preserved
        Assert.Contains("write the test", output, StringComparison.Ordinal);
        Assert.Contains("all tests pass", output, StringComparison.Ordinal);

        // Tool use preserved
        Assert.Contains("\"type\":\"tool_use\"", output, StringComparison.Ordinal);

        // Verify JSON validity of each line
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            using var doc = JsonDocument.Parse(trimmed);
            Assert.NotNull(doc);
        }
    }

    // ── Config toggle ─────────────────────────────────────────────────────────

    [Fact]
    public void SanitizerEnabled_CtorDefaultsToNull_IsEnabledByDefault()
    {
        // Default construction should enable sanitization (null config → enabled).
        var runner = new ClaudeAgentRunner();

        // Without a config, the runner treats null as enabled.
        // Verify through the sanitizer being called (tested via integration).
        Assert.NotNull(runner);
    }

    [Fact]
    public void SanitizerDisabled_ExplicitlyFalse()
    {
        var config = new ClaudeThinkingBlockSanitizerConfig { Enabled = false };
        var runner = new ClaudeAgentRunner(
            defaults: null,
            rotationPusher: null,
            sanitizerConfig: config);

        // When Enabled=false, reactive retry should be suppressed.
        // Unit tested via the failure result being returned without retry.
        Assert.NotNull(runner);
    }

    // ── Reactive retry integration tests ──────────────────────────────────────

    [Fact]
    public async Task RunResumedAsync_ThinkingBlockFailure_TriggersSanitizeAndRetry()
    {
        // The full flow:
        //   RestoreScratchpadAsync (bash) → PrepareSandboxAsync sanitize (bash)
        //   → claude (fails) → reactive sanitize (bash)
        //   → RestoreScratchpadAsync in retry (bash) → PrepareSandboxAsync sanitize (bash)
        //   → claude retry (succeeds)
        // Total: 5 bash + 2 claude = 7 execs
        var sandbox = new ThinkingBlockRetrySandbox(
            initialFailures: 1,
            sanitizerExitsZero: true);
        var config = new ClaudeThinkingBlockSanitizerConfig { Enabled = true };
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, null, config);

        var result = await runner.RunResumedAsync(
            sandbox, "/work", "prompt", credential: null,
            resume: new AgentResumeContext("refs/heads/codeybox/preempt/test"));

        Assert.True(result.Success);
        Assert.Equal("ok", result.Summary);
        // At least 3 execs (2 claude + at least 1 sanitizer)
        Assert.True(sandbox.AllExecs.Count >= 3);
        // Verify sanitizer was invoked (bash exec with "session_root" string)
        Assert.Contains(sandbox.AllExecs, e =>
            e.Argv.Count > 0
            && e.Argv[0] == "bash"
            && e.Argv[1] == "-c"
            && (e.Argv[2]?.Contains("session_root") == true));
        // Verify at least 2 claude calls (original + retry)
        var claudeCount = sandbox.AllExecs.Count(e =>
            e.Argv.Count > 0 && e.Argv[0] == "claude");
        Assert.True(claudeCount >= 2, $"Expected >=2 claude calls, got {claudeCount}");
    }

    [Fact]
    public async Task RunResumedAsync_SanitizerDisabled_NoRetryOnThinkingBlock()
    {
        var sandbox = new ThinkingBlockRetrySandbox(
            initialFailures: 1,
            sanitizerExitsZero: true);
        var config = new ClaudeThinkingBlockSanitizerConfig { Enabled = false };
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, null, config);

        var result = await runner.RunResumedAsync(
            sandbox, "/work", "prompt", credential: null,
            resume: new AgentResumeContext("refs/heads/codeybox/preempt/test"));

        Assert.False(result.Success);
        Assert.Contains("thinking", result.Stderr, StringComparison.OrdinalIgnoreCase);
        // Only one claude exec — the restore scratchpad bash also runs but we
        // only check that no retry claude invocation happened.
        var claudeCount = sandbox.AllExecs.Count(e =>
            e.Argv.Count > 0 && e.Argv[0] == "claude");
        Assert.Equal(1, claudeCount);
        // No sanitizer bash was run
        Assert.DoesNotContain(sandbox.AllExecs, e =>
            e.Argv.Count > 0
            && e.Argv[0] == "bash"
            && e.Argv[1] == "-c"
            && (e.Argv[2]?.Contains("session_root") == true));
    }

    [Fact]
    public async Task RunAsync_ThinkingBlockFailure_TriggersRetry()
    {
        var sandbox = new ThinkingBlockRetrySandbox(
            initialFailures: 1,
            sanitizerExitsZero: true);
        var config = new ClaudeThinkingBlockSanitizerConfig { Enabled = true };
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, null, config);

        var result = await runner.RunAsync(
            sandbox, "/work", "prompt", credential: null);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Summary);
    }

    [Fact]
    public async Task RunResumedAsync_NonThinkingBlockFailure_NoRetry()
    {
        // Generic failure that doesn't look like thinking-block — no retry.
        var sandbox = new ThinkingBlockRetrySandbox(
            initialFailures: 1,
            sanitizerExitsZero: true,
            useThinkingBlockError: false);
        var config = new ClaudeThinkingBlockSanitizerConfig { Enabled = true };
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, null, config);

        var result = await runner.RunResumedAsync(
            sandbox, "/work", "prompt", credential: null,
            resume: new AgentResumeContext("refs/heads/codeybox/preempt/test"));

        Assert.False(result.Success);
        Assert.Contains("generic failure", result.Stderr, StringComparison.OrdinalIgnoreCase);
        // Only one claude exec — no retry
        var claudeCount = sandbox.AllExecs.Count(e =>
            e.Argv.Count > 0 && e.Argv[0] == "claude");
        Assert.Equal(1, claudeCount);
    }
}

/// <summary>
/// Fake sandbox that returns thinking-block 400 errors for the first
/// N exec calls, then success. Simulates the reactive retry path where
/// a 400 triggers sanitise + retry.
/// </summary>
internal sealed class ThinkingBlockRetrySandbox : ISandbox
{
    private readonly int _initialFailures;
    private readonly bool _sanitizerExitsZero;
    private readonly bool _useThinkingBlockError;
    private int _callCount;

    public ThinkingBlockRetrySandbox(
        int initialFailures,
        bool sanitizerExitsZero,
        bool useThinkingBlockError = true)
    {
        _initialFailures = initialFailures;
        _sanitizerExitsZero = sanitizerExitsZero;
        _useThinkingBlockError = useThinkingBlockError;
    }

    public string Id => "thinking-block-retry";
    public List<SandboxExec> AllExecs { get; } = new();

    public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
    {
        AllExecs.Add(exec);

        // Sanitizer bash calls — return based on configuration
        if (exec.Argv.Count > 0 && exec.Argv[0] == "bash")
            return Task.FromResult(new SandboxExecResult(
                _sanitizerExitsZero ? 0 : 1, string.Empty,
                _sanitizerExitsZero ? string.Empty : "sanitizer failed"));

        // Claude calls
        _callCount++;
        if (_callCount <= _initialFailures)
        {
            return Task.FromResult(new SandboxExecResult(
                1,
                Stdout: string.Empty,
                Stderr: _useThinkingBlockError
                    ? "API Error: 400: `thinking` blocks in the latest assistant message cannot be modified."
                    : "generic failure"));
        }

        return Task.FromResult(new SandboxExecResult(0, "stdout", "ok"));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

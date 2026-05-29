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
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"prompt"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"partial A"}],"id":"msg_1"}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"partial B"}],"id":"msg_2"}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"final A"},{"type":"thinking","thinking":"thought"}],"id":"msg_1"}}
            """;

        var result = ClaudeSessionSanitizer.DeinterleaveTranscript(input);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);

        // Line 0 must be user message
        Assert.True(lines[0].Contains("\"user\"", StringComparison.Ordinal));
        // Line 1 must be msg_1 (the best/coalesced line for msg_1 — "final A" has more content blocks)
        Assert.True(lines[1].Contains("\"msg_1\"", StringComparison.Ordinal));
        Assert.True(lines[1].Contains("\"final A\"", StringComparison.Ordinal));
        // Line 2 must be msg_2 remaining at its original position
        Assert.True(lines[2].Contains("\"msg_2\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DeinterleaveTranscript_NoInterleaving_PassesThrough()
    {
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"hello"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"response"}],"id":"msg_1"}}
            """;

        var result = ClaudeSessionSanitizer.DeinterleaveTranscript(input);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("response", result, StringComparison.Ordinal);
        Assert.Contains("hello", result, StringComparison.Ordinal);
        // Verify ordering: user first, assistant second
        Assert.True(lines[0].Contains("\"user\"", StringComparison.Ordinal));
        Assert.True(lines[1].Contains("\"msg_1\"", StringComparison.Ordinal));
    }

    [Fact]
    public void DeinterleaveTranscript_NoMsgIdAssistantLines_PassThrough()
    {
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"q"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"a"}]}}
            """;

        var result = ClaudeSessionSanitizer.DeinterleaveTranscript(input);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"a\"", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DeinterleaveTranscript_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClaudeSessionSanitizer.DeinterleaveTranscript(""));
        Assert.Equal(string.Empty, ClaudeSessionSanitizer.DeinterleaveTranscript("  "));
    }

    [Fact]
    public void DeinterleaveTranscript_PreservesMessageOrdering()
    {
        // Verify that after de-interleaving, messages appear in their original
        // chronological order (not all assistant lines first).
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"first"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"part1"}],"id":"msg_a"}}
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"second"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"part2"}],"id":"msg_a"}}
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"third"}]}}
            """;

        var result = ClaudeSessionSanitizer.DeinterleaveTranscript(input);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        // Order: user("first"), coalesced msg_a ("part2" has more content), user("second"), user("third")
        Assert.True(lines[0].Contains("\"first\"", StringComparison.Ordinal));
        Assert.True(lines[1].Contains("\"msg_a\"", StringComparison.Ordinal));
        Assert.True(lines[2].Contains("\"second\"", StringComparison.Ordinal));
        Assert.True(lines[3].Contains("\"third\"", StringComparison.Ordinal));
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

    // ── BuildFileListAndBackupScript ──────────────────────────────────────────

    [Fact]
    public void BuildFileListAndBackupScript_ProducesValidNonEmptyScript()
    {
        var script = ClaudeSessionSanitizer.BuildFileListAndBackupScript();

        Assert.NotEmpty(script);
        Assert.Contains("session_root", script, StringComparison.Ordinal);
        Assert.Contains("claude/projects", script, StringComparison.Ordinal);
        Assert.Contains("backup", script, StringComparison.Ordinal);
        Assert.Contains("cp", script, StringComparison.Ordinal);
        Assert.Contains("cp -P", script, StringComparison.Ordinal);
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

        var output = ClaudeSessionSanitizer.SanitizeFullTranscript(transcript);

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

    // ── Trailing API-error tail ───────────────────────────────────────────────

    [Fact]
    public void StripTrailingApiErrorTail_RemovesTrailingNonConversationLine()
    {
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"hello"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}
            {"type":"result","subtype":"error","is_error":true,"result":"something went wrong"}
            """;

        var result = ClaudeSessionSanitizer.StripTrailingApiErrorTail(input);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"type\":\"user\"", result, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"assistant\"", result, StringComparison.Ordinal);
        Assert.DoesNotContain("went wrong", result, StringComparison.Ordinal);
    }

    [Fact]
    public void StripTrailingApiErrorTail_KeepsRecognisedLinesAfterError()
    {
        // An error line that appears BEFORE the last recognised turn should be kept.
        // (It will be treated as a non-conversation line, but since it's not
        // strictly trailing it passes through.)
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"hello"}]}}
            {"type":"result","subtype":"error","is_error":true,"result":"warning"}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}
            """;

        var result = ClaudeSessionSanitizer.StripTrailingApiErrorTail(input);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
    }

    [Fact]
    public void StripTrailingApiErrorTail_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ClaudeSessionSanitizer.StripTrailingApiErrorTail(""));
        Assert.Equal(string.Empty, ClaudeSessionSanitizer.StripTrailingApiErrorTail("  "));
    }

    [Fact]
    public void StripTrailingApiErrorTail_AllRecognisedLines_PassesThrough()
    {
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"q"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"a"}]}}
            """;

        var result = ClaudeSessionSanitizer.StripTrailingApiErrorTail(input);
        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    // ── SanitizeFullTranscript integration ────────────────────────────────────

    [Fact]
    public void SanitizeFullTranscript_DeinterleavesAndStripsTrailingTail()
    {
        var input = """
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"prompt"}]}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"partial"}],"id":"msg_1"}}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"thought"},{"type":"text","text":"complete"}],"id":"msg_1"}}
            {"type":"result","subtype":"error","api_error_status":400,"result":"thinking blocks cannot be modified"}
            """;

        var result = ClaudeSessionSanitizer.SanitizeFullTranscript(input);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        // De-interleaving: only one assistant line for msg_1, picking the most complete
        Assert.DoesNotContain("\"partial\"", result, StringComparison.Ordinal);
        Assert.Contains("\"complete\"", result, StringComparison.Ordinal);
        // Thinking block stripped
        Assert.DoesNotContain("\"thinking\"", result, StringComparison.Ordinal);
        // Trailing error tail removed
        Assert.DoesNotContain("api_error_status", result, StringComparison.Ordinal);
    }

    // ── Config toggle ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SanitizerEnabled_CtorDefaultsToNull_IsEnabledByDefault()
    {
        // Default construction (null config) should treat sanitization as enabled.
        // Verify that the runner can be created and the config gating logic
        // treats null as enabled.
        var runner = new ClaudeAgentRunner();
        Assert.NotNull(runner);

        // Prove the null-as-enabled semantic: build a sandbox that fails with
        // thinking-block 400 and verify retry fires.
        var sandbox = new ThinkingBlockRetrySandbox(
            initialFailures: 1,
            sanitizerExitsZero: true);
        // If the default-null config suppressed retry, this would fail rather than
        // retry and succeed.
        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: null);
        Assert.True(result.Success);
        var claudeCount = sandbox.AllExecs.Count(e =>
            e.Argv.Count > 0 && e.Argv[0] == "claude");
        Assert.True(claudeCount >= 2, $"Expected >=2 claude calls (retry fired), got {claudeCount}");
    }

    [Fact]
    public async Task SanitizerDisabled_ExplicitlyFalse()
    {
        var config = new ClaudeThinkingBlockSanitizerConfig { Enabled = false };
        var runner = new ClaudeAgentRunner(
            defaults: null,
            rotationPusher: null,
            sanitizerConfig: config);

        Assert.NotNull(runner);

        // When Enabled=false, reactive retry should be suppressed.
        var sandbox = new ThinkingBlockRetrySandbox(
            initialFailures: 1,
            sanitizerExitsZero: true);
        var result = await runner.RunAsync(sandbox, "/work", "prompt", credential: null);

        Assert.False(result.Success);
        var claudeCount = sandbox.AllExecs.Count(e =>
            e.Argv.Count > 0 && e.Argv[0] == "claude");
        Assert.Equal(1, claudeCount);
    }

    // ── Reactive retry integration tests ──────────────────────────────────────

    [Fact]
    public async Task RunResumedAsync_ThinkingBlockFailure_TriggersSanitizeAndRetry()
    {
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

        // Verify sanitizer ran before the retry claude call.
        var execs = sandbox.AllExecs;
        int sanitizerIdx = -1;
        int lastClaudeIdx = -1;
        int firstClaudeIdx = -1;
        for (var i = 0; i < execs.Count; i++)
        {
            if (execs[i].Argv.Count > 0 && execs[i].Argv[0] == "bash"
                && execs[i].Argv[1] == "-c"
                && (execs[i].Argv[2]?.Contains("cp -P") == true))
            {
                sanitizerIdx = i;
            }
            if (execs[i].Argv.Count > 0 && execs[i].Argv[0] == "claude")
            {
                if (firstClaudeIdx < 0) firstClaudeIdx = i;
                lastClaudeIdx = i;
            }
        }
        // Sanitizer must run between the two claude calls
        Assert.True(firstClaudeIdx >= 0, "First claude call not found");
        Assert.True(lastClaudeIdx > firstClaudeIdx, "Retry claude call not found after first");
        Assert.True(sanitizerIdx > firstClaudeIdx, "Sanitizer should run after first claude failure");
        Assert.True(sanitizerIdx < lastClaudeIdx, "Sanitizer should run before retry claude call");
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
        var claudeCount = sandbox.AllExecs.Count(e =>
            e.Argv.Count > 0 && e.Argv[0] == "claude");
        Assert.Equal(1, claudeCount);
        Assert.DoesNotContain(sandbox.AllExecs, e =>
            e.Argv.Count > 0
            && e.Argv[0] == "bash"
            && e.Argv[1] == "-c"
            && (e.Argv[2]?.Contains("cp -P") == true));
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

        // Verify ordering: sanitizer must run between the two claude calls
        var execs = sandbox.AllExecs;
        int sanitizerIdx = -1;
        int lastClaudeIdx = -1;
        int firstClaudeIdx = -1;
        for (var i = 0; i < execs.Count; i++)
        {
            if (execs[i].Argv.Count > 0 && execs[i].Argv[0] == "bash"
                && execs[i].Argv[1] == "-c"
                && (execs[i].Argv[2]?.Contains("cp -P") == true))
            {
                sanitizerIdx = i;
            }
            if (execs[i].Argv.Count > 0 && execs[i].Argv[0] == "claude")
            {
                if (firstClaudeIdx < 0) firstClaudeIdx = i;
                lastClaudeIdx = i;
            }
        }
        Assert.True(firstClaudeIdx >= 0, "First claude call not found");
        Assert.True(lastClaudeIdx > firstClaudeIdx, "Retry claude call not found after first");
        Assert.True(sanitizerIdx > firstClaudeIdx, "Sanitizer should run after first claude failure");
        Assert.True(sanitizerIdx < lastClaudeIdx, "Sanitizer should run before retry claude call");
    }

    [Fact]
    public async Task RunResumedAsync_NonThinkingBlockFailure_NoRetry()
    {
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
        var claudeCount = sandbox.AllExecs.Count(e =>
            e.Argv.Count > 0 && e.Argv[0] == "claude");
        Assert.Equal(1, claudeCount);
    }

    [Fact]
    public async Task RunResumedAsync_SanitizerExitsNonZero_NoRetry()
    {
        // Sanitiser itself fails — no retry should occur, original failure surfaces.
        var sandbox = new ThinkingBlockRetrySandbox(
            initialFailures: 1,
            sanitizerExitsZero: false);
        var config = new ClaudeThinkingBlockSanitizerConfig { Enabled = true };
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, null, config);

        var result = await runner.RunResumedAsync(
            sandbox, "/work", "prompt", credential: null,
            resume: new AgentResumeContext("refs/heads/codeybox/preempt/test"));

        Assert.False(result.Success);
        var claudeCount = sandbox.AllExecs.Count(e =>
            e.Argv.Count > 0 && e.Argv[0] == "claude");
        Assert.Equal(1, claudeCount);
    }

    [Fact]
    public async Task RunAsync_SanitizerExitsNonZero_NoRetry()
    {
        var sandbox = new ThinkingBlockRetrySandbox(
            initialFailures: 1,
            sanitizerExitsZero: false);
        var config = new ClaudeThinkingBlockSanitizerConfig { Enabled = true };
        var defaults = new AgentDefaultsSnapshot(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
        var runner = new ClaudeAgentRunner(defaults, null, config);

        var result = await runner.RunAsync(
            sandbox, "/work", "prompt", credential: null);

        Assert.False(result.Success);
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
        {
            // Detect the sanitizer's backup script by looking for the "cp -P" fingerprint
            if (exec.Argv.Count >= 3 && exec.Argv[1] == "-c"
                && (exec.Argv[2]?.Contains("cp -P") == true))
            {
                return Task.FromResult(new SandboxExecResult(
                    _sanitizerExitsZero ? 0 : 1, string.Empty,
                    _sanitizerExitsZero ? string.Empty : "sanitizer failed"));
            }
            // Other bash calls (scratchpad restore, cat, etc.) succeed
            return Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));
        }

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

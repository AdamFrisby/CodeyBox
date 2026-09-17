using System.Text.Json;
using CodeyBox.Agents.Qwen;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="QwenStreamParser"/>: qwen's Claude-shaped wire
/// vocabulary with qwen-only claim markers, and non-interference with the
/// other registered parsers' claims (notably Claude's bare
/// <c>assistant</c>/<c>result</c> types).
/// </summary>
public sealed class QwenStreamParserTests
{
    private static Stream StreamOf(string text)
    {
        var stream = new MemoryStream();
        var writer = new StreamWriter(stream);
        writer.Write(text);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void TryClaim_SystemInitWithVersion_Claimed()
    {
        // Recorded live shape (qwen 0.24.0 system/init, redacted).
        using var doc = JsonDocument.Parse(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"cwd\":\"/work\"," +
            "\"model\":\"m\",\"permission_mode\":\"yolo\",\"qwen_code_version\":\"0.24.0\"," +
            "\"slash_commands\":[\"auth\"],\"agents\":[\"general-purpose\"]}");

        Assert.True(new QwenStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_SystemWithoutQwenMarkers_NotClaimed()
    {
        // A foreign {"type":"system"} line (e.g. Claude's init frame, which
        // carries permissionMode camel-case and no qwen_code_version) must
        // never be misattributed.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"permissionMode\":\"default\"}");

        Assert.False(new QwenStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_AssistantWithTotalTokens_Claimed()
    {
        // Recorded live: assistant text frame with usage incl. total_tokens.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"assistant\",\"session_id\":\"s\",\"parent_tool_use_id\":null," +
            "\"message\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]," +
            "\"usage\":{\"input_tokens\":24157,\"output_tokens\":58,\"cache_read_input_tokens\":0,\"total_tokens\":24215}}}");

        Assert.True(new QwenStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_BareAssistant_NotClaimed()
    {
        // Claude's parser owns bare assistant frames; qwen must not steal
        // Claude streams. Claude usage never carries total_tokens.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"assistant\",\"session_id\":\"s\",\"parent_tool_use_id\":null," +
            "\"message\":{\"role\":\"assistant\",\"model\":\"claude-x\"," +
            "\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}");

        Assert.False(new QwenStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ResultWithStats_Claimed()
    {
        // Recorded live: success result frame with stats.models breakdown.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s\",\"is_error\":false," +
            "\"duration_ms\":115311,\"duration_api_ms\":70752,\"num_turns\":1,\"result\":\"ok\"," +
            "\"usage\":{\"input_tokens\":33823,\"output_tokens\":795,\"cache_read_input_tokens\":0,\"total_tokens\":34618}," +
            "\"stats\":{\"models\":{\"m\":{\"tokens\":{\"prompt\":33823,\"candidates\":795,\"total\":34618,\"cached\":0}}}}}");

        Assert.True(new QwenStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ErrorDuringExecutionResult_Claimed()
    {
        // Recorded live: failure result frame (no stats — usage-only run).
        using var doc = JsonDocument.Parse(
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"session_id\":\"s\"," +
            "\"is_error\":true,\"error\":{\"message\":\"[API Error: 401 Missing Authentication header]\"}}");

        Assert.True(new QwenStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_BareSuccessResult_NotClaimed()
    {
        // duration_ms/num_turns/is_error are shared with Claude — they are
        // deliberately NOT discriminators.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s\",\"is_error\":false," +
            "\"duration_ms\":100,\"num_turns\":1,\"result\":\"done\"}");

        Assert.False(new QwenStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_StreamEvent_Claimed()
    {
        // Recorded live: qwen's partial/goal envelope; no other registered
        // CLI emits this type.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"stream_event\",\"session_id\":\"s\",\"parent_tool_use_id\":null," +
            "\"event\":{\"type\":\"goal_state\",\"goal_state\":{\"v\":2,\"goal\":null,\"activity\":\"idle\"}}}");

        Assert.True(new QwenStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ForeignVocabulary_NotClaimed()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"turn.started\"}");
        Assert.False(new QwenStreamParser().TryClaim(doc.RootElement));

        using var doc2 = JsonDocument.Parse("{\"type\":\"session\",\"version\":3}");
        Assert.False(new QwenStreamParser().TryClaim(doc2.RootElement));
    }

    [Fact]
    public async Task ParseAsync_MapsQwenUsageNames()
    {
        // Recorded live shapes, redacted ids.
        var parser = new QwenStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"cwd\":\"/work\",\"permission_mode\":\"yolo\",\"qwen_code_version\":\"0.24.0\"}\n" +
            "{\"type\":\"assistant\",\"session_id\":\"s\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"usage\":{\"input_tokens\":24157,\"output_tokens\":58,\"cache_read_input_tokens\":12,\"total_tokens\":24227}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"s\",\"is_error\":false,\"duration_ms\":115311,\"num_turns\":1,\"result\":\"ok\",\"usage\":{\"input_tokens\":33823,\"output_tokens\":795,\"cache_read_input_tokens\":12,\"total_tokens\":34630}}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(33823, summary.InputTokens);
        Assert.Equal(795, summary.OutputTokens);
        Assert.Equal(12, summary.CachedInputTokens);
        Assert.Equal("ok", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_FailureFrame_YieldsNoUsage()
    {
        // Recorded live: all-zero usage on an error run.
        var parser = new QwenStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s\",\"qwen_code_version\":\"0.24.0\"}\n" +
            "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"session_id\":\"s\",\"is_error\":true,\"usage\":{\"input_tokens\":0,\"output_tokens\":0,\"cache_read_input_tokens\":0},\"error\":{\"message\":\"[API Error: 401 Missing Authentication header]\"}}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_ToolUseAndToolResult_Parsed()
    {
        // Recorded live shapes from a multi-turn repo-edit run (qwen
        // 0.24.0): tool_use parts ride in assistant message.content, and
        // tool_result parts ride in user message.content with matching
        // call ids.
        var parser = new QwenStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"assistant\",\"session_id\":\"s\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"assistant\",\"model\":\"m\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call-1\",\"name\":\"read_file\",\"input\":{\"file_path\":\"/work/calc.py\"}}],\"usage\":{\"input_tokens\":100,\"output_tokens\":10,\"cache_read_input_tokens\":0,\"total_tokens\":110}}}\n" +
            "{\"type\":\"user\",\"session_id\":\"s\",\"parent_tool_use_id\":null,\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"call-1\",\"is_error\":false,\"content\":\"def add(a, b):\\n    return a - b\"}]}}\n");

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("read_file", tool.ToolName);
        Assert.Equal("call-1", tool.ToolUseId);
        Assert.False(tool.Succeeded == false);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_YieldsEmptySummary()
    {
        var parser = new QwenStreamParser();
        await using var stream = StreamOf("");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Empty(summary.ToolCalls);
        Assert.Null(summary.FinalAssistantMessage);
    }
}

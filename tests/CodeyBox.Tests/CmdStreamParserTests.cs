using System.Text.Json;
using CodeyBox.Agents.Cmd;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="CmdStreamParser"/>: the claim discipline over the
/// cmd envelope (always claimed) versus the shared result line (claimed
/// only with the cmd markers — a bare Claude result must never be stolen),
/// the <c>CanEmitShapeOf</c> matrix, and usage/text/tool mapping over
/// recorded real command-code 1.54.2 output — including the all-zero usage
/// failure, the empty response, and an exit-8 max-turns run.
/// </summary>
public sealed class CmdStreamParserTests
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
    public void TryClaim_RunStartEvent_Claimed()
    {
        using var doc = JsonDocument.Parse(
            "{\"type\":\"event\",\"event\":{\"type\":\"run_start\",\"sessionId\":\"s\"}}");

        Assert.True(new CmdStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ToolHookBlockedEvent_Claimed()
    {
        using var doc = JsonDocument.Parse(
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_hook_blocked\",\"toolCallId\":\"c\",\"toolName\":\"shell\",\"hookOutput\":\"x\"}}");

        Assert.True(new CmdStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_RunErrorEvent_NeverClaimed()
    {
        // The in-stream error companion is parsed for the summary but never
        // claimed on its own: misattributing another agent's error line
        // would corrupt stream-file attribution.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"event\",\"event\":{\"type\":\"run_error\",\"error\":{\"name\":\"Error\",\"message\":\"Error: 400\"}}}");

        Assert.False(new CmdStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ClaudeResultLine_NeverClaimed()
    {
        // Claude's parser claims every type:result line and is registered
        // first; the cmd result claim requires the cmd markers (subtype
        // vocabulary + usage + finalText) so a real Claude result is never
        // stolen even if registration order changes.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"session_id\":\"s\",\"result\":\"done\"}");

        Assert.False(new CmdStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_CmdResultLine_Claimed()
    {
        using var doc = JsonDocument.Parse(
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false," +
            "\"usage\":{\"inputTokens\":101,\"outputTokens\":10,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1000,\"finalText\":\"CMD_SMOKE_OK\"}");

        Assert.True(new CmdStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_PiLifecycleVerb_NeverClaimed()
    {
        // Pi owns the top-level lifecycle verbs; cmd's verbs ride the nested
        // envelope, so a pi line is never cmd's.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}");

        Assert.False(new CmdStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void CanEmitShapeOf_CmdOnly()
    {
        var parser = new CmdStreamParser();

        Assert.True(parser.CanEmitShapeOf(AgentKind.Cmd));
        Assert.False(parser.CanEmitShapeOf(AgentKind.Claude));
        Assert.False(parser.CanEmitShapeOf(AgentKind.Omp));
    }

    [Fact]
    public async Task ParseAsync_MapsCamelCaseUsageAndRunTotalWins()
    {
        // Recorded real frames (command-code 1.54.2): per-turn usage plus
        // the run total on the terminal result line — the terminal frame
        // sorts last and wins. The shared ParseUsage only knows snake_case;
        // cmd reports camelCase.
        var parser = new CmdStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"event\",\"event\":{\"type\":\"model_request_start\",\"model\":\"openrouter/nvidia/nemotron-3.5-lightning:free\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"model_request_end\",\"model\":\"openrouter/nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"inputTokens\":15353,\"outputTokens\":186,\"cacheReadTokens\":0,\"cacheWriteTokens\":0},\"stopReason\":\"tool_calls\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"message_end\",\"content\":[{\"type\":\"text\",\"text\":\"Working…\"}]}}\n" +
            "{\"type\":\"result\",\"subtype\":\"success\",\"isError\":false," +
            "\"usage\":{\"inputTokens\":46284,\"outputTokens\":282,\"cacheReadTokens\":21760,\"cacheWriteTokens\":0}," +
            "\"durationMs\":110866,\"finalText\":\"Done!\"}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(46284, summary.InputTokens);
        Assert.Equal(282, summary.OutputTokens);
        Assert.Equal(21760, summary.CachedInputTokens);
        Assert.Equal("Done!", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_ToolLifecycle_MapsStartAndResult()
    {
        // Recorded real tool frames (command-code 1.54.2, write_file call):
        // queued carries id+name+input, running is a progress marker,
        // completed carries the text result.
        var parser = new CmdStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_queued\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\"," +
            "\"input\":{\"file_path\":\"/tmp/x\",\"content\":\"hi\"}}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_running\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\",\"description\":null}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_completed\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\"," +
            "\"result\":[{\"type\":\"text\",\"text\":\"File created successfully\"}],\"deferred\":false}}\n");

        var summary = await parser.ParseAsync(stream);

        var toolCall = Assert.Single(summary.ToolCalls);
        Assert.Equal("call-1", toolCall.ToolUseId);
        Assert.Equal("write_file", toolCall.ToolName);
        Assert.True(toolCall.Succeeded);
        Assert.True(toolCall.OutputBytes > 0);
    }

    [Fact]
    public async Task ParseAsync_HookBlockedTool_MapsFailedResult()
    {
        // Recorded real no-yolo frame (command-code 1.54.2): the permission
        // gate refusing a write. Queued first (as the live stream emits),
        // then the refusal — maps to a failed result so the summary shows
        // the refusal.
        var parser = new CmdStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_queued\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\"," +
            "\"input\":{\"file_path\":\"/tmp/x\",\"content\":\"hi\"}}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"tool_hook_blocked\",\"toolCallId\":\"call-1\",\"toolName\":\"write_file\"," +
            "\"hookOutput\":\"Tool \\\"write_file\\\" requires permissions. Use --yolo to allow all tools in trusted environments.\"}}\n");

        var summary = await parser.ParseAsync(stream);

        var toolCall = Assert.Single(summary.ToolCalls);
        Assert.Equal("write_file", toolCall.ToolName);
        Assert.False(toolCall.Succeeded);
    }

    [Fact]
    public async Task ParseAsync_AllZeroUsageFailure_DoesNotCorruptSummary()
    {
        // Recorded real failure shape (paid model on $0 key, exit 4): usage
        // is all zeros, finalText is empty, the run_error companion is
        // recognised but unclaimed.
        var parser = new CmdStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"event\",\"event\":{\"type\":\"run_start\",\"sessionId\":\"s\"}}\n" +
            "{\"type\":\"event\",\"event\":{\"type\":\"run_error\",\"error\":{\"name\":\"Error\",\"message\":\"Error: 403\"}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"error\",\"isError\":true," +
            "\"usage\":{\"inputTokens\":0,\"outputTokens\":0,\"cacheReadTokens\":0,\"cacheWriteTokens\":0}," +
            "\"durationMs\":2202,\"finalText\":\"\",\"error\":\"Error: 403 Key limit exceeded (total limit).\"}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_EmptyResponse_ParsesWithoutThrowing()
    {
        var parser = new CmdStreamParser();
        await using var stream = StreamOf(string.Empty);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Empty(summary.ToolCalls);
    }

    [Fact]
    public async Task ParseAsync_MaxTurnsRun_MapsUsageDespiteEmptyFinalText()
    {
        // Recorded real exit-8 shape (command-code 1.54.2): max_turns
        // subtype, the stderr warning has no stdout frame, but usage is
        // real spend.
        var parser = new CmdStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"event\",\"event\":{\"type\":\"turn_end\",\"turnNumber\":63,\"hadToolCalls\":false," +
            "\"usage\":{\"inputTokens\":220425,\"outputTokens\":8191,\"cacheReadTokens\":4096,\"cacheWriteTokens\":0}}}\n" +
            "{\"type\":\"result\",\"subtype\":\"max_turns\",\"isError\":true," +
            "\"usage\":{\"inputTokens\":222468,\"outputTokens\":8281,\"cacheReadTokens\":4096,\"cacheWriteTokens\":0}," +
            "\"durationMs\":1684026,\"finalText\":\"\",\"error\":\"Stopped: exceeded maximum turns (100).\"}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(222468, summary.InputTokens);
        Assert.Equal(8281, summary.OutputTokens);
        Assert.Equal(4096, summary.CachedInputTokens);
    }
}

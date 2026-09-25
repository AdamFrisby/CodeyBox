using System.Text.Json;
using CodeyBox.Agents.Devin;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Pins <see cref="DevinStreamParser"/>'s claim discipline and event mapping
/// for the <c>devin.acp</c> NDJSON envelopes the dispatch shim emits:
/// envelope lines are claimed (they are CodeyBox's own shape), foreign JSON
/// is never claimed, and session_update / turn_complete envelopes map to
/// tool calls, assistant chunks, and the final message. Usage counters are
/// recognised for liveness but never folded into the summary — they are
/// agent-influenceable telemetry that must not reach
/// <c>has_extracted_token_usage</c> cost rows.
/// </summary>
public sealed class DevinStreamParserTests
{
    [Fact]
    public void Kind_IsDevin()
        => Assert.Equal(AgentKind.Devin, new DevinStreamParser().Kind);

    [Theory]
    [InlineData("""{"type":"assistant","message":"hi"}""")]
    [InlineData("""{"type":"result","result":"done"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"type":"devin.acpish","event":"session_update"}""")]
    public void TryClaim_ForeignJsonShape_NeverClaimed(string line)
    {
        using var doc = JsonDocument.Parse(line);
        Assert.False(new DevinStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_DevinAcpEnvelope_Claimed()
    {
        using var doc = JsonDocument.Parse("""{"type":"devin.acp","event":"session_started","sessionId":"s-1"}""");
        Assert.True(new DevinStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void CanEmitShapeOf_OnlyOwnKind()
    {
        var parser = new DevinStreamParser();
        Assert.True(parser.CanEmitShapeOf(AgentKind.Devin));
        Assert.False(parser.CanEmitShapeOf(AgentKind.Cursor));
        Assert.False(parser.CanEmitShapeOf(AgentKind.Claude));
    }

    [Fact]
    public async Task ParseAsync_ToolCallLifecycle_ProducesCompletedToolInvocation()
    {
        var stream = StreamOf(
            """{"type":"devin.acp","event":"session_started","sessionId":"s-1"}""",
            """{"type":"devin.acp","event":"session_update","sessionId":"s-1","update":{"sessionUpdate":"tool_call","toolCallId":"exec:0#abc","title":"Ran dotnet test","kind":"execute"}}""",
            """{"type":"devin.acp","event":"session_update","sessionId":"s-1","update":{"sessionUpdate":"tool_call_update","toolCallId":"exec:0#abc","status":"in_progress"}}""",
            """{"type":"devin.acp","event":"session_update","sessionId":"s-1","update":{"sessionUpdate":"tool_call_update","toolCallId":"exec:0#abc","status":"completed"}}""",
            """{"type":"devin.acp","event":"turn_complete","stopReason":"end_turn","usage":{"inputTokens":11,"outputTokens":7},"finalText":"DONE"}""");

        var summary = await new DevinStreamParser().ParseAsync(stream);

        Assert.False(summary.IsUnsupported);
        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("exec:0#abc", tool.ToolUseId);
        Assert.Equal("Ran dotnet test", tool.ToolName);
        Assert.True(tool.Succeeded);
        // The envelope's usage object is telemetry — never folded into the
        // summary that feeds work_item_costs accounting.
        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Equal("DONE", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_FailedToolCallUpdate_MarksToolUnsuccessful()
    {
        var stream = StreamOf(
            """{"type":"devin.acp","event":"session_update","sessionId":"s-1","update":{"sessionUpdate":"tool_call","toolCallId":"t-1","title":"Ran bash","kind":"execute"}}""",
            """{"type":"devin.acp","event":"session_update","sessionId":"s-1","update":{"sessionUpdate":"tool_call_update","toolCallId":"t-1","status":"failed"}}""",
            """{"type":"devin.acp","event":"turn_complete","stopReason":"end_turn"}""");

        var summary = await new DevinStreamParser().ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.False(tool.Succeeded);
    }

    [Fact]
    public async Task ParseAsync_UsageUpdate_IsRecognized_CountersStayTelemetry()
    {
        // A usage tick still counts as a recognised envelope (it keeps the
        // stream alive), but its counters are agent-influenceable telemetry:
        // folding them into the summary would promote forged values into
        // has_extracted_token_usage cost rows that settle quota escrow.
        var stream = StreamOf(
            """{"type":"devin.acp","event":"session_update","sessionId":"s-1","update":{"sessionUpdate":"usage_update","used":100,"size":200,"_meta":{"cognition.ai/inputTokens":50,"cognition.ai/outputTokens":9,"cognition.ai/cachedReadTokens":4}}}""",
            """{"type":"devin.acp","event":"turn_complete","stopReason":"end_turn"}""");

        var summary = await new DevinStreamParser().ParseAsync(stream);

        Assert.False(summary.IsUnsupported);
        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Equal(0, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_IsUnsupported()
    {
        var summary = await new DevinStreamParser().ParseAsync(StreamOf());
        Assert.True(summary.IsUnsupported);
    }

    private static MemoryStream StreamOf(params string[] lines)
    {
        var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, leaveOpen: true);
        foreach (var line in lines)
            writer.WriteLine(line);
        writer.Flush();
        stream.Position = 0;
        return stream;
    }
}

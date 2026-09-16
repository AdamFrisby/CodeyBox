using System.Text.Json;
using CodeyBox.Agents.Cline;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ClineStreamParser"/>: cline's <c>--json</c> claim
/// vocabulary (verified against cline 3.0.62 live frames), the tool/usage
/// mapping, and non-interference with the other registered parsers' claims.
/// Parse cases run over recorded real output, including a provider failure
/// and an empty response. The recorded quota URL's workspace key id is
/// redacted — it is secret-derived.
/// </summary>
public sealed class ClineStreamParserTests
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

    // Recorded real frames (cline 3.0.62, one-shot --json run against
    // OpenRouter nemotron free that created a file end to end).
    private const string HookStartFrame =
        """{"ts":"2026-09-16T18:01:59.593Z","type":"hook_event","hookEventName":"agent_start","agentId":"agent_x","taskId":"conv_x","parentAgentId":null}""";
    private const string IterationStartFrame =
        """{"ts":"2026-09-16T18:02:02.530Z","type":"agent_event","event":{"type":"iteration_start","iteration":1}}""";
    private const string ReasoningStartFrame =
        """{"ts":"2026-09-16T18:02:02.531Z","type":"agent_event","event":{"type":"content_start","contentType":"reasoning","reasoning":"The user wants","redacted":false}}""";
    private const string ToolStartFrame =
        """{"ts":"2026-09-16T18:02:02.535Z","type":"agent_event","event":{"type":"content_start","contentType":"tool","toolName":"editor","toolCallId":"call-2c6742a4-4f20-48cf-9e18-9d12cdc0dbb5","input":{"new_text":"HELLO-FROM-CLINE","path":"/tmp/cline-tools/hello.txt"}}}""";
    private const string UsageFrame =
        """{"ts":"2026-09-16T18:02:02.531Z","type":"agent_event","event":{"type":"usage","inputTokens":6244,"outputTokens":131,"totalInputTokens":6244,"totalOutputTokens":131,"totalCost":0}}""";
    private const string ToolEndFrame =
        """{"ts":"2026-09-16T18:02:02.541Z","type":"agent_event","event":{"type":"content_end","contentType":"tool","toolName":"editor","toolCallId":"call-2c6742a4-4f20-48cf-9e18-9d12cdc0dbb5","output":{"query":"edit:/tmp/cline-tools/hello.txt","result":"File created successfully at: /tmp/cline-tools/hello.txt","success":true},"durationMs":6}}""";
    private const string DoneFrame =
        """{"ts":"2026-09-16T18:02:09.659Z","type":"agent_event","event":{"type":"done","reason":"completed","text":"The task is complete.","iterations":3,"usage":{"inputTokens":19209,"outputTokens":294,"cacheReadTokens":8704,"totalCost":0}}}""";
    private const string RunResultFrame =
        """{"ts":"2026-09-16T18:02:09.691Z","type":"run_result","finishReason":"completed","iterations":3,"usage":{"inputTokens":19209,"outputTokens":294,"cacheReadTokens":8704,"cacheWriteTokens":0,"totalCost":0},"aggregateUsage":{"inputTokens":19209,"outputTokens":294,"cacheReadTokens":8704,"cacheWriteTokens":0,"totalCost":0},"durationMs":10075,"text":"The task is complete.","model":{"id":"nvidia/nemotron-3.5-lightning:free","provider":"openrouter"}}""";

    // Recorded real failure frames (shapes verified live; the spend-limit
    // URL's workspace key id is redacted — it is secret-derived).
    private const string AgentErrorFrame =
        """{"ts":"2026-09-16T18:01:41.599Z","type":"agent_event","event":{"type":"error","error":{"name":"Error","message":"User not found.","stack":"Error: User not found."},"errorClass":"auth","recoverable":false,"iteration":1}}""";
    private const string ErrorRunResultFrame =
        """{"ts":"2026-09-16T18:01:51.706Z","type":"run_result","finishReason":"error","iterations":1,"usage":{"inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"totalCost":0},"aggregateUsage":{"inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"totalCost":0},"durationMs":152,"text":"Key limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED","model":{"id":"anthropic/claude-sonnet-4","provider":"openrouter"}}""";
    private const string StderrErrorLine =
        """{"ts":"2026-09-16T18:01:41.673Z","type":"error","message":"User not found."}""";

    [Fact]
    public void TryClaim_HookStartFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(HookStartFrame);

        Assert.True(new ClineStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_IterationStartFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(IterationStartFrame);

        Assert.True(new ClineStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ToolStartFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(ToolStartFrame);

        Assert.True(new ClineStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_UsageFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(UsageFrame);

        Assert.True(new ClineStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_RunResultFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(RunResultFrame);

        Assert.True(new ClineStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ErrorFrames_NotClaimed()
    {
        // The agent_event error frame and the bare stderr error line are
        // deliberately unclaimed (see parser docs): the error line has no
        // cline-unique marker, and the runner's failure path lifts the
        // message through the terminal diagnoser independently of sniffing.
        using var agentError = JsonDocument.Parse(AgentErrorFrame);
        using var stderrError = JsonDocument.Parse(StderrErrorLine);
        using var errorResult = JsonDocument.Parse(ErrorRunResultFrame);

        Assert.False(new ClineStreamParser().TryClaim(agentError.RootElement));
        Assert.False(new ClineStreamParser().TryClaim(stderrError.RootElement));
        // The error run_result keeps its terminal shape — only the error
        // agent_event and stderr error line are claim-exempt.
        Assert.True(new ClineStreamParser().TryClaim(errorResult.RootElement));
    }

    [Fact]
    public void TryClaim_OtherAgentsShapes_NotClaimed()
    {
        // Guard against claim drift swallowing another CLI's lines: none of
        // these carry cline's envelope vocabulary.
        const string autohandToolStart =
            """{"type":"tool_start","toolId":"call-1","toolName":"read_file","toolArgs":{"path":"notes.txt"}}""";
        const string genericResult =
            """{"type":"result","content":"done"}""";
        using var autohand = JsonDocument.Parse(autohandToolStart);
        using var result = JsonDocument.Parse(genericResult);

        Assert.False(new ClineStreamParser().TryClaim(autohand.RootElement));
        Assert.False(new ClineStreamParser().TryClaim(result.RootElement));
    }

    [Fact]
    public async Task ParseAsync_RecordedHealthyRun_MapsToolsUsageAndFinalText()
    {
        var text = string.Join("\n",
            HookStartFrame, IterationStartFrame, ReasoningStartFrame,
            UsageFrame, ToolStartFrame, ToolEndFrame, DoneFrame, RunResultFrame);

        var summary = await new ClineStreamParser().ParseAsync(StreamOf(text));

        Assert.False(summary.IsUnsupported);
        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("editor", tool.ToolName);
        Assert.Equal("call-2c6742a4-4f20-48cf-9e18-9d12cdc0dbb5", tool.ToolUseId);
        Assert.True(tool.Succeeded);
        // Cumulative usage from the terminal run_result aggregateUsage wins
        // over the per-iteration slices.
        Assert.Equal(19209, summary.InputTokens);
        Assert.Equal(294, summary.OutputTokens);
        Assert.Equal(8704, summary.CachedInputTokens);
        Assert.Equal(0m, summary.EstimatedUsd);
        Assert.Equal("The task is complete.", summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_RecordedFailureRun_ParsesWithoutFinalText()
    {
        var text = string.Join("\n", IterationStartFrame, AgentErrorFrame, ErrorRunResultFrame);

        var summary = await new ClineStreamParser().ParseAsync(StreamOf(text));

        // The error run_result carries measured (zero) usage, so the stream
        // is understood even though there is no assistant text.
        Assert.False(summary.IsUnsupported);
        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Empty(summary.ToolCalls);
        Assert.True(
            string.IsNullOrEmpty(summary.FinalAssistantMessage)
            || !summary.FinalAssistantMessage.Contains("Key limit exceeded", StringComparison.Ordinal),
            "error run_result text must not surface as the assistant message");
    }

    [Fact]
    public async Task ParseAsync_EmptyResponse_IsUnsupported()
    {
        var summary = await new ClineStreamParser().ParseAsync(StreamOf(string.Empty));

        Assert.True(summary.IsUnsupported);
    }

    [Fact]
    public async Task ParseAsync_NonJsonNoiseOnly_IsUnsupported()
    {
        var summary = await new ClineStreamParser().ParseAsync(
            StreamOf("some plain log line\nanother one\n"));

        Assert.True(summary.IsUnsupported);
    }
}

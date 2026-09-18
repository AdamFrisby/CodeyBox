using System.Text.Json;
using CodeyBox.Agents.DotNetOpencode;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DotNetOpencodeStreamParser"/>. Claim vocabulary pins
/// the envelope recorded live against
/// 0.1.0-ci.20260905083303.33955573552.1 (<c>step_start</c> with
/// <c>ses_/prt_/msg_</c> ids, <c>error</c> frames); the remaining verbs
/// (<c>text</c>, <c>reasoning</c>, <c>tool_use</c>, <c>step_finish</c>) are the
/// set the CLI's run-output layer emits.
/// </summary>
public sealed class DotNetOpencodeStreamParserTests
{
    private static JsonElement ParseLine(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

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
    public void Kind_IsDotNetOpencode()
    {
        Assert.Equal(AgentKind.DotNetOpencode, new DotNetOpencodeStreamParser().Kind);
    }

    [Fact]
    public void TryClaim_RecordedStepStart_Claimed()
    {
        // Recorded live 2026-09-16.
        var line = ParseLine(
            "{\"type\":\"step_start\",\"timestamp\":1789512734124,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"part\":{\"id\":\"prt_0a74555ac001QmCuITT448cuhf\",\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"messageID\":\"msg_0a745489a001WHQtkGpO5IBS2h\",\"type\":\"step-start\"}}");

        Assert.True(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Fact]
    public void TryClaim_RecordedErrorFrame_Claimed()
    {
        // Recorded live 2026-09-16 (bogus-key 401).
        var line = ParseLine(
            "{\"type\":\"error\",\"timestamp\":1789512734146,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"error\":{\"type\":\"provider.auth\",\"message\":\"Provider request failed with HTTP 401.\",\"status\":401}}");

        Assert.True(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Theory]
    [InlineData("text")]
    [InlineData("reasoning")]
    [InlineData("tool_use")]
    [InlineData("step_finish")]
    public void TryClaim_RunVerbs_Claimed(string type)
    {
        var line = ParseLine(
            $"{{\"type\":\"{type}\",\"timestamp\":1,\"sessionID\":\"ses_1\"}}");

        Assert.True(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("agent_end")]
    [InlineData("message_end")]
    [InlineData("assistant")]
    [InlineData("result")]
    public void TryClaim_ForeignVerbs_NotClaimed(string type)
    {
        // pi / claude verbs must never be stolen even with a sessionID.
        var line = ParseLine(
            $"{{\"type\":\"{type}\",\"timestamp\":1,\"sessionID\":\"ses_1\"}}");

        Assert.False(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Fact]
    public void TryClaim_MissingSessionId_NotClaimed()
    {
        // A foreign {"type":"text"} line without the CLI envelope stays foreign.
        var line = ParseLine("{\"type\":\"text\",\"text\":\"hello\"}");

        Assert.False(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Fact]
    public void TryClaim_NonObject_NotClaimed()
    {
        var line = ParseLine("[{\"type\":\"text\",\"sessionID\":\"ses_1\"}]");

        Assert.False(new DotNetOpencodeStreamParser().TryClaim(line));
    }

    [Fact]
    public async Task ParseAsync_EmptyStream_ReturnsUnsupported()
    {
        var parser = new DotNetOpencodeStreamParser();
        await using var stream = StreamOf("");

        var summary = await parser.ParseAsync(stream);

        Assert.True(summary.IsUnsupported);
        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Null(summary.FinalAssistantMessage);
    }

    [Fact]
    public async Task ParseAsync_WhitespaceOnlyStream_ReturnsUnsupported()
    {
        var parser = new DotNetOpencodeStreamParser();
        await using var stream = StreamOf("   \n\t\n  \n");

        var summary = await parser.ParseAsync(stream);

        Assert.True(summary.IsUnsupported);
    }

    [Fact]
    public async Task ParseAsync_RecordedFailureOutput_ProducesStreamSummary()
    {
        // Recorded live 2026-09-16 (bogus Anthropic key -> provider HTTP 401).
        const string recordedFailure =
            "{\"type\":\"step_start\",\"timestamp\":1789512734124,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"part\":{\"id\":\"prt_0a74555ac001QmCuITT448cuhf\",\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"messageID\":\"msg_0a745489a001WHQtkGpO5IBS2h\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"error\",\"timestamp\":1789512734146,\"sessionID\":\"ses_0a74541ec001ry8nCxA7V2gTzb\",\"error\":{\"type\":\"provider.auth\",\"message\":\"Provider request failed with HTTP 401.\",\"status\":401}}\n";

        var parser = new DotNetOpencodeStreamParser();
        await using var stream = StreamOf(recordedFailure);

        var summary = await parser.ParseAsync(stream);

        Assert.False(summary.IsUnsupported);
        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Empty(summary.ToolCalls);
        Assert.Equal("Provider request failed with HTTP 401.", summary.FinalAssistantMessage);
        Assert.Equal(TimeSpan.FromMilliseconds(22), summary.TotalDuration);
    }

    [Fact]
    public async Task ParseAsync_RecordedSuccessRun_ProducesStreamSummaryWithTokensAndTools()
    {
        const string recordedSuccess =
            "{\"type\":\"step_start\",\"timestamp\":1789512734000,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_0\",\"type\":\"step-start\"}}\n" +
            "{\"type\":\"text\",\"timestamp\":1789512734100,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"type\":\"text\",\"text\":\"Checking directory contents.\"}}\n" +
            "{\"type\":\"tool_use\",\"timestamp\":1789512734200,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"tool_1\",\"name\":\"bash\",\"input\":{\"command\":\"ls -la\"}}}\n" +
            "{\"type\":\"tool_result\",\"timestamp\":1789512734300,\"sessionID\":\"ses_1\",\"part\":{\"tool_use_id\":\"tool_1\",\"output\":\"file1.cs\\nfile2.cs\",\"is_error\":false}}\n" +
            "{\"type\":\"step_finish\",\"timestamp\":1789512734400,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_2\",\"type\":\"step-finish\",\"tokens\":{\"input\":1200,\"output\":340,\"cache\":{\"read\":5600,\"write\":10}}}}\n";

        var parser = new DotNetOpencodeStreamParser();
        await using var stream = StreamOf(recordedSuccess);

        var summary = await parser.ParseAsync(stream);

        Assert.False(summary.IsUnsupported);
        Assert.Equal(1200, summary.InputTokens);
        Assert.Equal(340, summary.OutputTokens);
        Assert.Equal(5600, summary.CachedInputTokens);
        Assert.Single(summary.ToolCalls);
        var tool = summary.ToolCalls[0];
        Assert.Equal("tool_1", tool.ToolUseId);
        Assert.Equal("bash", tool.ToolName);
        Assert.True(tool.Succeeded);
        Assert.Equal("Checking directory contents.", summary.FinalAssistantMessage);
        Assert.Equal(TimeSpan.FromMilliseconds(400), summary.TotalDuration);
    }

    [Fact]
    public async Task ParseAsync_StandaloneHostingLogsInterleaved_ParsesEventsCleanly()
    {
        const string recordedWithLogs =
            "info: Microsoft.Hosting.Lifetime[14]\n" +
            "      Now listening on: http://127.0.0.1:37033\n" +
            "{\"type\":\"step_start\",\"timestamp\":1000,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_1\",\"type\":\"step-start\"}}\n" +
            "dbug: Microsoft.AspNetCore.Hosting.Diagnostics[1]\n" +
            "{\"type\":\"text\",\"timestamp\":1100,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_2\",\"type\":\"text\",\"text\":\"Task completed successfully.\"}}\n" +
            "{\"type\":\"step_finish\",\"timestamp\":1200,\"sessionID\":\"ses_1\",\"part\":{\"id\":\"prt_3\",\"type\":\"step-finish\",\"tokens\":{\"input\":100,\"output\":50,\"cache\":{\"read\":10}}}}\n";

        var parser = new DotNetOpencodeStreamParser();
        await using var stream = StreamOf(recordedWithLogs);

        var summary = await parser.ParseAsync(stream);

        Assert.False(summary.IsUnsupported);
        Assert.Equal(100, summary.InputTokens);
        Assert.Equal(50, summary.OutputTokens);
        Assert.Equal(10, summary.CachedInputTokens);
        Assert.Equal("Task completed successfully.", summary.FinalAssistantMessage);
    }
}

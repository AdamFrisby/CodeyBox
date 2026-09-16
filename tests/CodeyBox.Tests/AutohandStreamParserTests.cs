using System.Text.Json;
using CodeyBox.Agents.Autohand;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="AutohandStreamParser"/>: autohand's
/// <c>--output-format stream-json</c> claim vocabulary (verified against
/// autohand-cli 0.9.7 live frames), the tool/result mapping, and
/// non-interference with the other registered parsers' claims. Parse cases
/// run over recorded real output, including a provider failure and an empty
/// response. Recorded error messages are sanitised of workspace key ids.
/// </summary>
public sealed class AutohandStreamParserTests
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

    // Recorded real frames (autohand-cli 0.9.7, bare headless run against
    // OpenRouter nemotron free that edited a file end to end).
    private const string ToolStartFrame =
        """{"type":"tool_start","toolId":"call-befd2305-0c9c-45d1-b6f5-63d2b32519df","toolName":"read_file","toolArgs":{"path":"/tmp/ahedit/notes.txt","type":"read_file"}}""";
    private const string ToolEndFrame =
        """{"type":"tool_end","toolId":"call-befd2305-0c9c-45d1-b6f5-63d2b32519df","toolName":"read_file","toolSuccess":true,"toolOutput":"     1\tplaceholder"}""";
    private const string FileModifiedFrame =
        """{"type":"file_modified","filePath":"/tmp/ahedit/notes.txt","changeType":"modify","toolId":"call-c1a8487d-da7e-47a8-b5cb-a312ab182e77"}""";
    private const string ResultFrame =
        """{"type":"result","content":"The task is complete. I appended the line as requested."}""";

    // Recorded real failure frames (shapes verified live; the quota URL's
    // workspace key id is redacted — it is secret-derived).
    private const string AuthErrorFrame =
        """{"type":"error","message":"Authentication failed. Please verify your OpenRouter API key in ~/.autohand/config.json.\nUser not found."}""";
    private const string QuotaErrorFrame =
        """{"type":"error","message":"Access denied. Your OpenRouter API key may not have permission for this model.\nKey limit exceeded (total limit). Manage it using https://openrouter.ai/workspaces/default/keys/REDACTED"}""";
    private const string GenericErrorFrame =
        """{"type":"error","message":"Command did not complete successfully."}""";

    [Fact]
    public void TryClaim_ToolStartFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(ToolStartFrame);

        Assert.True(new AutohandStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ToolEndFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(ToolEndFrame);

        Assert.True(new AutohandStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_FileModifiedFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(FileModifiedFrame);

        Assert.True(new AutohandStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ResultWithContentNoSubtype_Claimed()
    {
        using var doc = JsonDocument.Parse(ResultFrame);

        Assert.True(new AutohandStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ResultWithSubtype_NotClaimed()
    {
        // Claude's result frames always carry subtype; claiming them would
        // steal Claude (and Claude-shaped wrapper) streams in the
        // first-claim-wins sniffer.
        using var doc = JsonDocument.Parse("{\"type\":\"result\",\"subtype\":\"success\",\"content\":\"hi\"}");

        Assert.False(new AutohandStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_BareErrorFrame_NotClaimed()
    {
        // type:error with a bare message has no autohand-unique marker;
        // claiming it would misattribute other agents' error lines. The
        // runner lifts the message via AutohandTerminalDiagnoser
        // independently of sniffing.
        using var doc = JsonDocument.Parse(GenericErrorFrame);

        Assert.False(new AutohandStreamParser().TryClaim(doc.RootElement));
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("tool_use")]
    [InlineData("tool_result")]
    public void TryClaim_ClaudeVocabulary_NotClaimed(string type)
    {
        using var doc = JsonDocument.Parse($"{{\"type\":\"{type}\"}}");

        Assert.False(new AutohandStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ToolStartWithoutIds_NotClaimed()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"tool_start\",\"toolName\":\"read_file\"}");

        Assert.False(new AutohandStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void CanEmitShapeOf_Autohand_ReturnsTrue()
    {
        Assert.True(new AutohandStreamParser().CanEmitShapeOf(AgentKind.Autohand));
    }

    [Fact]
    public async Task ParseAsync_HealthyRun_SurfacesToolsAndFinalText()
    {
        var parser = new AutohandStreamParser();
        await using var stream = StreamOf(string.Join("\n",
            ToolStartFrame, ToolEndFrame, FileModifiedFrame, ResultFrame) + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Contains("The task is complete", summary.FinalAssistantMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(summary.ToolCalls, t => t.ToolName == "read_file");
        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_FailureOutput_ExtractsNoUsage()
    {
        // Recorded real failure shape (bad OpenRouter key, exit 1): a single
        // type:error frame. No usage is attributed; the terminal message is
        // lifted by AutohandTerminalDiagnoser, not the summary.
        var parser = new AutohandStreamParser();
        await using var stream = StreamOf(AuthErrorFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.True(string.IsNullOrEmpty(summary.FinalAssistantMessage));
    }

    [Fact]
    public async Task ParseAsync_QuotaFailureOutput_ExtractsNoUsage()
    {
        var parser = new AutohandStreamParser();
        await using var stream = StreamOf(QuotaErrorFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_EmptyResponse_YieldsZeroUsage()
    {
        var parser = new AutohandStreamParser();
        await using var stream = StreamOf(string.Empty);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.True(string.IsNullOrEmpty(summary.FinalAssistantMessage));
    }

    [Fact]
    public async Task ParseAsync_DocumentedUsageFrame_MapsTokens()
    {
        // The headless-mode docs describe a messageType:usage frame carrying
        // promptTokens/completionTokens. Bare 0.9.7 streams were observed
        // without any usage frame, but the documented shape is honoured when
        // present so a build that emits it attributes correctly.
        const string usageFrame =
            """{"type":"message","messageType":"usage","promptTokens":294,"completionTokens":97}""";
        var parser = new AutohandStreamParser();
        await using var stream = StreamOf(usageFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(294, summary.InputTokens);
        Assert.Equal(97, summary.OutputTokens);
    }
}

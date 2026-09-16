using System.Text.Json;
using CodeyBox.Agents.Kilo;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="KiloStreamParser"/>: the OpenCode-family claim
/// envelope (<c>step_start/step_finish/text</c> + <c>ses_…</c> session id +
/// nested <c>part</c> with <c>sessionID/messageID</c>), kilo's nested
/// <c>part.text</c> / <c>part.tokens</c> mapping, and non-interference with
/// the other registered parsers' claims. Frame fixtures are recorded real
/// @kilocode/cli 7.7.2 output (ids shortened; token counts verbatim).
/// </summary>
public sealed class KiloStreamParserTests
{
    private const string SessionId = "ses_f54610d34ffesBY0XvLr2x3pIp";

    private const string StepStartFrame =
        "{\"type\":\"step_start\",\"timestamp\":1789585720043,\"sessionID\":\"" + SessionId + "\"," +
        "\"part\":{\"id\":\"prt_0ab9f02e3001lUrDZ8U0STdfR3\",\"sessionID\":\"" + SessionId + "\"," +
        "\"messageID\":\"msg_0ab9eff19001Ub8mEpGVbLw8G2\",\"type\":\"step-start\"}}";

    private const string TextFrame =
        "{\"type\":\"text\",\"timestamp\":1789585720933,\"sessionID\":\"" + SessionId + "\"," +
        "\"part\":{\"id\":\"prt_0ab9f060a001QvKEcocNjlUkLt\",\"sessionID\":\"" + SessionId + "\"," +
        "\"messageID\":\"msg_0ab9eff19001Ub8mEpGVbLw8G2\",\"type\":\"text\",\"text\":\"KILO_JSON_OK\"," +
        "\"time\":{\"start\":1789585720842,\"end\":1789585720924}}}";

    private const string StepFinishFrame =
        "{\"type\":\"step_finish\",\"timestamp\":1789585720968,\"sessionID\":\"" + SessionId + "\"," +
        "\"part\":{\"id\":\"prt_0ab9f0673001wIVblRyyXuXCo9\",\"sessionID\":\"" + SessionId + "\"," +
        "\"messageID\":\"msg_0ab9eff19001Ub8mEpGVbLw8G2\",\"type\":\"step-finish\",\"reason\":\"stop\"," +
        "\"metrics\":{\"generation\":55.73770491803279,\"source\":\"computed\"}," +
        "\"time\":{\"start\":1789585720030,\"end\":1789585720945,\"elapsed\":915},\"cost\":0," +
        "\"tokens\":{\"total\":12655,\"input\":1724,\"output\":2,\"reasoning\":49,\"cache\":{\"read\":10880,\"write\":0}}}}";

    // Recorded real tool frame (@kilocode/cli 7.7.2, bash call in a
    // file-edit run; ids shortened, field names verbatim). The merged frame
    // already carries the completed call: status, input, output, and
    // start/end timestamps.
    private const string ToolUseFrame =
        "{\"type\":\"tool_use\",\"timestamp\":1789586677921,\"sessionID\":\"" + SessionId + "\"," +
        "\"part\":{\"id\":\"prt_tool\",\"sessionID\":\"" + SessionId + "\",\"messageID\":\"msg_tool\"," +
        "\"type\":\"tool\",\"callID\":\"call-0bddae16-29ae-4ded-b1db-23879117dc9b\",\"tool\":\"bash\"," +
        "\"state\":{\"status\":\"completed\"," +
        "\"input\":{\"command\":\"ls /tmp/kiloreal/notes.txt\",\"description\":\"Check if notes.txt exists\"}," +
        "\"output\":\"/tmp/kiloreal/notes.txt\\n\"," +
        "\"time\":{\"start\":1789586677750,\"end\":1789586677907}}}}";
    // Recorded real failure shape (@kilocode/cli 7.7.2, missing API key,
    // exit 1): a single type:error frame (response headers trimmed — the
    // message + statusCode carry the signal).
    private const string AuthErrorFrame =
        "{\"type\":\"error\",\"timestamp\":1789585744519,\"sessionID\":\"ses_f5460ae95ffeLYpfQ1svbD5utB\"," +
        "\"error\":{\"name\":\"APIError\",\"data\":{\"message\":\"No cookie auth credentials found\"," +
        "\"statusCode\":401,\"isRetryable\":false," +
        "\"responseBody\":\"{\\\"error\\\":{\\\"message\\\":\\\"No cookie auth credentials found\\\",\\\"code\\\":401}}\"," +
        "\"metadata\":{\"url\":\"https://openrouter.ai/api/v1/chat/completions\"}}}}";

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
    public void TryClaim_StepStartWithEnvelope_Claimed()
    {
        using var doc = JsonDocument.Parse(StepStartFrame);

        Assert.True(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_TextWithEnvelope_Claimed()
    {
        using var doc = JsonDocument.Parse(TextFrame);

        Assert.True(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_StepFinishWithEnvelope_Claimed()
    {
        using var doc = JsonDocument.Parse(StepFinishFrame);

        Assert.True(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_StepFinishWithoutPart_NotClaimed()
    {
        // The nested part envelope (sessionID + messageID) is what makes the
        // claim kilo-distinctive; a bare type:step_finish must never be
        // misattributed.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"step_finish\",\"sessionID\":\"ses_abc\"}");

        Assert.False(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ToolUseWithEnvelope_Claimed()
    {
        using var doc = JsonDocument.Parse(ToolUseFrame);

        Assert.True(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ToolUseWithoutPart_NotClaimed()
    {
        using var doc = JsonDocument.Parse(
            "{\"type\":\"tool_use\",\"sessionID\":\"ses_abc\"}");

        Assert.False(new KiloStreamParser().TryClaim(doc.RootElement));
    }
    [Fact]
    public void TryClaim_StepFinishWithoutSesPrefix_NotClaimed()
    {
        using var doc = JsonDocument.Parse(
            "{\"type\":\"step_finish\",\"sessionID\":\"other-1\"," +
            "\"part\":{\"sessionID\":\"other-1\",\"messageID\":\"m1\"}}");

        Assert.False(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_ErrorFrame_NotClaimed()
    {
        // Bare error lines carry no kilo-unique marker beyond the envelope;
        // claiming them risks misattributing another agent's failure line.
        // The runner lifts the message via KiloTerminalDiagnoser instead.
        using var doc = JsonDocument.Parse(AuthErrorFrame);

        Assert.False(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("result")]
    [InlineData("tool_use")]
    [InlineData("tool_result")]
    public void TryClaim_ClaudeVocabulary_NotClaimed(string type)
    {
        // Claude's parser owns these; kilo must not steal Claude streams.
        using var doc = JsonDocument.Parse($"{{\"type\":\"{type}\"}}");

        Assert.False(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_CodexDottedVocabulary_NotClaimed()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"turn.started\"}");

        Assert.False(new KiloStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void CanEmitShapeOf_Kilo_ReturnsTrue()
    {
        Assert.True(new KiloStreamParser().CanEmitShapeOf(AgentKind.Kilo));
    }

    [Fact]
    public async Task ParseAsync_HealthyRun_SurfacesFinalTextAndUsage()
    {
        // Recorded real success run: step_start / text / step_finish.
        // total (12655) == input (1724) + cache.read (10880) + output (2) +
        // reasoning (49): input is fresh input, cache.read the cached bucket.
        var parser = new KiloStreamParser();
        await using var stream = StreamOf(
            StepStartFrame + "\n" + TextFrame + "\n" + StepFinishFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Contains("KILO_JSON_OK", summary.FinalAssistantMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1724, summary.InputTokens);
        Assert.Equal(2, summary.OutputTokens);
        Assert.Equal(10880, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_FailureOutput_ExtractsNoUsage()
    {
        // Recorded real failure shape (missing key, exit 1): a single
        // type:error frame. No usage is attributed; the terminal message is
        // lifted by KiloTerminalDiagnoser, not the summary.
        var parser = new KiloStreamParser();
        await using var stream = StreamOf(AuthErrorFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.True(string.IsNullOrEmpty(summary.FinalAssistantMessage));
    }

    [Fact]
    public async Task ParseAsync_EmptyResponse_ExtractsNothing()
    {
        // An empty capture (killed run, truncated stream file) must parse as
        // "no signal", not throw and not fabricate zeros that look like a
        // measured $0 run.
        var parser = new KiloStreamParser();
        await using var stream = StreamOf(string.Empty);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Equal(0, summary.CachedInputTokens);
        Assert.True(string.IsNullOrEmpty(summary.FinalAssistantMessage));
    }

    [Fact]
    public async Task ParseAsync_ToolUseFrame_SurfacesCompletedToolCall()
    {
        // Recorded real tool frame: the merged event carries the completed
        // bash call, so the summary gains one succeeded tool invocation.
        var parser = new KiloStreamParser();
        await using var stream = StreamOf(ToolUseFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("bash", tool.ToolName);
        Assert.Equal("call-0bddae16-29ae-4ded-b1db-23879117dc9b", tool.ToolUseId);
        Assert.True(tool.Succeeded);
        Assert.Contains("ls /tmp/kiloreal/notes.txt", tool.InputSummary, StringComparison.Ordinal);
        Assert.True(tool.Duration.HasValue && tool.Duration.Value >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ParseAsync_MultiStepRun_SurfacesToolsTextAndUsage()
    {
        // Shape of the recorded real file-edit run: tool calls interleaved
        // with text and terminal step-finish frames.
        var parser = new KiloStreamParser();
        await using var stream = StreamOf(
            StepStartFrame + "\n" + ToolUseFrame + "\n" + TextFrame + "\n" + StepFinishFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Contains(summary.ToolCalls, t => t.ToolName == "bash");
        Assert.Contains("KILO_JSON_OK", summary.FinalAssistantMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1724, summary.InputTokens);
    }

    [Fact]
    public async Task ParseAsync_LifecycleFrames_DoNotCorruptSummary()
    {
        var parser = new KiloStreamParser();
        await using var stream = StreamOf(StepStartFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Empty(summary.ToolCalls);
    }
}

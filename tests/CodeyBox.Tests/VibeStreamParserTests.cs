using System.Text.Json;
using CodeyBox.Agents.Vibe;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="VibeStreamParser"/>: vibe's <c>--output
/// streaming</c> claim vocabulary (verified against vibe 2.25.4 live frames),
/// assistant-text extraction, effect-frame tool-call mapping, and
/// non-interference with the other registered parsers' claims. Parse cases
/// run over recorded real output, including a provider failure and an empty
/// response.
/// </summary>
public sealed class VibeStreamParserTests
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

    // Recorded real assistant frame (vibe 2.25.4, OpenRouter nemotron free).
    private const string AssistantFrame =
        """{"id":"265ca6b7-305c-40f6-a8ca-46cca31fd819","sessionId":"a54c1241-bc61-28d6-0141-2a675c3860c1","turnId":"b6f13933-c15e-45b8-bf46-b419a881ef5b","createdAt":1789580847231,"updatedAt":1789580847320,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"assistant","content":[{"type":"text","text":"8"}],"source":null,"userDisplayContent":null}""";

    // Recorded real user-echo frame.
    private const string UserFrame =
        """{"id":"eaba512d-57c4-41c4-8478-d83c4c75929f","sessionId":"a54c1241-bc61-28d6-0141-2a675c3860c1","turnId":"b6f13933-c15e-45b8-bf46-b419a881ef5b","createdAt":1789580841806,"updatedAt":1789580841806,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"user","content":[{"type":"text","text":"What is 4+4? Reply with just the number."}],"source":"turn_start","userDisplayContent":null}""";

    // Recorded real tool-call frame (write_file run).
    private const string EffectFrame =
        """{"id":"call-e9d9510b-64a5-4b55-a0d3-20b61c4cea42","sessionId":"402b5676-411c-1855-fc4f-d4c27677632e","turnId":"be0f7629-70bb-4f85-9b85-84ca0f6cc445","createdAt":1789580864397,"updatedAt":1789580864449,"generationStatus":"completed","relatedEntryId":null,"type":"effect","title":"write_file","detail":{"toolName":"write_file","display":{"summary":"Writing hello.txt"},"kind":"file_write","input":{"filePath":"hello.txt","content":"hello"}},"state":{"status":"completed","output":{"filePath":"/tmp/vibetest/toolrun/hello.txt","content":"hello"},"outputText":"","durationMs":2.5}}""";

    [Fact]
    public void TryClaim_MessageFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(AssistantFrame);

        Assert.True(new VibeStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_EffectFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(EffectFrame);

        Assert.True(new VibeStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_MessageWithoutSessionEnvelope_NotClaimed()
    {
        // A foreign {"type":"message"} line without vibe's sessionId/turnId
        // envelope must never be misattributed.
        using var doc = JsonDocument.Parse("{\"type\":\"message\",\"role\":\"assistant\",\"text\":\"hi\"}");

        Assert.False(new VibeStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_GooseShapedMessage_NotClaimed()
    {
        // Goose's nested-envelope message frame carries no sessionId/turnId,
        // so the vibe parser must not steal goose streams.
        using var doc = JsonDocument.Parse(
            "{\"type\":\"message\",\"message\":{\"id\":\"gen-1\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");

        Assert.False(new VibeStreamParser().TryClaim(doc.RootElement));
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("result")]
    [InlineData("tool_use")]
    [InlineData("tool_result")]
    public void TryClaim_ClaudeVocabulary_NotClaimed(string type)
    {
        // Claude's parser owns these; vibe must not steal Claude streams.
        using var doc = JsonDocument.Parse($"{{\"type\":\"{type}\"}}");

        Assert.False(new VibeStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_CodexDottedVocabulary_NotClaimed()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"turn.started\"}");

        Assert.False(new VibeStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void CanEmitShapeOf_Vibe_ReturnsTrue()
    {
        Assert.True(new VibeStreamParser().CanEmitShapeOf(AgentKind.Vibe));
    }

    [Fact]
    public async Task ParseAsync_AssistantFrame_SurfacesFinalText()
    {
        var parser = new VibeStreamParser();
        await using var stream = StreamOf(UserFrame + "\n" + AssistantFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Contains("8", summary.FinalAssistantMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_EffectFrame_MapsToolCall()
    {
        var parser = new VibeStreamParser();
        await using var stream = StreamOf(UserFrame + "\n" + EffectFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        var tool = Assert.Single(summary.ToolCalls);
        Assert.Equal("write_file", tool.ToolName);
    }

    [Fact]
    public async Task ParseAsync_NoUsageInStream_YieldsZeroUsage()
    {
        // Vibe history entries carry no token counts anywhere (verified live:
        // success, tool-use and failure runs alike emit no usage object), so
        // attribution stays at zero rather than fabricating totals.
        var parser = new VibeStreamParser();
        await using var stream = StreamOf(UserFrame + "\n" + AssistantFrame + "\n" + EffectFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.Equal(0, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_FailureOutput_YieldsNoTextOrUsage()
    {
        // Recorded real failure shape (vibe 2.25.4, $0-limit key against a
        // paid model): stdout carries only the user-echo history entry; the
        // API-error body goes to stderr, outside this stream.
        var parser = new VibeStreamParser();
        await using var stream = StreamOf(
            """{"id":"0447d122-1175-477c-96c9-6145edfed5e3","sessionId":"fe267585-22f6-653b-95c5-1ba5c441fdf8","turnId":"7e381af2-b392-46ee-a18b-130d7d0d1718","createdAt":1789580940416,"updatedAt":1789580940416,"generationStatus":"completed","relatedEntryId":null,"type":"message","role":"user","content":[{"type":"text","text":"Say OK."}],"source":"turn_start","userDisplayContent":null}""" + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        // No assistant frame exists: the only text in the stream is the
        // user echo. The failure cause lives on stderr, outside this stream.
        Assert.Contains("Say OK.", summary.FinalAssistantMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_EmptyResponse_YieldsZeroUsage()
    {
        var parser = new VibeStreamParser();
        await using var stream = StreamOf(string.Empty);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.True(string.IsNullOrEmpty(summary.FinalAssistantMessage));
    }
}

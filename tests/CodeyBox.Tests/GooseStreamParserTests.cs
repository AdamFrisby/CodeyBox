using System.Text.Json;
using CodeyBox.Agents.Goose;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GooseStreamParser"/>: goose's
/// <c>--output-format stream-json</c> claim vocabulary (verified against
/// goose 1.50.1 live frames), the complete-frame usage mapping, and
/// non-interference with the other registered parsers' claims. Parse cases
/// run over recorded real output, including a provider failure and an empty
/// response.
/// </summary>
public sealed class GooseStreamParserTests
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

    // Recorded real per-chunk frame (goose 1.50.1, OpenRouter nemotron free).
    private const string MessageFrame =
        """{"type":"message","message":{"id":"gen-1789551821-abc","role":"assistant","created":1789551821,"content":[{"type":"thinking","thinking":" reply","signature":""}],"metadata":{"userVisible":true,"agentVisible":true,"inference":{"provider":"openrouter","requestedModel":"nvidia/nemotron-3.5-lightning:free"}}}}""";

    // Recorded real terminal totals frame.
    private const string CompleteFrame =
        """{"type":"complete","total_tokens":5357,"input_tokens":5257,"output_tokens":100,"cache_read_input_tokens":4352,"cache_write_input_tokens":0,"cost_usd":0.0}""";

    [Fact]
    public void TryClaim_MessageFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(MessageFrame);

        Assert.True(new GooseStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_CompleteFrame_Claimed()
    {
        using var doc = JsonDocument.Parse(CompleteFrame);

        Assert.True(new GooseStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_MessageWithoutEnvelope_NotClaimed()
    {
        // A foreign {"type":"message"} line without goose's nested
        // assistant/user envelope must never be misattributed.
        using var doc = JsonDocument.Parse("{\"type\":\"message\",\"role\":\"assistant\",\"text\":\"hi\"}");

        Assert.False(new GooseStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_CompleteWithoutTotals_NotClaimed()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"complete\"}");

        Assert.False(new GooseStreamParser().TryClaim(doc.RootElement));
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("result")]
    [InlineData("tool_use")]
    [InlineData("tool_result")]
    public void TryClaim_ClaudeVocabulary_NotClaimed(string type)
    {
        // Claude's parser owns these; goose must not steal Claude streams.
        using var doc = JsonDocument.Parse($"{{\"type\":\"{type}\"}}");

        Assert.False(new GooseStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_CodexDottedVocabulary_NotClaimed()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"turn.started\"}");

        Assert.False(new GooseStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void CanEmitShapeOf_Goose_ReturnsTrue()
    {
        Assert.True(new GooseStreamParser().CanEmitShapeOf(AgentKind.Goose));
    }

    [Fact]
    public async Task ParseAsync_MapsCompleteFrameTotals()
    {
        // The shared ParseUsage knows no goose names; the complete frame
        // reports input_tokens/output_tokens/cache_read_input_tokens.
        var parser = new GooseStreamParser();
        await using var stream = StreamOf(MessageFrame + "\n" + CompleteFrame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(5257, summary.InputTokens);
        Assert.Equal(100, summary.OutputTokens);
        Assert.Equal(4352, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_TextMessageFrame_SurfacesFinalText()
    {
        const string frame =
            """{"type":"message","message":{"id":"gen-1","role":"assistant","created":1789551816,"content":[{"type":"text","text":"GOOSE_OK"}],"metadata":{"userVisible":true}}}""";
        var parser = new GooseStreamParser();
        await using var stream = StreamOf(frame + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Contains("GOOSE_OK", summary.FinalAssistantMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParseAsync_FailureOutput_ExtractsErrorWithoutAttributingUsage()
    {
        // Recorded real failure shape (bad OpenRouter key, exit 0): an
        // assistant content error block plus an all-zero complete frame.
        // The banner lines are not JSON and must be skipped.
        var parser = new GooseStreamParser();
        await using var stream = StreamOf(
            "__( O)>  ● new session · openrouter nvidia/nemotron-3.5-lightning:free\n" +
            "   \\____)    20260916_6 · /tmp/goosetest\n" +
            "     L L     goose is ready\n" +
            """{"type":"message","message":{"id":"msg_1","role":"assistant","created":1789551852,"content":[{"type":"error","kind":"authentication","message":"Ran into this error: Authentication error: Authentication failed for https://openrouter.ai/api/v1/chat/completions. Status: 401 Unauthorized."}],"metadata":{"userVisible":true,"agentVisible":false}}}""" + "\n" +
            """{"type":"complete","total_tokens":0,"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_write_input_tokens":0,"cost_usd":0.0}""" + "\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_EmptyResponse_YieldsZeroUsage()
    {
        var parser = new GooseStreamParser();
        await using var stream = StreamOf(string.Empty);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
        Assert.True(string.IsNullOrEmpty(summary.FinalAssistantMessage));
    }
}

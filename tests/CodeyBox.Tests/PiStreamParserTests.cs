using System.Text.Json;
using CodeyBox.Agents.Pi;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PiStreamParser"/>: pi's <c>--mode json</c> claim
/// vocabulary, pi-named usage mapping, and non-interference with the other
/// registered parsers' claims.
/// </summary>
public sealed class PiStreamParserTests
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
    public void TryClaim_SessionHeader_Claimed()
    {
        using var doc = JsonDocument.Parse(
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"cwd\":\"/work\"}");

        Assert.True(new PiStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_SessionWithoutVersionOrCwd_NotClaimed()
    {
        // A foreign {"type":"session"} line must never be misattributed.
        using var doc = JsonDocument.Parse("{\"type\":\"session\"}");

        Assert.False(new PiStreamParser().TryClaim(doc.RootElement));
    }

    [Theory]
    [InlineData("agent_start")]
    [InlineData("agent_end")]
    [InlineData("agent_settled")]
    [InlineData("turn_start")]
    [InlineData("turn_end")]
    [InlineData("message_start")]
    [InlineData("message_update")]
    [InlineData("message_end")]
    public void TryClaim_LifecycleVerbs_Claimed(string type)
    {
        using var doc = JsonDocument.Parse($"{{\"type\":\"{type}\"}}");

        Assert.True(new PiStreamParser().TryClaim(doc.RootElement));
    }

    [Theory]
    [InlineData("assistant")]
    [InlineData("result")]
    [InlineData("tool_use")]
    [InlineData("tool_result")]
    public void TryClaim_ClaudeVocabulary_NotClaimed(string type)
    {
        // Claude's parser owns these; pi must not steal Claude streams.
        using var doc = JsonDocument.Parse($"{{\"type\":\"{type}\"}}");

        Assert.False(new PiStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_CodexDottedVocabulary_NotClaimed()
    {
        using var doc = JsonDocument.Parse("{\"type\":\"turn.started\"}");

        Assert.False(new PiStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public async Task ParseAsync_MapsPiUsageNames()
    {
        // The shared ParseUsage only knows input_tokens/prompt_tokens; pi
        // reports usage:{input,output,cacheRead}.
        var parser = new PiStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"cwd\":\"/work\"}\n" +
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"model\":\"m\"," +
            "\"usage\":{\"input\":1200,\"output\":340,\"cacheRead\":5600,\"cacheWrite\":10,\"totalTokens\":7140}}}\n" +
            "{\"type\":\"agent_end\",\"messages\":[],\"willRetry\":false}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(1200, summary.InputTokens);
        Assert.Equal(340, summary.OutputTokens);
        Assert.Equal(5600, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_SessionHeaderAndLifecycle_DoNotCorruptSummary()
    {
        var parser = new PiStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"cwd\":\"/work\"}\n" +
            "{\"type\":\"agent_start\"}\n" +
            "{\"type\":\"turn_start\"}\n" +
            "{\"type\":\"turn_end\"}\n" +
            "{\"type\":\"agent_end\",\"messages\":[],\"willRetry\":false}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Empty(summary.ToolCalls);
    }
}

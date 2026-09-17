using System.Text.Json;
using CodeyBox.Agents.Omp;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="OmpStreamParser"/>: the never-claim policy over the
/// shared pi wire shape (pi owns the shape), the <c>CanEmitShapeOf</c>
/// attribution matrix, and pi-named usage mapping over recorded real omp
/// 18.2.2 output — including the all-zero usage failure and the empty
/// response.
/// </summary>
public sealed class OmpStreamParserTests
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
    public void TryClaim_PiLifecycleVerb_NeverClaimed()
    {
        // Pi owns the shared shape; claiming would steal real pi streams
        // depending on registration order (same discipline as prime).
        using var doc = JsonDocument.Parse(
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"}}");

        Assert.False(new OmpStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void TryClaim_SessionHeader_NeverClaimed()
    {
        using var doc = JsonDocument.Parse(
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"cwd\":\"/work\"}");

        Assert.False(new OmpStreamParser().TryClaim(doc.RootElement));
    }

    [Fact]
    public void CanEmitShapeOf_OmpAndPi_True_Other_False()
    {
        var parser = new OmpStreamParser();

        Assert.True(parser.CanEmitShapeOf(AgentKind.Omp));
        Assert.True(parser.CanEmitShapeOf(AgentKind.Pi));
        Assert.False(parser.CanEmitShapeOf(AgentKind.Claude));
    }

    [Fact]
    public async Task ParseAsync_MapsOmpUsageNames()
    {
        // Recorded real success usage (omp 18.2.2, "PING" reply): the
        // shared ParseUsage only knows input_tokens/prompt_tokens; omp
        // reports usage:{input,output,cacheRead} on the message envelope.
        var parser = new OmpStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"session\",\"version\":3,\"id\":\"s\",\"cwd\":\"/work\"}\n" +
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"input\":19200,\"output\":45,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":19245,\"reasoningTokens\":45}," +
            "\"stopReason\":\"stop\"}}\n" +
            "{\"type\":\"turn_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"},\"toolResults\":[]}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(19200, summary.InputTokens);
        Assert.Equal(45, summary.OutputTokens);
        Assert.Equal(0, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_CacheRead_MapsToCachedBucket()
    {
        // Recorded real cache-active usage (omp 18.2.2, piped-stdin run):
        // cacheRead is the cached-input bucket.
        var parser = new OmpStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"input\":1795,\"output\":53,\"cacheRead\":17408,\"cacheWrite\":0,\"totalTokens\":19256}," +
            "\"stopReason\":\"stop\"}}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(1795, summary.InputTokens);
        Assert.Equal(53, summary.OutputTokens);
        Assert.Equal(17408, summary.CachedInputTokens);
    }

    [Fact]
    public async Task ParseAsync_AllZeroUsageFailure_DoesNotCorruptSummary()
    {
        // Recorded real failure shape (paid model on $0 key): usage is all
        // zeros and there is no assistant text worth surfacing.
        var parser = new OmpStreamParser();
        await using var stream = StreamOf(
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[]," +
            "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0}," +
            "\"stopReason\":\"error\",\"errorMessage\":\"403 Key limit exceeded (total limit).\"}}\n");

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Equal(0, summary.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_EmptyResponse_ParsesWithoutThrowing()
    {
        var parser = new OmpStreamParser();
        await using var stream = StreamOf(string.Empty);

        var summary = await parser.ParseAsync(stream);

        Assert.Equal(0, summary.InputTokens);
        Assert.Empty(summary.ToolCalls);
    }
}

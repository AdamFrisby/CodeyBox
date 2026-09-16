using CodeyBox.Agents;
using CodeyBox.Agents.Goose;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="GooseCostExtractor"/> over recorded real
/// <c>--output-format stream-json</c> output (goose 1.50.1). Goose fronts
/// 30+ providers with unrelated economics, so the extractor ships no
/// fallback rate: unrated output yields null (unknown), never a default
/// that looks like data.
/// </summary>
public sealed class GooseCostExtractorTests
{
    private static readonly GooseCostExtractor Extractor = new();

    // Recorded real per-chunk frame carrying the dispatch model id.
    private const string MessageFrame =
        """{"type":"message","message":{"id":"gen-1789551821-abc","role":"assistant","created":1789551821,"content":[{"type":"thinking","thinking":" reply","signature":""}],"metadata":{"userVisible":true,"agentVisible":true,"inference":{"provider":"openrouter","requestedModel":"nvidia/nemotron-3.5-lightning:free"}}}}""";

    // Recorded real terminal totals frame.
    private const string CompleteFrame =
        """{"type":"complete","total_tokens":5357,"input_tokens":5257,"output_tokens":100,"cache_read_input_tokens":4352,"cache_write_input_tokens":0,"cost_usd":0.0}""";

    [Fact]
    public void Kind_IsGoose()
    {
        Assert.Equal(AgentKind.Goose, Extractor.Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Goose fronts 30+ providers with unrelated per-token economics (and
        // free-tier models at $0): no single fallback rate is honest, so the
        // extractor opts out and unrated output costs $0 with a warning.
        Assert.Null(Extractor.DefaultPricing);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract(null, null));
        Assert.Null(Extractor.TryExtract(string.Empty, string.Empty));
        Assert.Null(Extractor.TryExtract("   \n  ", null));
    }

    [Fact]
    public void CompleteFrame_ParsesInputOutputAndCacheRead()
    {
        var snapshot = Extractor.TryExtract(MessageFrame + "\n" + CompleteFrame, null);

        Assert.NotNull(snapshot);
        Assert.Equal(5257, snapshot!.InputTokens);
        Assert.Equal(4352, snapshot.CachedInputTokens);
        Assert.Equal(100, snapshot.OutputTokens);
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", snapshot.ModelId);
    }

    [Fact]
    public void LatestCompleteFrame_Wins()
    {
        // A retried run emits one totals frame per attempt; the last is the
        // run total.
        const string first =
            """{"type":"complete","total_tokens":100,"input_tokens":80,"output_tokens":20,"cache_read_input_tokens":0,"cache_write_input_tokens":0,"cost_usd":0.0}""";
        var snapshot = Extractor.TryExtract(first + "\n" + CompleteFrame, null);

        Assert.NotNull(snapshot);
        Assert.Equal(5257, snapshot!.InputTokens);
        Assert.Equal(100, snapshot.OutputTokens);
    }

    [Fact]
    public void ZeroTotalsFrame_ReturnsNull()
    {
        // Recorded real failure shape (bad OpenRouter key, exit 0): the
        // terminal frame completes with every counter at 0. That must yield
        // unknown, not a zero-token snapshot that looks like a free run.
        const string stdout =
            """{"type":"message","message":{"id":"msg_1","role":"assistant","created":1789551852,"content":[{"type":"error","kind":"authentication","message":"Authentication failed for https://openrouter.ai/api/v1/chat/completions. Status: 401 Unauthorized."}],"metadata":{"userVisible":true,"agentVisible":false}}}""" + "\n" +
            """{"type":"complete","total_tokens":0,"input_tokens":0,"output_tokens":0,"cache_read_input_tokens":0,"cache_write_input_tokens":0,"cost_usd":0.0}""";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void MessageFramesWithoutComplete_ReturnsNull()
    {
        // Chunks carry no counts; without the terminal totals frame there is
        // nothing attributable — report unknown rather than zeros.
        Assert.Null(Extractor.TryExtract(MessageFrame + "\n" + MessageFrame, null));
    }

    [Fact]
    public void BannerOnly_ReturnsNull()
    {
        const string stdout =
            "__( O)>  ● new session · openrouter nvidia/nemotron-3.5-lightning:free\n" +
            "   \\____)    20260916_6 · /tmp/goosetest\n" +
            "     L L     goose is ready\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void UsageOnStderr_Extracted()
    {
        var snapshot = Extractor.TryExtract(null, CompleteFrame);

        Assert.NotNull(snapshot);
        Assert.Equal(5257, snapshot!.InputTokens);
        Assert.Equal(100, snapshot.OutputTokens);
    }

    [Fact]
    public void NonJsonChatter_Skipped()
    {
        var snapshot = Extractor.TryExtract(
            "goose is thinking…\n" +
            "{not json}\n" +
            MessageFrame + "\n" +
            CompleteFrame + "\n" +
            "interleaved plaintext\n",
            null);

        Assert.NotNull(snapshot);
        Assert.Equal(5257, snapshot!.InputTokens);
    }

    [Fact]
    public void NeverThrows_OnHostileInput()
    {
        // Every hostile shape must return normally with null (unknown cost),
        // never throw and never fabricate a snapshot.
        AgentCostSnapshot? truncated = null;
        AgentCostSnapshot? huge = null;
        AgentCostSnapshot? wrongType = null;
        AgentCostSnapshot? negative = null;
        var ex = Record.Exception(() =>
        {
            truncated = Extractor.TryExtract("{\"type\":\"complete\",\"total_tokens\":", "[[[");
            huge = Extractor.TryExtract(new string('x', 100_000), new string('y', 100_000));
            wrongType = Extractor.TryExtract("{\"type\":\"complete\",\"total_tokens\":\"lots\"}", null);
            negative = Extractor.TryExtract("{\"type\":\"complete\",\"total_tokens\":-5,\"input_tokens\":-5}", null);
        });

        Assert.Null(ex);
        Assert.Null(truncated);
        Assert.Null(huge);
        Assert.Null(wrongType);
        Assert.Null(negative);
    }
}

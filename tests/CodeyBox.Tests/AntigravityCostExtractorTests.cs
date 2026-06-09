using CodeyBox.Agents.Antigravity;

namespace CodeyBox.Tests;

public sealed class AntigravityCostExtractorTests
{
    private static readonly AntigravityCostExtractor Extractor = new();

    [Fact]
    public void NdJson_AnthropicShape_ParsesCacheBuckets()
    {
        // claude-* gateway models speak the Anthropic NDJSON shape.
        var stdout =
            """{"type":"assistant","message":{"model":"claude-opus-4-6-thinking"}}""" + "\n" +
            """{"type":"result","usage":{"input_tokens":12000,"cache_creation_input_tokens":300,"cache_read_input_tokens":4000,"output_tokens":650}}""";

        var snap = Extractor.TryExtract(stdout, null);

        Assert.NotNull(snap);
        Assert.Equal(12000 + 300, snap.InputTokens);
        Assert.Equal(4000, snap.CachedInputTokens);
        Assert.Equal(650, snap.OutputTokens);
        Assert.Equal("claude-opus-4-6-thinking", snap.ModelId);
    }

    [Fact]
    public void NdJson_GeminiShape_ParsesCachedInputTokens()
    {
        // gemini-* gateway models emit the flat cached_input_tokens field.
        var stdout =
            """{"type":"result","model":"gemini-3.5-flash-high","usage":{"prompt_tokens":5000,"cached_input_tokens":1000,"completion_tokens":420}}""";

        var snap = Extractor.TryExtract(stdout, null);

        Assert.NotNull(snap);
        Assert.Equal(5000, snap.InputTokens);
        Assert.Equal(1000, snap.CachedInputTokens);
        Assert.Equal(420, snap.OutputTokens);
        Assert.Equal("gemini-3.5-flash-high", snap.ModelId);
    }

    [Fact]
    public void HumanReadable_FooterFallback_ParsesInputOutputAndCached()
    {
        var stdout = "Total cost: $0.07 (12,345 input tokens, 678 output tokens, 5,000 cached tokens)";

        var snap = Extractor.TryExtract(stdout, null);

        Assert.NotNull(snap);
        // FreshInput strips cached from total, matching the Claude extractor's behaviour.
        Assert.Equal(12_345 - 5_000, snap.InputTokens);
        Assert.Equal(5_000, snap.CachedInputTokens);
        Assert.Equal(678, snap.OutputTokens);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract(null, null));
        Assert.Null(Extractor.TryExtract("", ""));
    }
}

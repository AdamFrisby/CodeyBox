using CodeyBox.Agents.Pi;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PiCostExtractor"/>. Pi reports cumulative
/// provider-reported <c>usage {input, output, cacheRead, …}</c> plus the
/// dispatch model on every assistant message frame; the extractor keeps the
/// latest usage object as the run total.
/// </summary>
public sealed class PiCostExtractorTests
{
    private static readonly PiCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsPi()
    {
        Assert.Equal(AgentKind.Pi, Extractor.Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Pi fronts 30+ providers with unrelated per-token economics; no
        // single fallback rate is honest. Operators configure per-model rates
        // under CodeyBox:AgentPricing (or rely on the bundled pi bucket).
        Assert.Null(Extractor.DefaultPricing);
    }

    [Fact]
    public void NullAndEmpty_ReturnsNull()
    {
        Assert.Null(Extractor.TryExtract(null, null));
        Assert.Null(Extractor.TryExtract("", ""));
        Assert.Null(Extractor.TryExtract("   ", null));
        Assert.Null(Extractor.TryExtract(null, "   "));
    }

    [Fact]
    public void MessageUsage_ParsesInputOutputAndCacheRead()
    {
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"model\":\"claude-haiku-4-5\"," +
            "\"usage\":{\"input\":1200,\"output\":340,\"cacheRead\":5600,\"cacheWrite\":10,\"totalTokens\":7140}}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1200, result!.InputTokens);
        Assert.Equal(340, result.OutputTokens);
        Assert.Equal(5600, result.CachedInputTokens);
        Assert.Equal("claude-haiku-4-5", result.ModelId);
    }

    [Fact]
    public void NestedTurnFrameUsage_ParsedThroughMessageEnvelope()
    {
        const string stdout =
            "{\"type\":\"turn_end\",\"timestamp\":1,\"message\":{\"role\":\"assistant\",\"model\":\"gpt-4o\"," +
            "\"usage\":{\"input\":10,\"output\":5,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":15}}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(10, result!.InputTokens);
        Assert.Equal(5, result.OutputTokens);
        Assert.Equal("gpt-4o", result.ModelId);
    }

    [Fact]
    public void LatestUsageFrame_Wins()
    {
        // Usage is cumulative per session: the last frame is the run total.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"model\":\"m\",\"usage\":{\"input\":100,\"output\":10,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":110}}}\n" +
            "{\"type\":\"agent_end\",\"messages\":[],\"willRetry\":false,\"message\":{\"model\":\"m\",\"usage\":{\"input\":900,\"output\":90,\"cacheRead\":50,\"cacheWrite\":0,\"totalTokens\":1040}}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(900, result!.InputTokens);
        Assert.Equal(90, result.OutputTokens);
        Assert.Equal(50, result.CachedInputTokens);
    }

    [Fact]
    public void ZeroUsageFrames_ReturnNull()
    {
        // Error runs report all-zero usage; there is nothing to attribute.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"model\":\"claude-haiku-4-5\"," +
            "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0},\"stopReason\":\"error\"}}\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void UsageOnStderr_Extracted()
    {
        const string stderr =
            "{\"type\":\"message_end\",\"message\":{\"model\":\"m\",\"usage\":{\"input\":7,\"output\":3,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":10}}}\n";

        var result = Extractor.TryExtract(null, stderr);

        Assert.NotNull(result);
        Assert.Equal(7, result!.InputTokens);
    }

    [Fact]
    public void NonJsonChatter_Skipped()
    {
        const string stdout =
            "some plaintext banner\n" +
            "{\"type\":\"message_end\",\"message\":{\"model\":\"m\",\"usage\":{\"input\":11,\"output\":2,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":13}}}\n" +
            "{\"broken\": \n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(11, result!.InputTokens);
    }

    [Fact]
    public void NeverThrows_OnHostileInput()
    {
        const string stdout = "{\"usage\":[1,2,{\"input\":\"x\"}]}\n{\"usage\":{\"input\":-5,\"output\":null}}\n";

        // Must return normally (null here — no positive counts), never throw.
        Assert.Null(Extractor.TryExtract(stdout, stdout));
    }
}

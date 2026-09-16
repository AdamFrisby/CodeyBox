using CodeyBox.Agents.Prime;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="PrimeCostExtractor"/> over RECORDED prime-agent
/// 0.9.5 output. Prime reports cumulative provider-reported
/// <c>usage {input, output, cacheRead, …}</c> plus the dispatch model on
/// every assistant message frame; the extractor keeps the latest non-zero
/// usage object as the run total, and reports unknown (null) — never a zero
/// that looks like data — when no frame carries usage.
/// </summary>
public sealed class PrimeCostExtractorTests
{
    private static readonly PrimeCostExtractor Extractor = new();

    [Fact]
    public void Kind_IsPrime()
    {
        Assert.Equal(AgentKind.Prime, Extractor.Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull()
    {
        // Prime fronts many providers with unrelated per-token economics; no
        // single fallback rate is honest. Operators configure per-model rates
        // under CodeyBox:AgentPricing (or rely on the bundled prime bucket).
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
    public void RecordedSuccessRun_ParsesInputOutputCacheReadAndModel()
    {
        // Live frame from the prime-agent 0.9.5 OpenRouter success run.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"provider\":\"openrouter\"," +
            "\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"input\":1185,\"output\":104,\"cacheRead\":4352,\"cacheWrite\":0,\"totalTokens\":5641," +
            "\"cost\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"total\":0}}," +
            "\"stopReason\":\"stop\"}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(1185, result!.InputTokens);
        Assert.Equal(104, result.OutputTokens);
        Assert.Equal(4352, result.CachedInputTokens);
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", result.ModelId);
    }

    [Fact]
    public void RecordedErrorRun_ReturnsNull()
    {
        // Live frame from the bad-key failure run: all-zero usage with
        // stopReason error — nothing to attribute.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"provider\":\"openrouter\"," +
            "\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0}," +
            "\"stopReason\":\"error\",\"errorMessage\":\"401 User not found.\"}}\n";

        Assert.Null(Extractor.TryExtract(stdout, null));
    }

    [Fact]
    public void StreamWithoutUsage_ReturnsNullNeverZero()
    {
        // A run whose stream carries no usage frame at all (missing CLI,
        // killed process, plaintext-only failure): unknown, not zero.
        const string stdout =
            "{\"type\":\"session\",\"version\":3,\"cwd\":\"/work\"}\n" +
            "{\"type\":\"agent_end\",\"messages\":[]}\n";

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
    public void LatestUsageFrame_Wins()
    {
        // Usage is cumulative per session: the last non-zero frame is the
        // run total.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"model\":\"m\",\"usage\":{\"input\":100,\"output\":10,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":110}}}\n" +
            "{\"type\":\"agent_end\",\"messages\":[],\"message\":{\"model\":\"m\",\"usage\":{\"input\":900,\"output\":90,\"cacheRead\":50,\"cacheWrite\":0,\"totalTokens\":1040}}}\n";

        var result = Extractor.TryExtract(stdout, null);

        Assert.NotNull(result);
        Assert.Equal(900, result!.InputTokens);
        Assert.Equal(90, result.OutputTokens);
        Assert.Equal(50, result.CachedInputTokens);
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

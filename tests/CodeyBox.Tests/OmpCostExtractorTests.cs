using CodeyBox.Agents.Omp;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="OmpCostExtractor"/>: usage + model extraction over
/// recorded real omp 18.2.2 output, the all-zero/empty unknown paths, and
/// the no-fallback-rate posture (an agent with no readable single rate must
/// report unknown rather than a default that looks like data).
/// </summary>
public sealed class OmpCostExtractorTests
{
    [Fact]
    public void Kind_IsOmp()
    {
        Assert.Equal(AgentKind.Omp, new OmpCostExtractor().Kind);
    }

    [Fact]
    public void DefaultPricing_IsNull_UnknownRatherThanFabricated()
    {
        // OMP fronts ~60 providers with unrelated per-token economics: no
        // single fallback rate is honest, so the extractor reports unknown
        // and unrated models cost $0 with a startup warning.
        Assert.Null(new OmpCostExtractor().DefaultPricing);
    }

    [Fact]
    public void TryExtract_SuccessRun_ReturnsUsageAndBareModelId()
    {
        // Recorded real success frame (omp 18.2.2): the stream reports the
        // bare provider-catalog id even when --model carried the
        // openrouter/ qualifier.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"input\":19200,\"output\":45,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":19245,\"reasoningTokens\":45}," +
            "\"stopReason\":\"stop\"}}\n" +
            "{\"type\":\"turn_end\",\"message\":{\"role\":\"assistant\",\"stopReason\":\"stop\"},\"toolResults\":[]}\n";

        var snapshot = new OmpCostExtractor().TryExtract(stdout, agentStderr: string.Empty);

        Assert.NotNull(snapshot);
        Assert.Equal(19200, snapshot.InputTokens);
        Assert.Equal(0, snapshot.CachedInputTokens);
        Assert.Equal(45, snapshot.OutputTokens);
        Assert.Equal("nvidia/nemotron-3.5-lightning:free", snapshot.ModelId);
    }

    [Fact]
    public void TryExtract_CacheActiveRun_SeparatesCachedBucket()
    {
        // Recorded real cache-active usage (omp 18.2.2, piped-stdin run).
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"model\":\"nvidia/nemotron-3.5-lightning:free\"," +
            "\"usage\":{\"input\":1795,\"output\":53,\"cacheRead\":17408,\"cacheWrite\":0,\"totalTokens\":19256}," +
            "\"stopReason\":\"stop\"}}\n";

        var snapshot = new OmpCostExtractor().TryExtract(stdout, agentStderr: null);

        Assert.NotNull(snapshot);
        Assert.Equal(1795, snapshot.InputTokens);
        Assert.Equal(17408, snapshot.CachedInputTokens);
        Assert.Equal(53, snapshot.OutputTokens);
    }

    [Fact]
    public void TryExtract_QuotaFailureAllZeroUsage_ReturnsNull()
    {
        // Recorded real failure shape (paid model on $0 key): usage frames
        // exist but every counter is zero — null is unknown, never a zero
        // that looks like data.
        const string stdout =
            "{\"type\":\"message_end\",\"message\":{\"role\":\"assistant\",\"content\":[]," +
            "\"usage\":{\"input\":0,\"output\":0,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":0}," +
            "\"stopReason\":\"error\",\"errorMessage\":\"403 Key limit exceeded (total limit).\"}}\n";

        Assert.Null(new OmpCostExtractor().TryExtract(stdout, agentStderr: string.Empty));
    }

    [Fact]
    public void TryExtract_EmptyResponse_ReturnsNull()
    {
        Assert.Null(new OmpCostExtractor().TryExtract(agentStdout: string.Empty, agentStderr: string.Empty));
        Assert.Null(new OmpCostExtractor().TryExtract(agentStdout: null, agentStderr: null));
    }

    [Fact]
    public void TryExtract_MalformedLines_NeverThrows()
    {
        const string stdout = "not json\n{\"type\":\"message_end\", truncated\n";

        Assert.Null(new OmpCostExtractor().TryExtract(stdout, agentStderr: null));
    }
}

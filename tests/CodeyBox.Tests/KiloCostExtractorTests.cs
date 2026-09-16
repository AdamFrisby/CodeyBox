using System.Reflection;
using CodeyBox.Agents.Kilo;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="KiloCostExtractor"/>: the step-finish token mapping
/// (fresh <c>input</c>, cached <c>cache.read</c>, <c>output</c>) over recorded
/// real @kilocode/cli 7.7.2 output, and the unknown-not-zero contract — when
/// no usage frame is present the extractor returns null (no cost row) rather
/// than a zero snapshot that looks like measured data. The stream carries no
/// model id, so snapshots record a null model and attribution falls through
/// to the AgentDefaults-derived rate; <c>DefaultPricing</c> stays null
/// because no single fallback rate is honest across the hundreds of models
/// kilo fronts.
/// </summary>
public sealed class KiloCostExtractorTests
{
    // Recorded real step-finish frame (token counts verbatim, ids shortened):
    // total (12655) == input (1724) + cache.read (10880) + output (2) +
    // reasoning (49), so input is fresh input.
    private const string StepFinishLine =
        "{\"type\":\"step_finish\",\"timestamp\":1789585720968,\"sessionID\":\"ses_abc\"," +
        "\"part\":{\"id\":\"p3\",\"sessionID\":\"ses_abc\",\"messageID\":\"m1\",\"type\":\"step-finish\"," +
        "\"reason\":\"stop\",\"cost\":0," +
        "\"tokens\":{\"total\":12655,\"input\":1724,\"output\":2,\"reasoning\":49,\"cache\":{\"read\":10880,\"write\":0}}}}";

    private const string AuthErrorLine =
        "{\"type\":\"error\",\"timestamp\":1789585744519,\"sessionID\":\"ses_def\"," +
        "\"error\":{\"name\":\"APIError\",\"data\":{\"message\":\"No cookie auth credentials found\",\"statusCode\":401}}}";

    [Fact]
    public void Kind_IsKilo()
    {
        Assert.Equal(AgentKind.Kilo, new KiloCostExtractor().Kind);
    }

    [Fact]
    public void TryExtract_StepFinishFrame_MapsTokenBuckets()
    {
        var stdout =
            "{\"type\":\"step_start\",\"sessionID\":\"ses_abc\",\"part\":{\"sessionID\":\"ses_abc\",\"messageID\":\"m1\",\"type\":\"step-start\"}}\n" +
            StepFinishLine;

        var snapshot = new KiloCostExtractor().TryExtract(stdout, agentStderr: null);

        Assert.NotNull(snapshot);
        Assert.Equal(1724, snapshot.InputTokens);
        Assert.Equal(10880, snapshot.CachedInputTokens);
        Assert.Equal(2, snapshot.OutputTokens);
        // The stream carries no model id — null (resolved via the
        // AgentDefaults-derived rate), never a guessed default.
        Assert.Null(snapshot.ModelId);
    }

    [Fact]
    public void TryExtract_LatestUsageFrameWins()
    {
        // Usage is cumulative per session, so the last frame is the run total.
        var earlier = StepFinishLine.Replace("\"input\":1724", "\"input\":900").Replace("\"output\":2", "\"output\":1");
        var stdout = earlier + "\n" + StepFinishLine;

        var snapshot = new KiloCostExtractor().TryExtract(stdout, agentStderr: null);

        Assert.NotNull(snapshot);
        Assert.Equal(1724, snapshot.InputTokens);
        Assert.Equal(2, snapshot.OutputTokens);
    }

    [Fact]
    public void TryExtract_FailureOutput_ReturnsNull_NotZero()
    {
        // A failed run carries no usage frame. Unknown (null, no cost row) —
        // not a zero snapshot that would read as a measured $0 run.
        var snapshot = new KiloCostExtractor().TryExtract(AuthErrorLine, agentStderr: "Error: No cookie auth credentials found");

        Assert.Null(snapshot);
    }

    [Fact]
    public void TryExtract_EmptyOutput_ReturnsNull_NotZero()
    {
        Assert.Null(new KiloCostExtractor().TryExtract(null, null));
        Assert.Null(new KiloCostExtractor().TryExtract(string.Empty, "  "));
        Assert.Null(new KiloCostExtractor().TryExtract("KILO_SMOKE_OK\n", null));
    }

    [Fact]
    public void TryExtract_MalformedLines_SkippedNeverThrows()
    {
        var stdout = "not json at all\n{\"type\":\"step_finish\",\"part\":\n" + StepFinishLine + "\n";

        var snapshot = new KiloCostExtractor().TryExtract(stdout, agentStderr: null);

        Assert.NotNull(snapshot);
        Assert.Equal(1724, snapshot.InputTokens);
    }

    [Fact]
    public void DefaultPricing_IsNull_NoFabricatedFallbackRate()
    {
        // Kilo fronts hundreds of models with unrelated per-token economics;
        // a built-in fallback would book a cost that was never measured. The
        // shipped free-tier member prices through the explicit zero-rate
        // bucket instead.
        Assert.Null(new KiloCostExtractor().DefaultPricing);
    }

    [Fact]
    public void KiloAssembly_ShipsNoQuotaProbe_UnknownRatherThanFabricated()
    {
        // kilo stats is local history, not a quota balance — there is no
        // meterable quota endpoint, so the adapter must not ship a probe
        // that fabricates one. Members fall through to the NullQuotaProbe
        // unknown path and the router gates on observed failures.
        var probeTypes = typeof(KiloCostExtractor).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IAgentQuotaProbe).IsAssignableFrom(t))
            .Select(t => t.FullName)
            .ToArray();

        Assert.Empty(probeTypes);
    }
}

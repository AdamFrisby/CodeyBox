using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the cost-row → usage-summary reducer that backs the
/// per-iteration / cumulative split on the API and webhook surfaces.
/// </summary>
public sealed class WorkItemUsageAggregatorTests
{
    private static WorkItemCost MakeCost(
        string phase,
        int? iteration,
        int input,
        int output,
        int cached,
        double usd,
        double elapsedSeconds)
    {
        var ended = DateTimeOffset.UtcNow;
        return new WorkItemCost
        {
            Id = Guid.NewGuid().ToString("N"),
            WorkItemId = "wi",
            Phase = phase,
            Iteration = iteration,
            AgentKind = "claude",
            ModelId = "claude-opus-4-7",
            InputTokens = input,
            CachedInputTokens = cached,
            OutputTokens = output,
            EstimatedUsd = usd,
            StartedAt = ended.AddSeconds(-elapsedSeconds),
            EndedAt = ended,
        };
    }

    [Fact]
    public void Summarise_NoRows_ReturnsNull()
    {
        Assert.Null(WorkItemUsageAggregator.Summarise([]));
    }

    [Fact]
    public void Summarise_SingleWorkRow_Bucketed_AsIteration1()
    {
        var rows = new[] { MakeCost("work", null, 1000, 200, 0, 0.05, 1.5) };

        var summary = WorkItemUsageAggregator.Summarise(rows)!;

        Assert.Equal(1, summary.Iteration.Iteration);
        Assert.Equal(1000, summary.Iteration.TokensInput);
        Assert.Equal(200, summary.Iteration.TokensOutput);
        Assert.Equal(0, summary.Iteration.TokensCached);
        Assert.Equal(0, summary.Iteration.TokensReasoning);
        Assert.Equal(0.05, summary.Iteration.CostUsd);
        Assert.Equal(1500, summary.Iteration.ElapsedMs);

        Assert.Equal(1000, summary.Total.TokensInput);
        Assert.Equal(200, summary.Total.TokensOutput);
        Assert.Equal(0.05, summary.Total.CostUsd);
        Assert.Equal(1500, summary.Total.ElapsedMs);
    }

    [Fact]
    public void Summarise_MultipleIterations_TotalSumsAcrossEverything_PerIterationIsLatestDelta()
    {
        var rows = new[]
        {
            MakeCost("work",   null, 5000,  500,    0, 0.10, 4.0),  // iter 1
            MakeCost("audit",  1,    2000,  100,    0, 0.04, 2.0),  // iter 1
            MakeCost("rework", 2,    8000,  900,  500, 0.20, 6.0),  // iter 2
            MakeCost("audit",  2,    1500,   80,    0, 0.03, 1.5),  // iter 2
            MakeCost("merge",  null,  100,   10,    0, 0.001, 0.5), // folded into iter 2
        };

        var summary = WorkItemUsageAggregator.Summarise(rows)!;

        Assert.Equal(2, summary.Iteration.Iteration);
        // iter 2 = rework(2) + audit(2) + merge(folded)
        Assert.Equal(8000 + 1500 + 100, summary.Iteration.TokensInput);
        Assert.Equal(900 + 80 + 10, summary.Iteration.TokensOutput);
        Assert.Equal(500, summary.Iteration.TokensCached);
        Assert.Equal(0.231, summary.Iteration.CostUsd, precision: 4);
        Assert.Equal((long)((6.0 + 1.5 + 0.5) * 1000), summary.Iteration.ElapsedMs);

        // total = every row
        Assert.Equal(5000 + 2000 + 8000 + 1500 + 100, summary.Total.TokensInput);
        Assert.Equal(500 + 100 + 900 + 80 + 10, summary.Total.TokensOutput);
        Assert.Equal(500, summary.Total.TokensCached);
        Assert.Equal(0.371, summary.Total.CostUsd, precision: 4);
    }

    [Fact]
    public void Summarise_MergePhase_IsCaseInsensitive_FoldsIntoLatestIteration()
    {
        // Defence-in-depth: the comparison in BucketIteration is OrdinalIgnoreCase
        // so callers writing "Merge" or "MERGE" still fold into the latest
        // iteration's delta. Pinning this prevents a regression to ordinal.
        var rows = new[]
        {
            MakeCost("audit", 1, 1000, 100, 0, 0.05, 1.0),
            MakeCost("MERGE", null,  500,  50, 0, 0.01, 0.5),
        };

        var summary = WorkItemUsageAggregator.Summarise(rows)!;

        // MERGE row must be in iter 1's delta, not double-counted or dropped.
        Assert.Equal(1000 + 500, summary.Iteration.TokensInput);
        Assert.Equal(100 + 50, summary.Iteration.TokensOutput);
    }

    [Fact]
    public void Summarise_NegativeElapsed_IsClampedToZero()
    {
        // EndedAt < StartedAt would otherwise produce a negative elapsed_ms.
        // The reducer clamps via Math.Max(0, …) to defend against clock-skew
        // rows landing in the cost store. Removing the clamp must not pass.
        var ended = DateTimeOffset.UtcNow;
        var rows = new[]
        {
            new WorkItemCost
            {
                Id = "skewed",
                WorkItemId = "wi",
                Phase = "work",
                AgentKind = "claude",
                InputTokens = 1,
                OutputTokens = 1,
                EstimatedUsd = 0.01,
                // Started AFTER Ended → negative span; the aggregator must
                // clamp to 0 so totals/iteration ms stay non-negative.
                StartedAt = ended.AddSeconds(5),
                EndedAt = ended,
            },
        };

        var summary = WorkItemUsageAggregator.Summarise(rows)!;

        Assert.Equal(0, summary.Iteration.ElapsedMs);
        Assert.Equal(0, summary.Total.ElapsedMs);
    }

    [Fact]
    public void Summarise_RoundsCostToFourDecimals()
    {
        // Sum of these values = 0.123456789 — should round to 0.1235.
        var rows = new[]
        {
            MakeCost("work", null, 1, 1, 0, 0.0617283945, 0.001),
            MakeCost("work", null, 1, 1, 0, 0.0617283945, 0.001),
        };

        var summary = WorkItemUsageAggregator.Summarise(rows)!;

        Assert.Equal(0.1235, summary.Total.CostUsd);
        Assert.Equal(0.1235, summary.Iteration.CostUsd);
    }
}

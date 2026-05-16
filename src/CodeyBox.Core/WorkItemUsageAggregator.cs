namespace CodeyBox.Core;

/// <summary>
/// Pure reducer that turns a list of <see cref="WorkItemCost"/> rows into the
/// per-iteration delta + cumulative total used by API and webhook payloads.
/// </summary>
public static class WorkItemUsageAggregator
{
    /// <summary>
    /// Returns null when <paramref name="rows"/> is empty (cost data unavailable
    /// for this work item — caller should omit the usage block).
    /// </summary>
    public static WorkItemUsageSummary? Summarise(IReadOnlyList<WorkItemCost> rows)
    {
        if (rows.Count == 0) return null;

        // Bucket every row to an iteration number. Audit/rework rows carry an
        // explicit iteration; null-iteration rows fall back to:
        //   - 'merge' phase → folded into the highest iteration (the merge happens
        //     at the very end of the run, so its cost belongs to the most recent
        //     iteration's delta from the operator's perspective);
        //   - everything else (notably the initial 'work' phase) → iteration 1.
        var maxExplicit = 1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Iteration is { } iter && iter > maxExplicit) maxExplicit = iter;
        }

        int totalIn = 0, totalOut = 0, totalReason = 0, totalCached = 0;
        int iterIn = 0, iterOut = 0, iterReason = 0, iterCached = 0;
        double totalUsd = 0.0, iterUsd = 0.0;
        long totalElapsed = 0, iterElapsed = 0;

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var elapsedMs = (long)Math.Max(0, (r.EndedAt - r.StartedAt).TotalMilliseconds);

            totalIn += r.InputTokens;
            totalOut += r.OutputTokens;
            totalCached += r.CachedInputTokens;
            totalUsd += r.EstimatedUsd;
            totalElapsed += elapsedMs;
            // totalReason left at 0 — reasoning tokens are not currently captured
            // by any IAgentCostExtractor implementation; field kept on the wire for
            // forward compatibility per the surface spec.

            var bucket = BucketIteration(r, maxExplicit);
            if (bucket == maxExplicit)
            {
                iterIn += r.InputTokens;
                iterOut += r.OutputTokens;
                iterCached += r.CachedInputTokens;
                iterUsd += r.EstimatedUsd;
                iterElapsed += elapsedMs;
            }
        }

        var iteration = new WorkItemIterationUsage(
            Iteration: maxExplicit,
            TokensInput: iterIn,
            TokensOutput: iterOut,
            TokensReasoning: iterReason,
            TokensCached: iterCached,
            CostUsd: Round4(iterUsd),
            ElapsedMs: iterElapsed);

        var total = new WorkItemUsageTotal(
            TokensInput: totalIn,
            TokensOutput: totalOut,
            TokensReasoning: totalReason,
            TokensCached: totalCached,
            CostUsd: Round4(totalUsd),
            ElapsedMs: totalElapsed);

        return new WorkItemUsageSummary(iteration, total);
    }

    private static int BucketIteration(WorkItemCost row, int maxExplicit)
    {
        if (row.Iteration is { } iter) return iter;
        if (row.Phase.Equals("merge", StringComparison.OrdinalIgnoreCase))
            return maxExplicit;
        return 1;
    }

    private static double Round4(double value) =>
        Math.Round(value, 4, MidpointRounding.AwayFromZero);
}

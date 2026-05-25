namespace CodeyBox.Core;

/// <summary>
/// Durable store for per-agent-invocation cost records.
/// Writes are best-effort: callers must never let a store failure abort a pipeline phase.
/// </summary>
public interface IWorkItemCostStore
{
    /// <summary>Persists a single cost record. Throws on storage failure (caller wraps).</summary>
    Task RecordAsync(WorkItemCost cost, CancellationToken ct = default);

    /// <summary>Returns all cost records for a work item, ordered by started_at.</summary>
    Task<IReadOnlyList<WorkItemCost>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns all cost records for work items belonging to <paramref name="projectId"/>
    /// whose <c>started_at</c> falls within [<paramref name="from"/>, <paramref name="to"/>).
    /// Joins with the work_items table internally.
    /// </summary>
    Task<IReadOnlyList<WorkItemCost>> GetByProjectAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>
    /// Returns cost records for the most recent <paramref name="maxItems"/> work
    /// items in <paramref name="projectId"/> whose cost rows fall within
    /// [<paramref name="from"/>, <paramref name="to"/>). Optional agent/model
    /// filters are applied before selecting the recent work-item set, so callers
    /// can build bounded per-agent projections without scanning every project row.
    /// </summary>
    Task<IReadOnlyList<WorkItemCost>> GetRecentByProjectAsync(
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        string? agentKind,
        string? modelId,
        int maxItems,
        CancellationToken ct = default);

    /// <summary>
    /// Fleet aggregation: returns per-project cost totals for cost records whose
    /// <c>started_at</c> falls within [<paramref name="from"/>, <paramref name="to"/>).
    /// Returns one row per project that has any matching records. Used by GET /fleet/summary
    /// to avoid per-project N+1 queries.
    /// </summary>
    Task<IReadOnlyList<(string ProjectId, double TotalUsd)>> GetFleetCostSummaryAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>Deletes all cost records for a work item (cascade with parent item deletion).</summary>
    Task DeleteByWorkItemAsync(string workItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns the SUM of estimated_usd for all cost records belonging to work items
    /// in <paramref name="projectId"/> whose started_at falls within
    /// [<paramref name="from"/>, <paramref name="to"/>). Uses a single aggregation
    /// query against the indexed (work_item_id, started_at) column; safe to call
    /// frequently.
    /// </summary>
    Task<decimal> SumEstimatedUsdAsync(
        string projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);

    /// <summary>
    /// Aggregates the cost rows for <paramref name="workItemId"/> into a per-iteration
    /// delta (the most recent iteration's contribution) and a cumulative total across
    /// every iteration. Returns null when no cost rows exist for the work item — the
    /// API and webhook layers treat this as "usage unknown" and omit the block.
    ///
    /// Default implementation reads via <see cref="GetByWorkItemAsync"/> and reduces
    /// in memory; override for stores that can compute the aggregation server-side.
    /// </summary>
    async Task<WorkItemUsageSummary?> SummariseAsync(string workItemId, CancellationToken ct = default)
    {
        var rows = await GetByWorkItemAsync(workItemId, ct);
        return WorkItemUsageAggregator.Summarise(rows);
    }

    /// <summary>
    /// Batched summarisation: returns one entry per <paramref name="workItemIds"/>
    /// member that has cost rows (missing entries → "usage unknown" at the call site).
    /// The default implementation falls back to per-item <see cref="GetByWorkItemAsync"/>
    /// calls; SQLite-backed stores override with a single SELECT … WHERE IN (...) to
    /// avoid N+1 round-trips on the list endpoint.
    /// </summary>
    async Task<IReadOnlyDictionary<string, WorkItemUsageSummary>> SummariseManyAsync(
        IReadOnlyCollection<string> workItemIds, CancellationToken ct = default)
    {
        var results = new Dictionary<string, WorkItemUsageSummary>(workItemIds.Count, StringComparer.Ordinal);
        foreach (var id in workItemIds)
        {
            var summary = await SummariseAsync(id, ct);
            if (summary is not null) results[id] = summary;
        }
        return results;
    }
}

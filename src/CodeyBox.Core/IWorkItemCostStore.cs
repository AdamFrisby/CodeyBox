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
}

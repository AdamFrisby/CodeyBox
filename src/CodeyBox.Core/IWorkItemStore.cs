namespace CodeyBox.Core;

/// <summary>
/// Durable store of work items. Survives orchestrator restart so in-flight
/// items can be recovered and replayed.
/// </summary>
public interface IWorkItemStore
{
    Task CreateAsync(WorkItem item, CancellationToken ct = default);
    Task UpdateAsync(WorkItem item, CancellationToken ct = default);
    /// <summary>Updates the item only when its persisted state still matches <paramref name="onlyIfState"/>. Returns true if the row was updated.</summary>
    Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default);
    Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default);
    IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default);
    IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>queue_position</c> for the listed Queued items in the given order
    /// (index + 1 becomes the position). Only rows still in <c>Queued</c> state
    /// are touched; items that raced to a non-Queued state are silently skipped.
    /// Runs inside a single transaction for atomicity.
    /// </summary>
    Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default);

    /// <summary>
    /// Count of work items for <paramref name="projectId"/> whose
    /// <c>started_at</c> timestamp falls within [<paramref name="since"/>, now].
    /// Used for per-project hourly / daily rate-limit checks.
    /// Hits the index on (project_id, started_at).
    /// </summary>
    Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default);

    /// <summary>
    /// Count of work items for <paramref name="projectId"/> that are in a
    /// non-terminal, non-Queued state (i.e. actively running). Used for
    /// MaxConcurrentForProject enforcement.
    /// </summary>
    Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default);

    /// <summary>
    /// Look up a work item by its caller-supplied external ID within a project.
    /// Returns null when no matching item exists.
    /// </summary>
    Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default);

    /// <summary>
    /// Fleet aggregation: returns (project_id, state, count, max_updated_at) rows produced by
    /// SELECT project_id, state, COUNT(*), MAX(updated_at) FROM work_items GROUP BY project_id, state.
    /// Hits the composite index on (project_id, state). No work item bodies are loaded.
    /// </summary>
    Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Fleet aggregation: returns the most-recent <paramref name="perProject"/> terminal work item states
    /// per project, newest-first. Uses ROW_NUMBER() OVER (PARTITION BY project_id ORDER BY updated_at DESC).
    /// Terminal states: Done, Failed, AuditFailed, Cancelled.
    /// </summary>
    Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default);
}

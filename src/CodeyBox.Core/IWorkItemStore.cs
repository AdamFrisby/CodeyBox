namespace CodeyBox.Core;

/// <summary>
/// Outcome of <see cref="IWorkItemStore.UpdatePriorityAsync"/>.
/// </summary>
public enum PriorityUpdateOutcome
{
    /// <summary>The row was updated and the new priority is persisted.</summary>
    Updated,
    /// <summary>The row no longer exists.</summary>
    NotFound,
    /// <summary>The row exists but is in a terminal state; no write was issued.</summary>
    TerminalState,
}

/// <summary>
/// Result returned by <see cref="IWorkItemStore.UpdatePriorityAsync"/>.
/// <see cref="Item"/> is populated on <see cref="PriorityUpdateOutcome.Updated"/>
/// and on <see cref="PriorityUpdateOutcome.TerminalState"/> (so callers can
/// surface the current state to the client); null on <see cref="PriorityUpdateOutcome.NotFound"/>.
/// </summary>
public readonly record struct PriorityUpdateResult(PriorityUpdateOutcome Outcome, WorkItem? Item, int? OldPriority);

/// <summary>
/// Durable store of work items. Survives orchestrator restart so in-flight
/// items can be recovered and replayed.
/// </summary>
public interface IWorkItemStore
{
    Task CreateAsync(WorkItem item, CancellationToken ct = default);
    /// <summary>
    /// Updates persisted work-item fields except <see cref="WorkItem.Priority"/>.
    /// Use <see cref="UpdatePriorityAsync"/> for priority changes so worker writes
    /// from stale snapshots cannot revert a concurrent PATCH /priority update.
    /// </summary>
    Task UpdateAsync(WorkItem item, CancellationToken ct = default);
    /// <summary>
    /// Updates persisted work-item fields except <see cref="WorkItem.Priority"/>
    /// only when the persisted state still matches <paramref name="onlyIfState"/>.
    /// Returns true if the row was updated.
    /// </summary>
    Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default);

    /// <summary>
    /// Partial UPDATE that touches only the <c>priority</c> and <c>updated_at</c>
    /// columns for the row identified by <paramref name="id"/>. Avoids the TOCTOU
    /// race that a full-row <see cref="UpdateAsync"/> introduces when a concurrent
    /// worker transitions the item out of Queued between caller read and write
    /// (a full-row write would stomp <c>state</c>, <c>started_at</c>, etc).
    /// Returns the persisted item after the write, or null if the row no longer
    /// exists, or a tuple flag indicating the row was in a terminal state and was
    /// not modified.
    /// </summary>
    Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default);
    Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default);
    IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default);
    IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of work items currently persisted in
    /// <paramref name="state"/> without loading the rows.
    /// </summary>
    Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>queue_position</c> for the listed Queued items in the given order
    /// (index + 1 becomes the position). Only rows still in <c>Queued</c> state
    /// are touched; items that raced to a non-Queued state are silently skipped.
    /// Runs inside a single transaction for atomicity.
    /// </summary>
    Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default);

    /// <summary>
    /// Returns dispatch-eligible items (Queued plus the mid-pipeline resumable states
    /// produced by recovery: Working with a preempt checkpoint, WorkComplete,
    /// AuditPassed, Merged, etc.) ordered by <c>priority DESC, created_at ASC</c>,
    /// skipping any IDs in <paramref name="skipIds"/> (active or deferred work the
    /// caller is tracking). The dispatch loop streams this enumerator until it finds
    /// an item whose dependencies are satisfied, so it stops reading early in the
    /// common case. Terminal states and <c>NeedsOperatorInput</c> are excluded.
    /// </summary>
    IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default);

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
    /// Terminal states: Done, Failed, AuditFailed, MergeConflictResolutionFailed, Cancelled.
    /// </summary>
    Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default);

    /// <summary>
    /// Fleet aggregation: returns per-project pause states read from the
    /// <c>project_queue_state</c> table (added by the budget-alerts work item).
    /// Returns an empty dictionary when the table does not yet exist so callers
    /// can treat every project as un-paused without crashing.
    /// </summary>
    Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all work items whose <c>replay_of_work_item_id</c> matches
    /// <paramref name="sourceId"/>, in creation order (oldest first).
    /// </summary>
    IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default);

    /// <summary>
    /// Clears <c>replay_of_work_item_id</c> for every work item that was a replay of
    /// <paramref name="sourceId"/>. Called when the source is cancelled so replays
    /// become orphaned but keep running.
    /// </summary>
    Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default);

    /// <summary>
    /// List all work items linked to the given release. Used by the release
    /// state machine to check whether all items have reached a terminal state.
    /// </summary>
    IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default);
}

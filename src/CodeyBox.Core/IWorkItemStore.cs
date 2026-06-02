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
/// Outcome of <see cref="IWorkItemStore.TryReplacePromptAsync"/>. <see cref="NewRevision"/>
/// is populated on <see cref="PromptReplaceOutcome.Updated"/>; null otherwise.
/// </summary>
public readonly record struct PromptReplaceResult(PromptReplaceOutcome Outcome, int? NewRevision);

public enum PromptReplaceOutcome { Updated, NotFound, TerminalState }

/// <summary>
/// Outcome of <see cref="IWorkItemStore.UpdateDependsOnAsync"/>.
/// </summary>
public enum DependsOnUpdateOutcome
{
    /// <summary>The row was updated and the new dependency set is persisted.</summary>
    Updated,
    /// <summary>The row no longer exists.</summary>
    NotFound,
    /// <summary>The row exists but is in a terminal state; no write was issued.</summary>
    TerminalState,
}

/// <summary>
/// Result returned by <see cref="IWorkItemStore.UpdateDependsOnAsync"/>.
/// <see cref="Item"/> is populated on <see cref="DependsOnUpdateOutcome.Updated"/>
/// and on <see cref="DependsOnUpdateOutcome.TerminalState"/> (so callers can
/// surface the current state to the client); null on <see cref="DependsOnUpdateOutcome.NotFound"/>.
/// <see cref="OldDependsOn"/> is the pre-update dependency list, captured so
/// the caller can emit a meaningful audit-log entry without re-reading the row.
/// </summary>
public readonly record struct DependsOnUpdateResult(
    DependsOnUpdateOutcome Outcome,
    WorkItem? Item,
    IReadOnlyList<WorkItemId>? OldDependsOn);

/// <summary>
/// Snapshot of a single dispatched iteration. <see cref="PromptRevisionAtDispatch"/>
/// is the value of <see cref="WorkItem.PromptRevision"/> at the moment the iteration
/// was handed to the agent; the orchestrator compares it against the trailer on the
/// agent's commit to detect "agent finished against a stale prompt".
/// </summary>
public sealed record WorkItemIteration(
    WorkItemId WorkItemId,
    int Iteration,
    int PromptRevisionAtDispatch,
    DateTimeOffset DispatchedAt);

/// <summary>
/// Durable store of work items. Survives orchestrator restart so in-flight
/// items can be recovered and replayed.
/// </summary>
public interface IWorkItemStore
{
    Task CreateAsync(WorkItem item, CancellationToken ct = default);
    /// <summary>
    /// Updates persisted work-item fields except <see cref="WorkItem.Priority"/>,
    /// <see cref="WorkItem.Prompt"/>, and <see cref="WorkItem.PromptRevision"/>.
    /// Use <see cref="UpdatePriorityAsync"/> for priority changes and
    /// <see cref="TryReplacePromptAsync"/> for prompt changes so worker writes
    /// from stale in-memory snapshots cannot revert a concurrent PATCH /priority
    /// or PUT /workitems/{id}/prompt update.
    /// </summary>
    Task UpdateAsync(WorkItem item, CancellationToken ct = default);
    /// <summary>
    /// Updates persisted work-item fields except <see cref="WorkItem.Priority"/>,
    /// <see cref="WorkItem.Prompt"/>, and <see cref="WorkItem.PromptRevision"/>
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

    /// <summary>
    /// Partial UPDATE that touches only the <c>depends_on_json</c> and
    /// <c>updated_at</c> columns for the row identified by <paramref name="id"/>.
    /// Used by PATCH /workitems/{id} when an operator edits the dependency set
    /// post-hoc — the full-row <see cref="UpdateAsync"/> would otherwise stomp
    /// <c>state</c>, <c>started_at</c>, and friends when applied to an
    /// in-flight item (Working/Auditing/etc).
    ///
    /// Returns <see cref="DependsOnUpdateOutcome.TerminalState"/> when the row
    /// is in a terminal state — dependency edits are meaningless once an item
    /// is Done/Cancelled/Failed.
    /// </summary>
    Task<DependsOnUpdateResult> UpdateDependsOnAsync(
        WorkItemId id,
        IReadOnlyList<WorkItemId> dependsOn,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);
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
    /// AuditPassed, Merged, etc.) ordered with post-audit finishing phases before
    /// fresh queued work, then by <c>priority DESC, created_at ASC</c> within each
    /// phase bucket. Skips any IDs in <paramref name="skipIds"/> (active or deferred
    /// work the caller is tracking). Implementations may buffer candidates to hydrate
    /// related data before yielding; callers must not rely on partial reads avoiding
    /// the cost of finding the eligible set. Terminal states plus parked
    /// <c>NeedsOperatorInput</c> and <c>WaitingForQuotaReset</c> rows are excluded.
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
    /// Look up a work item by a bare external-ID value within a project. Matches
    /// across every namespace in <see cref="WorkItem.ExternalIds"/>. Returns
    /// null when no matching item exists. Throws
    /// <see cref="AmbiguousExternalIdException"/> when the value matches in
    /// more than one namespace within the project — callers must disambiguate
    /// using <see cref="GetByNamespacedExternalIdAsync"/>.
    /// </summary>
    Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default);

    /// <summary>
    /// Look up a work item by an explicit (namespace, value) pair within a
    /// project. Returns null when no matching item exists. Unlike the bare
    /// lookup this is always unambiguous because <c>(projectId, namespace,
    /// value)</c> is uniquely indexed.
    /// </summary>
    Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the full <see cref="WorkItem.ExternalIds"/> map for the item.
    /// Implementations enforce per-project uniqueness on each
    /// <c>(namespace, value)</c> pair and throw
    /// <see cref="WorkItemExternalIdConflictException"/> on collision. The
    /// store snapshot returned by subsequent reads reflects the new map.
    /// </summary>
    Task<WorkItem?> ReplaceExternalIdsAsync(
        WorkItemId id,
        IReadOnlyDictionary<string, string> externalIds,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

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
    /// Returns work items whose <see cref="WorkItem.SuspendedVmName"/> is
    /// non-null. Hits the partial index <c>idx_work_items_suspended_vm</c> so
    /// the cost scales with the number of in-flight suspends rather than the
    /// full work-items table. Consumed by the startup resume handler and the
    /// leak reaper's protected-name set.
    /// </summary>
    IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the distinct set of non-null <see cref="WorkItem.BaselineImageRef"/>
    /// values across all work items that are NOT in a terminal state (Done /
    /// Failed / Cancelled / AuditFailed / MergeConflictResolutionFailed /
    /// AbandonedAfterRecoveryAttempts). The <see cref="CodeyBox.Orchestrator.BaselineImageReaper"/>
    /// uses this as the live-reference set for its GC sweep: any baseline VM on
    /// the host that does not appear here and is older than the grace window can
    /// be safely deleted. Hits the partial index on
    /// <c>baseline_image_ref WHERE NOT NULL</c>.
    /// </summary>
    Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the IDs and (display) titles of every work item that currently
    /// references <paramref name="baselineImageRef"/>. Operator endpoint helper
    /// for <c>/baselines</c> — lets the operator see who is pinning each
    /// baseline before disposing it.
    /// </summary>
    Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(
        string baselineImageRef, CancellationToken ct = default);

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

    /// <summary>
    /// Atomically replaces <see cref="WorkItem.Prompt"/> and increments
    /// <see cref="WorkItem.PromptRevision"/> by 1. Returns the new revision so the
    /// caller can echo it in the response. <see cref="PromptReplaceOutcome.TerminalState"/>
    /// means the item is already Done/Failed/Cancelled and was not modified.
    /// </summary>
    Task<PromptReplaceResult> TryReplacePromptAsync(
        WorkItemId id,
        string newPrompt,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Records that iteration <paramref name="iteration"/> of work item
    /// <paramref name="workItemId"/> was dispatched with the prompt revision
    /// <paramref name="promptRevisionAtDispatch"/>. Idempotent: re-dispatching
    /// the same iteration (e.g. orchestrator restart-recovery) overwrites the
    /// row so the captured revision reflects the latest dispatch.
    /// </summary>
    Task RecordIterationDispatchAsync(
        WorkItemId workItemId,
        int iteration,
        int promptRevisionAtDispatch,
        DateTimeOffset dispatchedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Returns all dispatched iterations for a work item in iteration order.
    /// </summary>
    Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(
        WorkItemId workItemId,
        CancellationToken ct = default);
}

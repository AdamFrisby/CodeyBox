using System.Runtime.CompilerServices;

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
    /// <summary>The row exists but is no longer in the state required by the caller; no write was issued.</summary>
    StateMismatch,
}

/// <summary>
/// Result returned by <see cref="IWorkItemStore.UpdatePriorityAsync"/>.
/// <see cref="Item"/> is populated on <see cref="PriorityUpdateOutcome.Updated"/>
/// and on <see cref="PriorityUpdateOutcome.TerminalState"/> /
/// <see cref="PriorityUpdateOutcome.StateMismatch"/> (so callers can surface
/// the current state to the client); null on <see cref="PriorityUpdateOutcome.NotFound"/>.
/// </summary>
public readonly record struct PriorityUpdateResult(PriorityUpdateOutcome Outcome, WorkItem? Item, int? OldPriority);

/// <summary>
/// Outcome of <see cref="IWorkItemStore.TryReplacePromptAsync"/>. <see cref="NewRevision"/>
/// is populated on <see cref="PromptReplaceOutcome.Updated"/>; null otherwise.
/// </summary>
public readonly record struct PromptReplaceResult(PromptReplaceOutcome Outcome, int? NewRevision);

public enum PromptReplaceOutcome { Updated, NotFound, TerminalState }

public enum QuotaRetryDispatchEligibility
{
    DueOnly,
    IncludeFuture,
}

/// <summary>
/// Seek cursor for priority-ordered <see cref="WorkItemState.WaitingForQuotaReset"/> scans.
/// The ordering is priority descending, then created time ascending, then id ascending.
/// </summary>
public readonly record struct WaitingForQuotaResetPriorityCursor(
    int Priority,
    DateTimeOffset CreatedAt,
    WorkItemId Id)
{
    public static WaitingForQuotaResetPriorityCursor From(WorkItem item) =>
        new(item.Priority, item.CreatedAt, item.Id);
}

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
/// Outcome of <see cref="IWorkItemStore.UpdateAuditBudgetAsync"/>.
/// </summary>
public enum AuditBudgetUpdateOutcome
{
    /// <summary>The row was updated and the new audit budget fields are persisted.</summary>
    Updated,
    /// <summary>The row no longer exists.</summary>
    NotFound,
    /// <summary>The row exists but is in a terminal state; no write was issued.</summary>
    TerminalState,
}

/// <summary>
/// Result returned by <see cref="IWorkItemStore.UpdateAuditBudgetAsync"/>.
/// <see cref="Item"/> is populated on <see cref="AuditBudgetUpdateOutcome.Updated"/>
/// and on <see cref="AuditBudgetUpdateOutcome.TerminalState"/> so callers can
/// return the current state to the client; null on <see cref="AuditBudgetUpdateOutcome.NotFound"/>.
/// </summary>
public readonly record struct AuditBudgetUpdateResult(AuditBudgetUpdateOutcome Outcome, WorkItem? Item);

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
    /// <see cref="WorkItem.Prompt"/>, <see cref="WorkItem.PromptRevision"/>,
    /// <see cref="WorkItem.AuditMaxIterations"/>, and
    /// <see cref="WorkItem.AuditComplexity"/>, and <see cref="WorkItem.Knobs"/>.
    /// Planning artifact fields are updated only when the persisted prompt
    /// revision still matches the supplied snapshot.
    /// Use <see cref="UpdatePriorityAsync"/>, <see cref="TryReplacePromptAsync"/>,
    /// <see cref="UpdateAuditBudgetAsync"/>, and
    /// <see cref="TryReplaceKnobsIfStateAndUpdatedAtAsync"/> for those fields so
    /// worker writes from stale in-memory snapshots cannot revert concurrent
    /// PATCH /priority, PUT /workitems/{id}/prompt, audit-budget, or knob updates.
    /// </summary>
    Task UpdateAsync(WorkItem item, CancellationToken ct = default);
    /// <summary>
    /// Updates persisted work-item fields except <see cref="WorkItem.Priority"/>,
    /// <see cref="WorkItem.Prompt"/>, <see cref="WorkItem.PromptRevision"/>,
    /// <see cref="WorkItem.AuditMaxIterations"/>, and
    /// <see cref="WorkItem.AuditComplexity"/>, and <see cref="WorkItem.Knobs"/>
    /// only when the persisted state still matches <paramref name="onlyIfState"/>.
    /// Planning artifact fields are updated only when the persisted prompt
    /// revision still matches the supplied snapshot.
    /// Returns true if the row was updated.
    /// </summary>
    Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default);

    /// <summary>
    /// Updates persisted work-item fields only when the persisted state and
    /// <c>updated_at</c> stamp still match the snapshot the caller inspected.
    /// Used by recovery paths that must not overwrite a worker's concurrent
    /// completion or progress update.
    /// </summary>
    async Task<bool> TryUpdateIfStateAndUpdatedAtAsync(
        WorkItem item,
        WorkItemState onlyIfState,
        DateTimeOffset onlyIfUpdatedAt,
        CancellationToken ct = default)
    {
        var current = await GetAsync(item.Id, ct).ConfigureAwait(false);
        if (current is null
            || current.State != onlyIfState
            || current.UpdatedAt != onlyIfUpdatedAt)
        {
            return false;
        }

        return await TryUpdateIfStateAsync(item, onlyIfState, ct).ConfigureAwait(false);
    }

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
    /// Partial priority update guarded by the persisted state. Returns
    /// <see cref="PriorityUpdateOutcome.StateMismatch"/> when the row exists but
    /// is no longer in <paramref name="onlyIfState"/>. Persistent stores should
    /// implement this as a single read/write critical section or conditional SQL
    /// update so callers cannot mutate a row that raced out of the expected state.
    /// </summary>
    async Task<PriorityUpdateResult> UpdatePriorityIfStateAsync(
        WorkItemId id,
        int priority,
        DateTimeOffset updatedAt,
        WorkItemState onlyIfState,
        CancellationToken ct = default)
    {
        var current = await GetAsync(id, ct).ConfigureAwait(false);
        if (current is null)
            return new PriorityUpdateResult(PriorityUpdateOutcome.NotFound, null, null);
        if (current.State is WorkItemState.Done
            or WorkItemState.Failed
            or WorkItemState.AuditFailed
            or WorkItemState.Cancelled
            or WorkItemState.MergeConflictResolutionFailed
            or WorkItemState.AbandonedAfterRecoveryAttempts)
            return new PriorityUpdateResult(PriorityUpdateOutcome.TerminalState, current, current.Priority);
        if (current.State != onlyIfState)
            return new PriorityUpdateResult(PriorityUpdateOutcome.StateMismatch, current, current.Priority);
        return await UpdatePriorityAsync(id, priority, updatedAt, ct).ConfigureAwait(false);
    }

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

    /// <summary>
    /// Partial UPDATE that touches only the audit-budget fields and
    /// <c>updated_at</c>. Used by PATCH /workitems/{id} so an operator can raise
    /// or label the audit budget on an in-flight item without writing stale
    /// pipeline-owned columns such as <c>state</c>, <c>started_at</c>, agent log
    /// paths, quota fields, or merge metadata.
    ///
    /// Returns <see cref="AuditBudgetUpdateOutcome.TerminalState"/> when the row
    /// is in a terminal state; budget edits cannot affect closed work.
    /// </summary>
    Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(
        WorkItemId id,
        int? auditMaxIterations,
        string? auditComplexity,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Partial UPDATE that touches only the per-item knob map and
    /// <c>updated_at</c>, guarded by both persisted state and the exact
    /// <c>updated_at</c> stamp the caller inspected. This is the only queued
    /// edit path for <see cref="WorkItem.Knobs"/>; routine worker state writes
    /// deliberately leave the column untouched so stale in-memory snapshots
    /// cannot erase operator edits accepted before dispatch.
    /// </summary>
    Task<bool> TryReplaceKnobsIfStateAndUpdatedAtAsync(
        WorkItemId id,
        IReadOnlyDictionary<string, string> knobs,
        DateTimeOffset updatedAt,
        WorkItemState onlyIfState,
        DateTimeOffset onlyIfUpdatedAt,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This work item store must implement guarded knob replacement before it can accept per-item knob edits.");

    /// <summary>
    /// Guarded queued-edit UPDATE used when PATCH /workitems/{id} edits the
    /// knob map together with other queued-only fields. Unlike
    /// <see cref="TryUpdateIfStateAndUpdatedAtAsync"/>, this writes
    /// <see cref="WorkItem.Prompt"/>, <see cref="WorkItem.PromptRevision"/>,
    /// <see cref="WorkItem.AuditMaxIterations"/>,
    /// <see cref="WorkItem.AuditComplexity"/>, and <see cref="WorkItem.Knobs"/>
    /// because the caller is applying a freshly validated operator PATCH
    /// against the exact snapshot identified by <paramref name="onlyIfUpdatedAt"/>.
    /// Keeping the requested row fields, audit-budget fields, and knobs in one
    /// conditional write prevents a failed mixed PATCH from partially persisting.
    /// </summary>
    Task<bool> TryUpdateQueuedFieldsAndKnobsIfStateAndUpdatedAtAsync(
        WorkItem item,
        WorkItemState onlyIfState,
        DateTimeOffset onlyIfUpdatedAt,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This work item store must implement guarded queued-field and knob replacement before it can accept mixed per-item knob edits.");

    Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default);
    IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default);
    IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default);

    /// <summary>
    /// Returns at most <paramref name="limit"/> parked quota rows in the retry
    /// sweep order: highest priority first, then oldest created time. Persistent
    /// stores should apply the limit inside the storage query so recovery paths
    /// cannot buffer an unbounded parked backlog before retrying anything.
    /// <paramref name="after"/> is an exclusive seek cursor from the prior page.
    /// </summary>
    IAsyncEnumerable<WorkItem> ListWaitingForQuotaResetByPriorityAsync(
        int limit,
        WaitingForQuotaResetPriorityCursor? after = null,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This work item store must implement bounded WaitingForQuotaReset priority queries before quota recovery sweeps can run.");

    /// <summary>
    /// Returns at most <paramref name="limit"/> terminal rows eligible for the
    /// agent-restore retry sweep. Implementations should push the state,
    /// outage window, <see cref="AgentRestoreRetryCandidatePolicy"/>, ordering,
    /// and limit into the backing store so a restore event cannot buffer all
    /// historical failures or consume the sweep cap with unrelated agents'
    /// failures.
    /// </summary>
    async IAsyncEnumerable<WorkItem> ListRestoreRetryCandidatesAsync(
        AgentKind restoredAgent,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        TimeSpan involvementTerminalLookback,
        TimeSpan involvementTerminalClockSkew,
        int limit,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (limit <= 0)
            yield break;

        var yielded = 0;
        foreach (var state in new[] { WorkItemState.Failed, WorkItemState.MergeConflictResolutionFailed })
        {
            await foreach (var item in ListByStateAsync(state, ct).ConfigureAwait(false))
            {
                if (yielded >= limit)
                    yield break;
                if (item.UpdatedAt < windowStart || item.UpdatedAt > windowEnd)
                    continue;
                if (!AgentRestoreRetryCandidatePolicy.IsEligible(item, restoredAgent, latestFailedInvolvementAgent: null))
                    continue;

                yielded++;
                yield return item;
            }
        }
    }

    /// <summary>
    /// Returns true when a successful agent-restore retry was already claimed
    /// for the same work item, restored agent, and outage start. Used before
    /// retrying so failed enqueue attempts do not consume the idempotency key.
    /// </summary>
    Task<bool> HasAgentRestoreRetryClaimAsync(
        WorkItemId id,
        AgentKind restoredAgent,
        DateTimeOffset outageStartedAt,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This work item store does not implement agent-restore retry claim lookup.");

    /// <summary>
    /// Claims a successfully requeued work item for a single agent-restore
    /// sweep key. Persistent stores should make this atomic and return false
    /// when another duplicate restore event already claimed the same
    /// item/window.
    /// </summary>
    Task<bool> TryClaimAgentRestoreRetryAsync(
        WorkItemId id,
        AgentKind restoredAgent,
        DateTimeOffset outageStartedAt,
        DateTimeOffset restoredAt,
        CancellationToken ct = default)
        => throw new NotSupportedException(
            "This work item store does not implement agent-restore retry claims.");

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
    /// <c>NeedsOperatorInput</c>, <c>WaitingForQuotaReset</c>,
    /// <c>WaitingForAgentResume</c>, and <c>WaitingForTransientRetry</c> rows
    /// are excluded.
    /// </summary>
    IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(IReadOnlySet<WorkItemId> skipIds, CancellationToken ct = default);

    /// <summary>
    /// Returns the dispatcher pickup set in one unified dispatch order: ordinary
    /// dispatch-eligible rows plus <see cref="WorkItemState.WaitingForQuotaReset"/>
    /// rows whose <see cref="WorkItem.NextQuotaRetryAt"/> is null or due,
    /// unless <paramref name="quotaRetryEligibility"/> is
    /// <see cref="QuotaRetryDispatchEligibility.IncludeFuture"/>. Finishing
    /// phases retain the same precedence as
    /// <see cref="ListDispatchEligibleByPriorityAsync"/>; rows are then ordered
    /// by <c>priority DESC</c>, then <c>created_at ASC</c> inside each phase
    /// bucket.
    /// Implementations should apply <paramref name="limit"/> as close to the
    /// storage query as possible so dispatch wakes cannot scan an unbounded
    /// parked-quota backlog.
    /// </summary>
    async IAsyncEnumerable<WorkItem> ListDispatchEligibleIncludingDueQuotaRetryByPriorityAsync(
        IReadOnlySet<WorkItemId> skipIds,
        DateTimeOffset now,
        int limit,
        QuotaRetryDispatchEligibility quotaRetryEligibility = QuotaRetryDispatchEligibility.DueOnly,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var rows = new List<(WorkItem Item, WorkItemState OrderingState, int Sequence)>();
        var sequence = 0;

        await foreach (var item in ListDispatchEligibleByPriorityAsync(skipIds, ct).ConfigureAwait(false))
            rows.Add((item, item.State, sequence++));

        await foreach (var item in ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct).ConfigureAwait(false))
        {
            if (skipIds.Contains(item.Id)
                || !IsQuotaRetryCandidateEligible(item, now, quotaRetryEligibility))
                continue;

            rows.Add((item, QuotaRetryPhasePolicy.OrderingStateForQuotaRetryCandidate(item), sequence++));
        }

        foreach (var row in rows
            .OrderBy(static row => QuotaRetryPhasePolicy.DispatchPhaseBucket(row.OrderingState))
            .ThenByDescending(static row => row.Item.Priority)
            .ThenBy(static row => row.Item.CreatedAt)
            .ThenBy(static row => row.Sequence)
            .Take(Math.Max(0, limit)))
        {
            yield return row.Item;
        }
    }

    private static bool IsQuotaRetryCandidateEligible(
        WorkItem item,
        DateTimeOffset now,
        QuotaRetryDispatchEligibility eligibility) =>
        eligibility == QuotaRetryDispatchEligibility.IncludeFuture
        || item.NextQuotaRetryAt is null
        || item.NextQuotaRetryAt <= now;

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
    /// Per-project in-flight counts split by whether the row is a
    /// <see cref="JobType.Refactor"/> or any other job type. Uses the same
    /// "in-flight" predicate as <see cref="CountInFlightAsync"/>. Used by the
    /// refactor project-exclusive gate: a <see cref="JobType.Refactor"/> item
    /// may only start when both counters are zero, and while one is in flight
    /// every other item for the same project must defer. When
    /// <paramref name="excludeId"/> is provided, that row is omitted from the
    /// split so recovered pass-through pickups do not count themselves as
    /// already in flight.
    /// </summary>
    async Task<(int Refactor, int Other)> CountInFlightSplitByRefactorAsync(
        ProjectId projectId,
        CancellationToken ct = default,
        WorkItemId? excludeId = null)
    {
        // Default implementation streams all items and partitions in-process so
        // existing IWorkItemStore implementations (in-memory test stubs) work
        // without modification. The Sqlite production implementation overrides
        // with a single COUNT() per partition.
        var refactor = 0;
        var other = 0;
        await foreach (var item in ListAsync(ct).ConfigureAwait(false))
        {
            if (item.ProjectId != projectId) continue;
            if (excludeId is { } excluded && item.Id == excluded) continue;
            if (!WorkItemInFlight.IsInFlight(item)) continue;
            if (item.JobType == JobType.Refactor) refactor++;
            else other++;
        }
        return (refactor, other);
    }

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

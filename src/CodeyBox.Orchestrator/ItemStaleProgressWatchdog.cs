using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Per-item, item-centric stale-updatedAt detector. Complements
/// <see cref="WorkerProgressWatchdog"/> (per-worker; treats CPU / stream
/// activity as progress) and <see cref="WorkerPoolHealthWatchdog"/> (pool-
/// level dispatch stall).
///
/// <para>
/// Walks <see cref="IWorkItemStore.ListByStateAsync"/> for every active
/// in-flight state (see <see cref="WorkItemRecoveryPolicy.IsItemStaleWatchedState"/>)
/// and compares <c>UpdatedAt</c> to
/// <see cref="WorkerProgressWatchdogOptions.ItemStaleTimeout"/>. When the
/// item has been frozen past the threshold the bound worker (if any) is
/// aborted, the pool slot released, and the item requeued PRESERVING its
/// work branch (re-pickup re-rebases onto current upstream main). The
/// per-worker watchdog cannot see this case — it iterates worker rows, so
/// an orphaned item with no live worker (post-restart) is invisible to it,
/// and a worker stuck in a transport reconnect loop (CPU active, item
/// frozen) looks healthy through its activity-source progress signal.
/// </para>
///
/// <para>
/// Independent of pool-level spawn health: other slots may be cycling
/// normally while this item's slot is held by a dead-or-wedged worker.
/// The per-worker watchdog defers to this one for any item it has not
/// already recovered, so the two never double-recover.
/// </para>
///
/// <para>
/// Triggered by the periodic background sweep (after the startup recovery
/// barrier) and by the operator endpoint <c>POST /workitems/{id}/recover</c>
/// — both call <see cref="RecoverItemAsync"/> with a trigger label. Bounded
/// by <see cref="WorkerProgressWatchdogOptions.ItemStaleMaxRecoveryAttempts"/>;
/// once exceeded the item is parked at
/// <see cref="WorkItemState.NeedsOperatorInput"/> instead of being requeued.
/// </para>
/// </summary>
public sealed class ItemStaleProgressWatchdog : BackgroundService
{
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IWorkerRegistry _registry;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly Func<WorkerProgressWatchdogOptions> _optsAccessor;
    private readonly ILogger<ItemStaleProgressWatchdog> _log;
    private readonly TimeProvider _time;
    private readonly IStartupInitialRecoveryBarrier? _startupRecoveryBarrier;
    private readonly CancellationRegistry? _cancellations;
    private IWorkerPoolRecoverySlotReleaser? _slotReleaser;

    // In-process record of items already recovered, keyed on the UpdatedAt
    // stamp the recovery wrote. The next sweep skips an item only while its
    // current UpdatedAt still matches (or precedes) the recorded mark — once
    // a re-pickup or any subsequent recovery advances UpdatedAt, the marker
    // becomes stale, is cleared, and the watchdog can detect a fresh wedge.
    // Without this expiry, a chronically-wedging item recovered once would
    // be permanently invisible to the watchdog for the rest of the
    // orchestrator process and the bounded-then-escalate contract would
    // never fire on it.
    private readonly ConcurrentDictionary<WorkItemId, DateTimeOffset> _recoveredItemsThisProcess = new();

    private WorkerProgressWatchdogOptions _opts => _optsAccessor();

    public ItemStaleProgressWatchdog(
        IWorkItemStore store,
        ITaskQueue queue,
        IWorkerRegistry registry,
        Func<WorkerProgressWatchdogOptions> optionsAccessor,
        ILogger<ItemStaleProgressWatchdog> log,
        IWebhookDispatcher? webhooks = null,
        IWorkerPoolRecoverySlotReleaser? slotReleaser = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null,
        CancellationRegistry? cancellations = null,
        TimeProvider? timeProvider = null)
    {
        _store = store;
        _queue = queue;
        _registry = registry;
        _optsAccessor = optionsAccessor;
        _log = log;
        _webhooks = webhooks;
        _slotReleaser = slotReleaser;
        _startupRecoveryBarrier = startupRecoveryBarrier;
        _cancellations = cancellations;
        _time = timeProvider ?? TimeProvider.System;
    }

    public ItemStaleProgressWatchdog(
        IWorkItemStore store,
        ITaskQueue queue,
        IWorkerRegistry registry,
        WorkerProgressWatchdogOptions opts,
        ILogger<ItemStaleProgressWatchdog> log,
        IWebhookDispatcher? webhooks = null,
        IWorkerPoolRecoverySlotReleaser? slotReleaser = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null,
        CancellationRegistry? cancellations = null,
        TimeProvider? timeProvider = null)
        : this(store, queue, registry, () => opts, log, webhooks, slotReleaser, startupRecoveryBarrier, cancellations, timeProvider) { }

    /// <summary>
    /// Mirrors the per-worker watchdog's late-attach pattern: the DI graph
    /// constructs the orchestrator after this service, so the slot releaser
    /// is wired in after-the-fact.
    /// </summary>
    public void AttachWorkerPoolSlotReleaser(IWorkerPoolRecoverySlotReleaser slotReleaser)
        => _slotReleaser = slotReleaser;

    /// <summary>
    /// True if this watchdog has already recovered <paramref name="itemId"/>
    /// and the recovered <c>UpdatedAt</c> stamp still matches the current
    /// row's stamp. Once the item's <c>UpdatedAt</c> advances past the
    /// recorded mark (re-pickup or a later recovery) the marker is cleared
    /// here so the next sweep evaluates the item fresh.
    /// </summary>
    internal bool HasRecoveredItemInCurrentProcess(WorkItemId itemId, DateTimeOffset currentUpdatedAt)
    {
        if (!_recoveredItemsThisProcess.TryGetValue(itemId, out var recoveredAt))
            return false;

        if (currentUpdatedAt > recoveredAt)
        {
            // Re-pickup (or any later state-mutating recovery) advanced the
            // row past our recorded mark. Clear the marker — the item is
            // back in play and a subsequent freeze must be detectable.
            _recoveredItemsThisProcess.TryRemove(itemId, out _);
            return false;
        }

        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_startupRecoveryBarrier is not null)
        {
            // Wait for startup recovery (sandbox resume + dead-worker reaper +
            // stranded sweep) so the orchestrator has already reclaimed the
            // unambiguous orphan set before this watchdog starts taking
            // additional action.
            await _startupRecoveryBarrier.InitialRecoveryCompleted.WaitAsync(stoppingToken);
        }

        await RunOnceAsync(stoppingToken);

        // Snapshot the configured interval at startup. Matches DeadWorkerReaper /
        // WorkerProgressWatchdog — threshold + max-attempts hot-reload via the
        // accessor, but the sweep frequency takes effect on next restart.
        using var timer = new PeriodicTimer(_opts.ItemStaleCheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    /// <summary>
    /// Single sweep. Walks every <see cref="WorkItemRecoveryPolicy.IsItemStaleWatchedState"/>
    /// state, recovers any item whose <c>UpdatedAt</c> has not advanced
    /// inside <see cref="WorkerProgressWatchdogOptions.ItemStaleTimeout"/>.
    /// Idempotent: an item already recovered in this process is skipped so
    /// the re-pickup window cannot double-recover before the new pickup
    /// stamps <c>UpdatedAt</c>.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var opts = _opts;
        if (opts.ItemStaleTimeout <= TimeSpan.Zero)
            return;

        try
        {
            var now = _time.GetUtcNow();
            var cutoff = now - opts.ItemStaleTimeout;

            foreach (var state in Enum.GetValues<WorkItemState>())
            {
                if (!WorkItemRecoveryPolicy.IsItemStaleWatchedState(state))
                    continue;

                await foreach (var item in _store.ListByStateAsync(state, ct))
                {
                    if (HasRecoveredItemInCurrentProcess(item.Id, item.UpdatedAt))
                        continue;

                    // Items being resumed from a suspended VM are owned by
                    // SandboxResumeOnStartupService — its single-shot resume
                    // may legitimately not stamp UpdatedAt during its window.
                    if (!string.IsNullOrWhiteSpace(item.SuspendedVmName))
                        continue;

                    if (item.UpdatedAt > cutoff)
                        continue;

                    var sinceUpdated = (long)(now - item.UpdatedAt).TotalSeconds;
                    await RecoverItemAsync(
                        item,
                        reason:
                            $"item-stale: state {item.State}, UpdatedAt frozen for {sinceUpdated}s (threshold {(long)opts.ItemStaleTimeout.TotalSeconds}s)",
                        trigger: "watchdog",
                        ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Item-stale progress watchdog sweep failed");
        }
    }

    /// <summary>
    /// Outcome of a single recovery call. Returned to the operator endpoint
    /// and the watchdog sweep so each caller can shape its response /
    /// telemetry. <see cref="Recovered"/> is true on a state-changing
    /// recovery (item written + slot released + audit + webhook); false when
    /// the item was already recovered in this process, was in a state the
    /// recovery routine does not own, or hit a transient error.
    /// </summary>
    public sealed record RecoveryResult(
        bool Recovered,
        WorkItemState? FromState,
        WorkItemState? NewState,
        int Attempt,
        bool BranchPreserved,
        string? Error);

    /// <summary>
    /// Recover a single item. Used by the periodic sweep and by the operator
    /// endpoint <c>POST /workitems/{id}/recover</c>. Steps:
    /// <list type="number">
    ///   <item>Refuse non-watched states (operator endpoint surfaces a 409).</item>
    ///   <item>Re-read the row and require the same active state / UpdatedAt
    ///         stamp the caller inspected.</item>
    ///   <item>Build the next state via <see cref="WorkItemRecoveryPolicy.BuildStaleItemRecovery"/>:
    ///         preserve-branch requeue for Working/Reworking without
    ///         checkpoint, NeedsOperatorInput when MaxRecoveryAttempts is
    ///         exceeded, then write it through a guarded update.</item>
    ///   <item>Claim and recovery-cancel the worker registry row for this item
    ///         (if any), then release the pool slot so the dispatcher can pick the next
    ///         eligible item up immediately.</item>
    ///   <item>Restore cascade-cancelled dependents, emit audit log + webhook,
    ///         and enqueue the recovered parent.</item>
    /// </list>
    /// Operator-triggered recovery is bounded by the same
    /// <see cref="WorkerProgressWatchdogOptions.ItemStaleMaxRecoveryAttempts"/>
    /// cap as the watchdog: an item that has already escalated to
    /// NeedsOperatorInput will escalate again via the policy helper, so the
    /// operator sees an explicit park rather than a silent loop.
    /// </summary>
    public Task<RecoveryResult> RecoverItemAsync(WorkItem item, string reason, CancellationToken ct)
        => RecoverItemAsync(item, reason, trigger: "operator", ct);

    internal async Task<RecoveryResult> RecoverItemAsync(
        WorkItem item,
        string reason,
        string trigger,
        CancellationToken ct)
    {
        if (!WorkItemRecoveryPolicy.IsItemStaleWatchedState(item.State))
        {
            return new RecoveryResult(
                Recovered: false,
                FromState: item.State,
                NewState: null,
                Attempt: item.RecoveryAttempts,
                BranchPreserved: false,
                Error: $"item is in state {item.State}, not an active in-flight state");
        }

        var opts = _opts;
        var current = await _store.GetAsync(item.Id, ct);
        if (current is null)
        {
            return new RecoveryResult(
                Recovered: false,
                FromState: item.State,
                NewState: null,
                Attempt: item.RecoveryAttempts,
                BranchPreserved: false,
                Error: "work item no longer exists");
        }

        if (current.State != item.State || current.UpdatedAt != item.UpdatedAt)
        {
            return new RecoveryResult(
                Recovered: false,
                FromState: current.State,
                NewState: null,
                Attempt: current.RecoveryAttempts,
                BranchPreserved: false,
                Error:
                    $"work item advanced from {item.State}@{item.UpdatedAt:O} to {current.State}@{current.UpdatedAt:O}; recovery skipped");
        }

        var attempts = current.RecoveryAttempts + 1;
        var now = _time.GetUtcNow();
        var recovered = WorkItemRecoveryPolicy.BuildStaleItemRecovery(
            current,
            attempts,
            opts.ItemStaleMaxRecoveryAttempts,
            reason,
            now);

        if (recovered is null)
        {
            // Defensive: BuildStaleItemRecovery returns null only for non-watched
            // states, which we filtered above. Surface the situation cleanly.
            return new RecoveryResult(
                Recovered: false,
                FromState: current.State,
                NewState: null,
                Attempt: current.RecoveryAttempts,
                BranchPreserved: false,
                Error: $"no recovery transition defined for state {current.State}");
        }

        var fromState = current.State;
        var toState = recovered.State;
        var branchPreserved =
            toState == WorkItemState.Queued
            && recovered.PreserveWorkBranchOnQueuedPickup
            && !string.IsNullOrWhiteSpace(recovered.WorkBranch);

        try
        {
            var wrote = await _store.TryUpdateIfStateAndUpdatedAtAsync(
                recovered,
                current.State,
                current.UpdatedAt,
                ct);
            if (!wrote)
            {
                return new RecoveryResult(
                    Recovered: false,
                    FromState: current.State,
                    NewState: null,
                    Attempt: current.RecoveryAttempts,
                    BranchPreserved: false,
                    Error: "work item advanced before recovery write; recovery skipped");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Item-stale recovery: failed to write recovered state for {ItemId}; retrying on next sweep",
                item.Id);
            return new RecoveryResult(
                Recovered: false,
                FromState: fromState,
                NewState: null,
                Attempt: current.RecoveryAttempts,
                BranchPreserved: false,
                Error: $"failed to update store: {ex.Message}");
        }

        MarkRecoveredItem(item.Id, recovered.UpdatedAt);

        // Claim any worker row pointing at this item only after the guarded
        // recovery write wins. If the row advanced concurrently, recovery is a
        // no-op and must not abort a worker that made progress.
        var workerId = await TryClaimBoundWorkerAsync(item.Id, ct);

        // Signal the running pipeline (if any) with recovery intent so it exits
        // and tears down its sandbox without routing the cancellation as
        // DELETE/operator cancellation.
        _cancellations?.CancelForRecovery(item.Id);

        // Release the worker pool slot regardless of whether the underlying
        // worker task ever exits — the durable row is already updated, so the
        // generic wake will not re-dispatch the stale worker-owned state.
        if (_slotReleaser is not null && workerId is not null)
        {
            await _slotReleaser.TryReleaseRecoveredWorkerSlotAsync(
                workerId, item.Id,
                $"item-stale recovery: {reason}",
                ct);
        }

        AuditLog.WorkItemStaleDetected(
            item.Id,
            workerId ?? "<no-live-worker>",
            fromState,
            (long)(now - current.UpdatedAt).TotalSeconds,
            trigger);

        AuditLog.WorkItemStaleRecovered(
            item.Id,
            workerId ?? "<no-live-worker>",
            fromState,
            toState,
            attempts,
            branchPreserved,
            trigger);

        _log.LogWarning(
            "Item-stale ({Trigger}) recovered work item {ItemId} (worker {WorkerId}) from {FromState} → {ToState} (attempt {Attempt}/{Max}); branchPreserved={BranchPreserved}",
            trigger, item.Id, workerId ?? "<no-live-worker>", fromState, toState, attempts, opts.ItemStaleMaxRecoveryAttempts, branchPreserved);

        var restoredDependents = 0;
        if (toState != WorkItemState.NeedsOperatorInput && toState is not WorkItemState.Failed)
            restoredDependents = await RestoreCascadedDependentsAsync(item.Id, ct);

        if (_webhooks is not null)
        {
            try
            {
                await _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.recovered",
                    WorkItem = recovered,
                    Details = new
                    {
                        workItemId = item.Id.ToString(),
                        projectId = item.ProjectId.Value,
                        fromState = fromState.ToString(),
                        toState = toState.ToString(),
                        reason = "item-stale updatedAt",
                        trigger,
                        recoveryAttempt = attempts,
                        maxRecoveryAttempts = opts.ItemStaleMaxRecoveryAttempts,
                        branchPreserved,
                        workerId,
                        dependentsRestored = restoredDependents,
                    },
                }, CancellationToken.None);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Item-stale recovery: failed to publish work_item.recovered for {ItemId}", item.Id);
            }
        }

        if (toState != WorkItemState.NeedsOperatorInput && toState is not WorkItemState.Failed)
            await _queue.EnqueueAsync(item.Id, ct);

        return new RecoveryResult(
            Recovered: true,
            FromState: fromState,
            NewState: toState,
            Attempt: attempts,
            BranchPreserved: branchPreserved,
            Error: null);
    }

    private async Task<int> RestoreCascadedDependentsAsync(WorkItemId recoveredId, CancellationToken ct)
    {
        var all = new List<WorkItem>();
        await foreach (var existing in _store.ListAsync(ct))
            all.Add(existing);

        var toRestore = WorkItemDependencies.FindDescendantsToRestore(recoveredId, all);
        if (toRestore.Count == 0) return 0;

        var restored = 0;
        foreach (var descendant in toRestore)
        {
            var requeued = descendant with
            {
                State = WorkItemState.Queued,
                CancellationReason = null,
                LastError = null,
                StartedAt = null,
                WorkBranch = null,
                UpdatedAt = _time.GetUtcNow(),
            };

            var wrote = await _store.TryUpdateIfStateAsync(requeued, WorkItemState.Cancelled, ct);
            if (!wrote) continue;

            AuditLog.WorkItemDependentRestored(descendant.Id, recoveredId);
            restored++;
        }

        return restored;
    }

    private void MarkRecoveredItem(WorkItemId itemId, DateTimeOffset recoveredUpdatedAt)
        => _recoveredItemsThisProcess[itemId] = recoveredUpdatedAt;

    private async Task<string?> TryClaimBoundWorkerAsync(WorkItemId itemId, CancellationToken ct)
    {
        IReadOnlyList<WorkerRegistration> workers;
        try
        {
            workers = await _registry.ListAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Item-stale recovery: failed to list workers while looking for owner of {ItemId}; continuing without claim", itemId);
            return null;
        }

        var idStr = itemId.ToString();
        foreach (var worker in workers)
        {
            if (string.IsNullOrEmpty(worker.CurrentWorkItemId)) continue;
            if (!string.Equals(worker.CurrentWorkItemId, idStr, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var claimed = await _registry.TryClaimWorkerAsync(worker.WorkerId, ct);
                if (claimed is not null)
                    return worker.WorkerId;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogDebug(ex,
                    "Item-stale recovery: failed to claim worker {WorkerId} for {ItemId}; continuing without claim",
                    worker.WorkerId, itemId);
            }
        }

        return null;
    }
}

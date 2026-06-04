using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Lifecycle-wide progress watchdog that complements
/// <see cref="DeadWorkerReaper"/> (which only catches stale heartbeats) and
/// <c>WorkItem.WorkTimeout</c> (which only fences the agent subprocess).
///
/// <para>
/// A worker can heartbeat forever yet make no real progress when it wedges
/// either BEFORE the agent run (sandbox provisioning, repo mount) or AFTER it
/// (commit, branch push, state transition WorkComplete/Auditing). In both
/// cases the worker holds its pool slot indefinitely, starving Queued and
/// finishing-phase items behind it. The watchdog observes
/// <c>item.UpdatedAt</c> + agent-stream file mtimes: when neither advances
/// for <see cref="WorkerProgressWatchdogOptions.ProgressTimeout"/> the
/// worker is recycled and (when configured) the item auto-retries from its
/// nearest recoverable resume state — without cascade-cancelling healthy
/// dependents.
/// </para>
///
/// <para>
/// Heartbeat alone does not satisfy the progress check; the registry row's
/// <c>last_heartbeat_at</c> is ignored here entirely. The reaper still owns
/// the stale-heartbeat path.
/// </para>
/// </summary>
public sealed class WorkerProgressWatchdog : BackgroundService
{
    private readonly IWorkerRegistry _registry;
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IAgentStreamStore? _streams;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly Func<WorkerProgressWatchdogOptions> _optsAccessor;
    private readonly ILogger<WorkerProgressWatchdog> _log;
    private readonly IStartupInitialRecoveryBarrier? _startupRecoveryBarrier;
    private IWorkerPoolRecoverySlotReleaser? _slotReleaser;

    // Tracks worker ids whose item the watchdog has already recycled in this
    // process so the next sweep does not re-recover an item that was correctly
    // re-queued and is now waiting on the dispatcher.
    private readonly ConcurrentDictionary<string, byte> _recoveredWorkers = new(StringComparer.Ordinal);

    private WorkerProgressWatchdogOptions _opts => _optsAccessor();

    public WorkerProgressWatchdog(
        IWorkerRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        Func<WorkerProgressWatchdogOptions> optionsAccessor,
        ILogger<WorkerProgressWatchdog> log,
        IAgentStreamStore? streams = null,
        IWebhookDispatcher? webhooks = null,
        IWorkerPoolRecoverySlotReleaser? slotReleaser = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null)
    {
        _registry = registry;
        _store = store;
        _queue = queue;
        _optsAccessor = optionsAccessor;
        _log = log;
        _streams = streams;
        _webhooks = webhooks;
        _slotReleaser = slotReleaser;
        _startupRecoveryBarrier = startupRecoveryBarrier;
    }

    public WorkerProgressWatchdog(
        IWorkerRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        WorkerProgressWatchdogOptions opts,
        ILogger<WorkerProgressWatchdog> log,
        IAgentStreamStore? streams = null,
        IWebhookDispatcher? webhooks = null,
        IWorkerPoolRecoverySlotReleaser? slotReleaser = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null)
        : this(registry, store, queue, () => opts, log, streams, webhooks, slotReleaser, startupRecoveryBarrier) { }

    /// <summary>
    /// Lets <see cref="OrchestratorService"/> wire itself in after-the-fact
    /// — the DI graph constructs the orchestrator second so we cannot
    /// inject it through the constructor without a circular dependency.
    /// Mirrors <see cref="DeadWorkerReaper.AttachWorkerPoolSlotReleaser"/>.
    /// </summary>
    public void AttachWorkerPoolSlotReleaser(IWorkerPoolRecoverySlotReleaser slotReleaser)
        => _slotReleaser = slotReleaser;

    internal bool HasRecoveredWorkerInCurrentProcess(string workerId)
        => _recoveredWorkers.ContainsKey(workerId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_startupRecoveryBarrier is not null)
        {
            // Keep the first watchdog pass behind the orchestrator's startup
            // recovery sweep. The watchdog ignores heartbeat freshness, so it
            // must not claim stale rows left for the startup reaper path.
            await _startupRecoveryBarrier.InitialRecoveryCompleted.WaitAsync(stoppingToken);
        }

        await RunOnceAsync(stoppingToken);

        // Snapshot the configured interval at startup; matching DeadWorkerReaper,
        // changes to CheckInterval take effect on the next process restart while
        // ProgressTimeout / AutoRecover / PostAgentTransitionTimeout are resolved
        // on every sweep via the accessor.
        using var timer = new PeriodicTimer(_opts.CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    /// <summary>
    /// Runs a single watchdog sweep. Public + idempotent so tests can drive it
    /// directly and so an operator-triggered endpoint could invoke it ad hoc.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var opts = _opts;
        if (opts.ProgressTimeout <= TimeSpan.Zero)
            return;

        try
        {
            var workers = await _registry.ListAsync(ct);
            if (workers.Count == 0) return;

            var now = DateTimeOffset.UtcNow;
            var cutoff = now - opts.ProgressTimeout;

            foreach (var worker in workers)
            {
                if (string.IsNullOrEmpty(worker.CurrentWorkItemId)) continue;
                if (!Guid.TryParse(worker.CurrentWorkItemId, out var guid)) continue;

                var itemId = new WorkItemId(guid);
                var item = await _store.GetAsync(itemId, ct);
                if (item is null) continue;
                if (!IsWatchedState(item.State)) continue;
                if (_recoveredWorkers.ContainsKey(worker.WorkerId)) continue;

                var lastStreamAt = await GetLastStreamActivityAsync(itemId, ct);
                var lastProgress = lastStreamAt is { } streamAt && streamAt > item.UpdatedAt
                    ? streamAt
                    : item.UpdatedAt;

                // Items that have not run long enough yet (StartedAt newer than
                // cutoff) cannot have stalled for the configured window even if
                // UpdatedAt is older. This handles items that pick up just before
                // a sweep with a pending UpdatedAt from an earlier requeue.
                if (item.StartedAt is { } startedAt && startedAt > cutoff) continue;

                if (lastProgress > cutoff) continue;

                var sinceProgress = (long)(now - lastProgress).TotalSeconds;
                AuditLog.WorkItemWatchdogStuck(
                    itemId, worker.WorkerId, item.State, sinceProgress,
                    lastStreamAt is null ? "no-stream" : $"stream-mtime={lastStreamAt:O}");

                if (opts.AutoRecover)
                {
                    await RecoverStuckWorkerAsync(worker, item, sinceProgress, ct);
                }
                else
                {
                    await ParkStuckItemAsync(worker, item, sinceProgress, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Worker-progress watchdog sweep failed");
        }
    }

    private async Task<DateTimeOffset?> GetLastStreamActivityAsync(WorkItemId itemId, CancellationToken ct)
    {
        if (_streams is null) return null;
        try
        {
            var files = await _streams.ListAsync(itemId, limit: AgentStreamStore.MaxListLimit, includeLineCount: false, ct);
            if (files.Count == 0) return null;
            DateTimeOffset newest = files[0].CapturedAt;
            for (var i = 1; i < files.Count; i++)
            {
                if (files[i].CapturedAt > newest)
                    newest = files[i].CapturedAt;
            }
            return newest;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Watchdog: failed to read stream activity for {ItemId}; treating as no-stream", itemId);
            return null;
        }
    }

    private async Task RecoverStuckWorkerAsync(
        WorkerRegistration worker, WorkItem item, long sinceProgressSeconds, CancellationToken ct)
    {
        // Atomically pull THIS wedged worker (and only this one) out of the
        // registry so a concurrent reaper or watchdog tick cannot recover the
        // same worker twice. A cutoff-based ClaimDeadWorkersAsync would also
        // delete every healthy peer whose heartbeat is older than the chosen
        // instant — i.e. all of them, since heartbeats are by definition in
        // the past — silently disabling the dead-worker safety net for those
        // peers. Per-id claim is the only correct shape here.
        WorkerRegistration? claimed;
        try
        {
            claimed = await _registry.TryClaimWorkerAsync(worker.WorkerId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Watchdog: failed to claim wedged worker {WorkerId} from registry; skipping recovery this tick",
                worker.WorkerId);
            return;
        }

        if (claimed is null)
        {
            _log.LogDebug(
                "Watchdog: wedged worker {WorkerId} row was already gone (another sweep handled it); skipping",
                worker.WorkerId);
            return;
        }

        // Pick a recovery target. Mirror the dead-worker reaper's mapping so
        // operators see the same "from → to" transition regardless of whether
        // the wedge was a stale heartbeat or a stalled-but-heartbeating worker.
        // Working-without-preempt is special: an in-flight agent run that
        // ghosted has no checkpoint, so re-queue from Queued.
        WorkItemState target;
        if (item.State == WorkItemState.Working && !string.IsNullOrWhiteSpace(item.PreemptCheckpoint))
        {
            // The preempt-checkpoint case is recoverable mid-Working — keep
            // the state, the next pickup will resume from the checkpoint.
            target = WorkItemState.Working;
        }
        else if (item.State == WorkItemState.Working)
        {
            target = WorkItemState.Queued;
        }
        else
        {
            target = DeadWorkerReaper.MapToRecoveryState(item.State) ?? item.State;
        }

        // RecoveryAttempts gates against an item that recurrently wedges,
        // matching the dead-worker reaper's ceiling. Watchdog interventions
        // count against the same budget — they represent genuine recovery
        // work even if the heartbeat path didn't fire.
        var attempts = item.RecoveryAttempts + 1;
        var fromState = item.State;
        var opts = _opts;
        // MaxRecoveryAttempts <= 0 means unlimited. Only enforce when > 0.
        // Mirrors the DeadWorkerReaper / OrchestratorService budget check so
        // an item that wedges on every pickup eventually Fails rather than
        // looping Working → Queued → Working forever and burning a slot per
        // iteration. The preempt-checkpoint branch is exempt: that's a clean
        // resume from a captured ref, not a counted recovery transition.
        if (opts.MaxRecoveryAttempts > 0
            && attempts > opts.MaxRecoveryAttempts
            && !(target == WorkItemState.Working && !string.IsNullOrWhiteSpace(item.PreemptCheckpoint)))
        {
            var failed = item with
            {
                State = WorkItemState.Failed,
                LastError = $"watchdog: exceeded MaxRecoveryAttempts ({opts.MaxRecoveryAttempts}); was {fromState} with no progress for {sinceProgressSeconds}s",
                RecoveryAttempts = attempts,
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpdateAsync(failed, ct);
            _recoveredWorkers[worker.WorkerId] = 0;
            if (_slotReleaser is not null)
            {
                await _slotReleaser.TryReleaseRecoveredWorkerSlotAsync(
                    worker.WorkerId, item.Id,
                    $"watchdog: exceeded MaxRecoveryAttempts ({opts.MaxRecoveryAttempts}) after {sinceProgressSeconds}s without progress",
                    ct);
            }

            AuditLog.WorkItemWatchdogRecovered(item.Id, worker.WorkerId, fromState, WorkItemState.Failed, dependentsRestored: 0);
            _log.LogWarning(
                "Watchdog: work item {ItemId} (worker {WorkerId}) exceeded MaxRecoveryAttempts ({Max}); failing permanently",
                item.Id, worker.WorkerId, opts.MaxRecoveryAttempts);

            if (_webhooks is not null)
            {
                _ = _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.recovered",
                    WorkItem = failed,
                    Details = new
                    {
                        workItemId = item.Id.ToString(),
                        projectId = item.ProjectId.Value,
                        fromState = fromState.ToString(),
                        toState = failed.State.ToString(),
                        reason = "watchdog progress timeout (exceeded MaxRecoveryAttempts)",
                        sinceProgressSeconds,
                        recoveryAttempt = attempts,
                        maxRecoveryAttempts = opts.MaxRecoveryAttempts,
                    },
                }, CancellationToken.None);
            }

            return;
        }

        WorkItem updated;
        if (target == WorkItemState.Working && !string.IsNullOrWhiteSpace(item.PreemptCheckpoint))
        {
            updated = item with
            {
                StartedAt = null,
                UpdatedAt = DateTimeOffset.UtcNow,
                RecoveryAttempts = attempts,
            };
        }
        else
        {
            updated = item with
            {
                State = target,
                LastError = $"watchdog: worker made no progress for {sinceProgressSeconds}s in state {fromState}",
                StartedAt = target == WorkItemState.Queued ? null : item.StartedAt,
                WorkBranch = target == WorkItemState.Queued ? null : item.WorkBranch,
                // Clearing WorkBranch on Queued recovery regenerates the default
                // rebase-owned branch name on re-dispatch; the operator-resume
                // preservation flag pointed at the prior (now-lost) branch, so
                // it must not silently apply to the regenerated default branch.
                PreserveWorkBranchOnQueuedPickup = target == WorkItemState.Queued
                    ? false
                    : item.PreserveWorkBranchOnQueuedPickup,
                PreemptedAt = target is WorkItemState.Working or WorkItemState.Reworking ? item.PreemptedAt : null,
                PreemptCheckpoint = target is WorkItemState.Working or WorkItemState.Reworking ? item.PreemptCheckpoint : null,
                RecoveryAttempts = attempts,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        await _store.UpdateAsync(updated, ct);
        _recoveredWorkers[worker.WorkerId] = 0;

        // Free the pool slot regardless of whether the underlying worker task
        // ever exits. The durable row is updated first so the generic wake
        // cannot redispatch stale worker-owned state.
        if (_slotReleaser is not null)
        {
            await _slotReleaser.TryReleaseRecoveredWorkerSlotAsync(
                worker.WorkerId, item.Id,
                $"watchdog: no progress for {sinceProgressSeconds}s in state {item.State}",
                ct);
        }

        // Restore any descendants that were cascade-cancelled because of THIS
        // parent. The operator-cancel cascade today (CascadeCancelDependentsAsync)
        // writes Cancelled + reason=ParentCascaded on every Queued descendant;
        // if the watchdog auto-recovers the parent, those dependents must come
        // back to Queued or they remain silently stranded with `lastError =
        // "parent dependency cancelled"`. We never resurrect operator-cancelled
        // items — FindDescendantsToRestore filters by CancellationReason.
        var restoredCount = await RestoreCascadedDependentsAsync(item.Id, ct);

        AuditLog.WorkItemWatchdogRecovered(item.Id, worker.WorkerId, fromState, updated.State, restoredCount);
        _log.LogWarning(
            "Watchdog recovered work item {ItemId} (worker {WorkerId}) from {FromState} → {ToState} after {Seconds}s without progress; restored {Restored} dependents",
            item.Id, worker.WorkerId, fromState, updated.State, sinceProgressSeconds, restoredCount);

        if (_webhooks is not null)
        {
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.recovered",
                WorkItem = updated,
                Details = new
                {
                    workItemId = item.Id.ToString(),
                    projectId = item.ProjectId.Value,
                    fromState = fromState.ToString(),
                    toState = updated.State.ToString(),
                    reason = "watchdog progress timeout",
                    sinceProgressSeconds,
                    dependentsRestored = restoredCount,
                    recoveryAttempt = attempts,
                },
            }, CancellationToken.None);
        }

        await _queue.EnqueueAsync(item.Id, ct);
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
            // Atomic restore: only flip Cancelled → Queued when the row is still
            // Cancelled. Anything else (a concurrent uncancel, manual operator
            // retry) won the race; do not clobber it.
            var requeued = descendant with
            {
                State = WorkItemState.Queued,
                CancellationReason = null,
                LastError = null,
                StartedAt = null,
                WorkBranch = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            var wrote = await _store.TryUpdateIfStateAsync(requeued, WorkItemState.Cancelled, ct);
            if (!wrote) continue;

            AuditLog.WorkItemDependentRestored(descendant.Id, recoveredId);
            // Don't enqueue — the dependency gate will reject the pickup until
            // the recovered parent reaches a satisfying state. The dispatcher
            // re-evaluates eligibility on every kick (PickNextEligibleAsync).
            restored++;
        }

        return restored;
    }

    private async Task ParkStuckItemAsync(
        WorkerRegistration worker, WorkItem item, long sinceProgressSeconds, CancellationToken ct)
    {
        // Same per-id claim shape as the recover path — see the comment there
        // for why a cutoff-based claim would wipe healthy peers.
        WorkerRegistration? claimed;
        try
        {
            claimed = await _registry.TryClaimWorkerAsync(worker.WorkerId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Watchdog: failed to claim wedged worker {WorkerId} from registry (park path); skipping this tick",
                worker.WorkerId);
            return;
        }
        if (claimed is null)
            return;

        var parked = item with
        {
            State = WorkItemState.NeedsOperatorInput,
            LastError = $"watchdog: worker made no progress for {sinceProgressSeconds}s in state {item.State}; operator triage required (auto-recover disabled)",
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _store.UpdateAsync(parked, ct);
        _recoveredWorkers[worker.WorkerId] = 0;

        if (_slotReleaser is not null)
        {
            await _slotReleaser.TryReleaseRecoveredWorkerSlotAsync(
                worker.WorkerId, item.Id,
                $"watchdog: parking item after {sinceProgressSeconds}s without progress (auto-recover disabled)",
                ct);
        }

        AuditLog.WorkItemWatchdogParked(item.Id, worker.WorkerId, item.State);
    }

    /// <summary>
    /// Worker-owned states for which lack of progress indicates a wedge.
    /// Mirrors <see cref="DeadWorkerReaper.HandlesRecoveryState"/> but excludes
    /// the durable phase-boundary resting states (WorkComplete / AuditPassed /
    /// Merged) — those are queue-tail states that legitimately sit idle while
    /// the dispatcher gets around to picking them up.
    /// </summary>
    internal static bool IsWatchedState(WorkItemState state) => state switch
    {
        WorkItemState.Working => true,
        WorkItemState.Reworking => true,
        WorkItemState.Auditing => true,
        WorkItemState.Merging => true,
        WorkItemState.ReworkingForConflict => true,
        WorkItemState.UpstreamPushing => true,
        _ => false,
    };
}

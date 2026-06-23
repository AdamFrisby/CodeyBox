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
/// <c>item.UpdatedAt</c> + agent-stream file mtimes + worker-side activity:
/// when none advances for <see cref="WorkerProgressWatchdogOptions.ProgressTimeout"/>
/// the worker is recycled and (when configured) the item auto-retries from
/// its nearest recoverable resume state — without cascade-cancelling healthy
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
    private readonly IWorkerProgressActivitySource? _activitySource;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly Func<WorkerProgressWatchdogOptions> _optsAccessor;
    private readonly ILogger<WorkerProgressWatchdog> _log;
    private readonly IStartupInitialRecoveryBarrier? _startupRecoveryBarrier;
    private readonly CancellationRegistry? _cancellations;
    private IWorkerPoolRecoverySlotReleaser? _slotReleaser;

    // Tracks worker ids whose item the watchdog has already recycled in this
    // process so the next sweep does not re-recover an item that was correctly
    // re-queued and is now waiting on the dispatcher.
    private readonly ConcurrentDictionary<string, byte> _recoveredWorkers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<WorkerActivityKey, WorkerActivityProgress> _workerActivityProgress = new();

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
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null,
        IWorkerProgressActivitySource? activitySource = null,
        CancellationRegistry? cancellationRegistry = null)
    {
        _registry = registry;
        _store = store;
        _queue = queue;
        _optsAccessor = optionsAccessor;
        _log = log;
        _streams = streams;
        _activitySource = activitySource;
        _webhooks = webhooks;
        _slotReleaser = slotReleaser;
        _startupRecoveryBarrier = startupRecoveryBarrier;
        _cancellations = cancellationRegistry;
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
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null,
        IWorkerProgressActivitySource? activitySource = null,
        CancellationRegistry? cancellationRegistry = null)
        : this(registry, store, queue, () => opts, log, streams, webhooks, slotReleaser, startupRecoveryBarrier, activitySource, cancellationRegistry) { }

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
        // Per-agent overrides may keep some kinds active even when the global
        // ProgressTimeout is set to zero (disable-by-default with explicit
        // opt-ins) and vice-versa, so the global short-circuit is conditional
        // on there being no opt-in overrides either.
        if (opts.ProgressTimeout <= TimeSpan.Zero && !HasPerAgentProgressOverride(opts))
            return;

        try
        {
            var workers = await _registry.ListAsync(ct);
            if (workers.Count == 0) return;

            var now = DateTimeOffset.UtcNow;

            foreach (var worker in workers)
            {
                if (string.IsNullOrEmpty(worker.CurrentWorkItemId)) continue;
                if (!Guid.TryParse(worker.CurrentWorkItemId, out var guid)) continue;

                var itemId = new WorkItemId(guid);
                var item = await _store.GetAsync(itemId, ct);
                if (item is null) continue;
                if (!IsWatchedState(item.State)) continue;
                if (!string.IsNullOrWhiteSpace(item.SuspendedVmName)) continue;
                if (_recoveredWorkers.ContainsKey(worker.WorkerId)) continue;

                // Per-agent ProgressTimeout override resolution. Batch-latency
                // agents (notably crock — minutes-to-hours per task) MUST NOT
                // be killed by the synchronous-agent default 60-minute window;
                // operators configure the override under
                // CodeyBox:WorkerProgressWatchdog:PerAgent:<kind>:ProgressTimeout.
                var effectiveTimeout = opts.ResolveProgressTimeout(item.Agent);
                if (effectiveTimeout <= TimeSpan.Zero) continue;
                var cutoff = now - effectiveTimeout;

                var activityKey = new WorkerActivityKey(worker.WorkerId, itemId);
                var lastStreamAt = await GetLastStreamActivityAsync(itemId, ct);
                DateTimeOffset? lastActivityAt = null;
                if (_workerActivityProgress.TryGetValue(activityKey, out var activityProgress))
                {
                    if (IsActivityReasonEnabled(activityProgress.Reason, opts))
                        lastActivityAt = activityProgress.ObservedAt;
                    else
                        _workerActivityProgress.TryRemove(activityKey, out _);
                }
                var lastProgress = MaxProgressAt(item.UpdatedAt, lastStreamAt, lastActivityAt);

                // Items that have not run long enough yet (StartedAt newer than
                // cutoff) cannot have stalled for the configured window even if
                // UpdatedAt is older. This handles items that pick up just before
                // a sweep with a pending UpdatedAt from an earlier requeue.
                if (item.StartedAt is { } startedAt && startedAt > cutoff) continue;

                if (lastProgress > cutoff) continue;

                var workerActivity = await GetWorkerActivityAsync(worker, itemId, opts, ct);
                if (workerActivity is not null)
                {
                    _workerActivityProgress[activityKey] = new WorkerActivityProgress(now, workerActivity.Reason);
                    _log.LogDebug(
                        "Watchdog: worker {WorkerId} for item {ItemId} has live activity signal {Reason}; treating as progress",
                        worker.WorkerId, itemId, workerActivity.Reason);
                    continue;
                }

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

    private static bool HasPerAgentProgressOverride(WorkerProgressWatchdogOptions opts)
    {
        foreach (var (_, per) in opts.PerAgent)
        {
            if (per?.ProgressTimeout is { } pt && pt > TimeSpan.Zero)
                return true;
        }
        return false;
    }

    private async ValueTask<WorkerProgressActivity?> GetWorkerActivityAsync(
        WorkerRegistration worker,
        WorkItemId itemId,
        WorkerProgressWatchdogOptions opts,
        CancellationToken ct)
    {
        if (_activitySource is null)
            return null;
        if (!opts.ProcessCpuProgressSignalEnabled && !opts.ActiveSandboxProgressSignalEnabled)
            return null;

        var probe = new WorkerProgressActivityProbe(
            opts.ProcessCpuProgressSignalEnabled,
            opts.ActiveSandboxProgressSignalEnabled);

        try
        {
            return await _activitySource.ObserveAsync(worker, itemId, probe, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Watchdog: failed to read worker activity for {ItemId}; treating as no activity", itemId);
            return null;
        }
    }

    private static DateTimeOffset MaxProgressAt(
        DateTimeOffset itemUpdatedAt,
        DateTimeOffset? streamAt,
        DateTimeOffset? activityAt)
    {
        var max = itemUpdatedAt;
        if (streamAt is { } stream && stream > max)
            max = stream;
        if (activityAt is { } activity && activity > max)
            max = activity;
        return max;
    }

    private static bool IsActivityReasonEnabled(string reason, WorkerProgressWatchdogOptions opts)
    {
        if (string.Equals(reason, "process-cpu", StringComparison.Ordinal))
            return opts.ProcessCpuProgressSignalEnabled;
        if (reason.StartsWith("active-sandbox", StringComparison.Ordinal))
            return opts.ActiveSandboxProgressSignalEnabled;

        return opts.ProcessCpuProgressSignalEnabled || opts.ActiveSandboxProgressSignalEnabled;
    }

    private readonly record struct WorkerActivityKey(string WorkerId, WorkItemId ItemId);
    private sealed record WorkerActivityProgress(DateTimeOffset ObservedAt, string Reason);

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
        var hadDispatchClaim = item.AgentTurnResumeCheckpoint?.DispatchClaimId is not null;
        var quiescedItem = await TryQuiesceDispatchClaimOwnerAsync(worker, item, _opts, ct);
        if (quiescedItem is null)
            return;
        item = quiescedItem;

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

        if (claimed is null && !hadDispatchClaim)
        {
            _log.LogDebug(
                "Watchdog: wedged worker {WorkerId} row was already gone (another sweep handled it); skipping",
                worker.WorkerId);
            return;
        }

        // A claimed durable turn is quiesced before the registry claim. Its
        // worker can therefore deregister normally while the watchdog waits.
        // The guarded work-item write below remains the recovery election in
        // that case; only one concurrent sweep can win it.

        // Pick a recovery target. Mirror the dead-worker reaper's mapping so
        // operators see the same "from → to" transition regardless of whether
        // the wedge was a stale heartbeat or a stalled-but-heartbeating worker.
        // Working-without-preempt is special: an in-flight agent run that
        // ghosted has no checkpoint, so re-queue from Queued.
        WorkItemState target;
        if (item.State is WorkItemState.Working or WorkItemState.Reworking
            && item.HasAgentTurnRecoveryBoundary)
        {
            // A work/rework checkpoint is already an exact durable boundary.
            // Keep its paired state and metadata; mapping Reworking to
            // WorkComplete would strand the typed checkpoint without its ref.
            target = item.State;
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
        var attempts = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);
        var fromState = item.State;
        var opts = _opts;
        // MaxRecoveryAttempts <= 0 means unlimited. Only enforce when > 0.
        // Mirrors the DeadWorkerReaper / OrchestratorService budget check so
        // an item that wedges on every pickup is eventually abandoned rather than
        // looping through recovery forever and burning a slot per iteration.
        if (WorkItemRecoveryPolicy.ExceedsRecoveryAttempts(attempts, opts.MaxRecoveryAttempts))
        {
            var failedAt = DateTimeOffset.UtcNow;
            var failedBase = item.HasTypedAgentTurnRecoveryBoundary
                ? WorkItemRecoveryPolicy.ReleaseAgentTurnDispatchClaim(item) with
                {
                    State = WorkItemState.AbandonedAfterRecoveryAttempts,
                    LastError = $"watchdog: exceeded MaxRecoveryAttempts ({opts.MaxRecoveryAttempts}); was {fromState} with no progress for {sinceProgressSeconds}s",
                    StartedAt = null,
                    UpdatedAt = failedAt,
                }
                : item with
                {
                    State = WorkItemState.AbandonedAfterRecoveryAttempts,
                    LastError = $"watchdog: exceeded MaxRecoveryAttempts ({opts.MaxRecoveryAttempts}); was {fromState} with no progress for {sinceProgressSeconds}s",
                    StartedAt = null,
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    AgentTurnResumeCheckpoint = null,
                    AgentTurnRecoveryLease = null,
                    UpdatedAt = failedAt,
                };
            var failed = WorkItemRecoveryPolicy.WithRecoveryAttempt(
                failedBase,
                attempts,
                item.State);
            var wrote = await _store.TryUpdateIfStateAndUpdatedAtAsync(
                failed,
                item.State,
                item.UpdatedAt,
                ct);
            if (!wrote)
                return;
            _cancellations?.CancelForRecovery(item.Id);
            _recoveredWorkers[worker.WorkerId] = 0;
            if (_slotReleaser is not null)
            {
                await _slotReleaser.TryReleaseRecoveredWorkerSlotAsync(
                    worker.WorkerId, item.Id,
                    $"watchdog: exceeded MaxRecoveryAttempts ({opts.MaxRecoveryAttempts}) after {sinceProgressSeconds}s without progress",
                    ct);
            }

            AuditLog.WorkItemWatchdogRecovered(item.Id, worker.WorkerId, fromState, WorkItemState.AbandonedAfterRecoveryAttempts, dependentsRestored: 0);
            _log.LogWarning(
                "Watchdog: work item {ItemId} (worker {WorkerId}) exceeded MaxRecoveryAttempts ({Max}); abandoning for operator triage",
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
        if (target is WorkItemState.Working or WorkItemState.Reworking
            && item.HasAgentTurnRecoveryBoundary)
        {
            updated = WorkItemRecoveryPolicy.WithRecoveryAttempt(
                WorkItemRecoveryPolicy.ReleaseAgentTurnDispatchClaim(item) with
            {
                StartedAt = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, attempts, item.State);
        }
        else
        {
            updated = WorkItemRecoveryPolicy.WithRecoveryAttempt(item with
            {
                State = target,
                LastError = $"watchdog: worker made no progress for {sinceProgressSeconds}s in state {fromState}",
                StartedAt = WorkItemRecoveryPolicy.ShouldClearStartedAtForRecoveryTarget(target) ? null : item.StartedAt,
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
                AgentTurnResumeCheckpoint = target is WorkItemState.Working or WorkItemState.Reworking
                    ? item.AgentTurnResumeCheckpoint
                    : null,
                AgentTurnRecoveryLease = target is WorkItemState.Working or WorkItemState.Reworking
                    ? item.AgentTurnRecoveryLease
                    : null,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, attempts, item.State);
            updated = WorkItemRecoveryPolicy.ClearPlanFieldsIfQueued(updated);
        }

        var recovered = await _store.TryUpdateIfStateAndUpdatedAtAsync(
            updated,
            item.State,
            item.UpdatedAt,
            ct);
        if (!recovered)
            return;

        // Unclaimed legacy turns are still cancelled after the guarded write.
        // Claimed durable turns were already cancelled and confirmed inactive
        // before their claim was released above.
        _cancellations?.CancelForRecovery(item.Id);
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
        var hadDispatchClaim = item.AgentTurnResumeCheckpoint?.DispatchClaimId is not null;
        var quiescedItem = await TryQuiesceDispatchClaimOwnerAsync(worker, item, _opts, ct);
        if (quiescedItem is null)
            return;
        item = quiescedItem;

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
        if (claimed is null && !hadDispatchClaim)
            return;

        var parked = item.With(
            WorkItemState.NeedsOperatorInput,
            $"watchdog: worker made no progress for {sinceProgressSeconds}s in state {item.State}; operator triage required (auto-recover disabled)");
        var wrote = await _store.TryUpdateIfStateAndUpdatedAtAsync(
            parked,
            item.State,
            item.UpdatedAt,
            ct);
        if (!wrote)
            return;
        _cancellations?.CancelForRecovery(item.Id);
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

    private async Task<WorkItem?> TryQuiesceDispatchClaimOwnerAsync(
        WorkerRegistration worker,
        WorkItem item,
        WorkerProgressWatchdogOptions opts,
        CancellationToken ct)
    {
        var expectedClaimId = item.AgentTurnResumeCheckpoint?.DispatchClaimId;
        if (expectedClaimId is null)
            return item;

        // CancellationRegistry is process-local. Releasing a claim owned by a
        // different host or process would allow a new dispatch while the old
        // CLI can still publish terminal state, so leave the claim intact for
        // dead-worker/startup recovery or operator intervention.
        if (!string.Equals(worker.HostName, Environment.MachineName, StringComparison.Ordinal)
            || worker.ProcessId != Environment.ProcessId
            || _cancellations is null)
        {
            _log.LogWarning(
                "Watchdog: refusing to release dispatch claim {ClaimId} for item {ItemId}; worker {WorkerId} is not locally fenceable (host={Host}, pid={ProcessId})",
                expectedClaimId, item.Id, worker.WorkerId, worker.HostName, worker.ProcessId);
            return null;
        }

        if (_cancellations.IsActive(item.Id))
        {
            _cancellations.CancelForRecovery(item.Id);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(opts.PostAgentTransitionTimeout);
            try
            {
                await _cancellations.WaitForInactiveAsync(item.Id, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(
                    "Watchdog: item {ItemId} did not stop within {Timeout}; retaining dispatch claim {ClaimId}",
                    item.Id, opts.PostAgentTransitionTimeout, expectedClaimId);
                return null;
            }
        }

        // The cancelled owner may have completed a legitimate transition while
        // winding down. Recover only the exact snapshot whose claim we fenced.
        var current = await _store.GetAsync(item.Id, ct);
        if (current is null
            || current.State != item.State
            || current.UpdatedAt != item.UpdatedAt
            || current.AgentTurnResumeCheckpoint?.DispatchClaimId != expectedClaimId)
        {
            _log.LogDebug(
                "Watchdog: item {ItemId} advanced while dispatch claim {ClaimId} was being fenced; skipping recovery",
                item.Id, expectedClaimId);
            return null;
        }

        return current;
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
        WorkItemState.Planning => true,
        WorkItemState.PlanReview => true,
        WorkItemState.Working => true,
        WorkItemState.Reworking => true,
        WorkItemState.Auditing => true,
        WorkItemState.Merging => true,
        WorkItemState.ReworkingForConflict => true,
        WorkItemState.UpstreamPushing => true,
        _ => false,
    };
}

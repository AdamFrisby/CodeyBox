using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Periodic background sweep that detects workers whose heartbeat has gone
/// stale and recovers any work items they were holding. Also exposed as a
/// callable method (<see cref="RunOnceAsync"/>) so
/// <see cref="OrchestratorService"/> can invoke it synchronously at startup
/// before the worker pool begins pulling from the queue.
///
/// Idempotency guarantee: <see cref="IWorkerRegistry.ClaimDeadWorkersAsync"/>
/// atomically DELETEs stale rows in one transaction; only the caller that
/// successfully removed a row performs recovery for that worker. Concurrent
/// or restarted reapers are safe.
/// </summary>
public sealed class DeadWorkerReaper : BackgroundService
{
    private readonly IWorkerRegistry _registry;
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly Func<DeadWorkerOptions> _optsAccessor;
    private readonly ILogger<DeadWorkerReaper> _log;
    private readonly IStartupInitialRecoveryBarrier? _startupRecoveryBarrier;
    private readonly ConcurrentDictionary<WorkItemId, byte> _recoveredItemsThisProcess = new();
    private IWorkerPoolRecoverySlotReleaser? _slotReleaser;

    // Resolves the current DeadWorkerOptions value on every read so MaxRecoveryAttempts /
    // DeadWorkerThreshold edits applied via IOptionsMonitor take effect on the next sweep
    // without restarting CodeyBox. PeriodicTimer's interval is fixed at construction so
    // changes to CheckInterval are picked up by the next timer (i.e. next restart) — that
    // limitation is documented on CheckInterval itself.
    private DeadWorkerOptions _opts => _optsAccessor();

    private const string StartupSweepWorkerId = "<orchestrator-startup>";

    public DeadWorkerReaper(
        IWorkerRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        DeadWorkerOptions opts,
        ILogger<DeadWorkerReaper> log,
        IWebhookDispatcher? webhooks = null,
        IWorkerPoolRecoverySlotReleaser? slotReleaser = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null)
        : this(registry, store, queue, () => opts, log, webhooks, slotReleaser, startupRecoveryBarrier) { }

    public DeadWorkerReaper(
        IWorkerRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        Func<DeadWorkerOptions> optionsAccessor,
        ILogger<DeadWorkerReaper> log,
        IWebhookDispatcher? webhooks = null,
        IWorkerPoolRecoverySlotReleaser? slotReleaser = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null)
    {
        _registry = registry;
        _store = store;
        _queue = queue;
        _optsAccessor = optionsAccessor;
        _log = log;
        _webhooks = webhooks;
        _slotReleaser = slotReleaser;
        _startupRecoveryBarrier = startupRecoveryBarrier;
    }

    internal void AttachWorkerPoolSlotReleaser(IWorkerPoolRecoverySlotReleaser slotReleaser)
        => _slotReleaser = slotReleaser;

    internal bool HasRecoveredItemInCurrentProcess(WorkItemId itemId)
        => _recoveredItemsThisProcess.ContainsKey(itemId);

    /// <summary>
    /// Runs a single reaper sweep. Safe to call concurrently or repeatedly;
    /// the registry's atomic DELETE ensures no double-recovery.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - _opts.DeadWorkerThreshold;
            var dead = await _registry.ClaimDeadWorkersAsync(cutoff, ct);
            foreach (var worker in dead)
                await RecoverWorkerAsync(worker, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Dead-worker reaper sweep failed");
        }
    }

    /// <summary>
    /// One-shot startup sweep that finds work items left in a state the reaper
    /// owns (mid-flight worker-owned states plus durable phase-boundary resume
    /// states) with no live worker row holding them, and routes each through
    /// the shared per-item recovery helper.
    ///
    /// <para>
    /// Closes the crash-before-heartbeat edge case: the periodic
    /// <see cref="RunOnceAsync"/> only catches items whose worker row exists
    /// but has gone stale. If the orchestrator crashed between writing the
    /// <c>Working</c> state and the worker-registry row (or the registry
    /// table is otherwise empty), the item is orphaned until the next
    /// periodic sweep — that's minutes of wasted dispatch capacity per
    /// affected item.
    /// </para>
    ///
    /// <para>
    /// Startup-stranded recovery RECLAIMS Working items whose work branch
    /// survives (the bare repo holds it across the restart): the item is
    /// requeued preserving its work branch and
    /// <see cref="WorkItem.PreserveWorkBranchOnQueuedPickup"/> is set so the
    /// next pickup re-rebases the branch onto current upstream main rather
    /// than discarding partial progress. Bounded by
    /// <see cref="DeadWorkerOptions.MaxRecoveryAttempts"/>; once exceeded
    /// the item escalates to
    /// <see cref="WorkItemState.NeedsOperatorInput"/> so it does not loop
    /// burning a slot per restart. Distinct from the periodic / heartbeat-
    /// stale path, which still uses
    /// <see cref="WorkItemRecoveryPolicy.TryBuildWorkingWithoutPreemptFailure"/>
    /// (mark Failed) — a dead worker mid-flight is a different signal from
    /// a clean restart with the work branch intact.
    /// </para>
    ///
    /// <para>
    /// Callers should invoke <see cref="RunOnceAsync"/> first so any stale
    /// registry rows are claimed and deleted; the remaining rows are then
    /// trusted as live and any items they own are left alone here.
    /// </para>
    /// </summary>
    public async Task SweepStrandedItemsAsync(CancellationToken ct)
    {
        try
        {
            // Snapshot the live worker set after the periodic reaper has
            // claimed and deleted any stale rows. Anything still in the
            // registry is therefore treated as a live worker; we must not
            // touch its in-flight item.
            var liveWorkers = await _registry.ListAsync(ct);
            var liveOwnedIds = new HashSet<WorkItemId>();
            foreach (var w in liveWorkers)
            {
                if (string.IsNullOrEmpty(w.CurrentWorkItemId)) continue;
                if (!Guid.TryParse(w.CurrentWorkItemId, out var guid)) continue;
                liveOwnedIds.Add(new WorkItemId(guid));
            }

            foreach (var state in Enum.GetValues<WorkItemState>())
            {
                if (!HandlesRecoveryState(state))
                    continue;

                await foreach (var item in _store.ListByStateAsync(state, ct))
                {
                    if (HasRecoveredItemInCurrentProcess(item.Id))
                        continue;
                    if (liveOwnedIds.Contains(item.Id))
                        continue;
                    await RecoverWorkItemAsync(
                        item,
                        StartupSweepWorkerId,
                        noPreemptFailedReason: "orchestrator restarted while work was in progress without a preempt checkpoint",
                        webhookReason: "orchestrator restart with stranded item",
                        preserveWorkBranchForOrphan: true,
                        ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup stranded-item sweep failed");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_startupRecoveryBarrier is not null)
            await _startupRecoveryBarrier.InitialRecoveryCompleted.WaitAsync(stoppingToken);

        using var timer = new PeriodicTimer(_opts.CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    private async Task RecoverWorkerAsync(WorkerRegistration worker, CancellationToken ct)
    {
        if (worker.CurrentWorkItemId is null)
        {
            _log.LogDebug("Dead worker {WorkerId} (host={Host}) had no active work item; row removed", worker.WorkerId, worker.HostName);
            await ReleaseRecoveredWorkerSlotAsync(worker.WorkerId, null, "dead worker row had no active work item", ct);
            return;
        }

        if (!Guid.TryParse(worker.CurrentWorkItemId, out var guid))
        {
            _log.LogWarning("Dead worker {WorkerId} had malformed work item id '{ItemId}'; skipping", worker.WorkerId, worker.CurrentWorkItemId);
            await ReleaseRecoveredWorkerSlotAsync(worker.WorkerId, null, "dead worker row had malformed work item id", ct);
            return;
        }

        var itemId = new WorkItemId(guid);
        var item = await _store.GetAsync(itemId, ct);
        if (item is null)
        {
            _log.LogWarning("Dead worker {WorkerId} referenced work item {ItemId} which no longer exists", worker.WorkerId, itemId);
            await ReleaseRecoveredWorkerSlotAsync(worker.WorkerId, itemId, "dead worker row referenced a missing work item", ct);
            return;
        }

        await RecoverWorkItemAsync(
            item,
            worker.WorkerId,
            noPreemptFailedReason: "worker died while work phase was running without a preempt checkpoint",
            webhookReason: "dead worker detected",
            preserveWorkBranchForOrphan: false,
            ct);
    }

    /// <summary>
    /// Per-item recovery logic shared by the periodic reaper (called for each
    /// worker whose heartbeat row has been claimed) and the orchestrator's
    /// startup stranded-item sweep (called for each mid-flight item with no
    /// live worker row). State transitions, recovery-attempt accounting, audit
    /// logging, state-changing recovery webhooks, and re-enqueue are identical
    /// across both callers — only the worker-identity log token, the
    /// no-preempt-checkpoint <c>LastError</c> phrasing, the webhook reason,
    /// and the orphan-recovery policy vary.
    ///
    /// <para>
    /// When <paramref name="preserveWorkBranchForOrphan"/> is true (startup
    /// stranded sweep), Working items without a preempt checkpoint are
    /// reclaimed via
    /// <see cref="WorkItemRecoveryPolicy.BuildStaleItemRecovery"/>: requeued
    /// preserving the work branch, with bounded recovery attempts escalating
    /// to <see cref="WorkItemState.NeedsOperatorInput"/>. When false (periodic
    /// dead-worker reaper), the same items are marked
    /// <see cref="WorkItemState.Failed"/> via
    /// <see cref="WorkItemRecoveryPolicy.TryBuildWorkingWithoutPreemptFailure"/>
    /// because a dead worker mid-flight is a different signal — the worker
    /// process is known to be gone, and re-pickup may re-trigger whatever
    /// killed it.
    /// </para>
    /// </summary>
    private async Task RecoverWorkItemAsync(
        WorkItem item,
        string workerIdContext,
        string noPreemptFailedReason,
        string webhookReason,
        bool preserveWorkBranchForOrphan,
        CancellationToken ct)
    {
        var itemId = item.Id;

        if (!string.IsNullOrWhiteSpace(item.SuspendedVmName))
        {
            _log.LogInformation(
                "Recovery ({WorkerId}): work item {ItemId} still references suspended sandbox {VmName}; startup resume owns this item, skipping recovery",
                workerIdContext, itemId, item.SuspendedVmName);
            return;
        }

        if (!string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && item.State is WorkItemState.Working or WorkItemState.Reworking)
        {
            var preempted = item with { StartedAt = null, UpdatedAt = DateTimeOffset.UtcNow };
            await _store.UpdateAsync(preempted, ct);
            await _queue.EnqueueAsync(itemId, ct);
            MarkRecoveredItem(itemId);
            _log.LogInformation(
                "Recovery ({WorkerId}): work item {ItemId} has preempt checkpoint {Ref}; re-enqueued for clean resume",
                workerIdContext, itemId, item.PreemptCheckpoint);
            return;
        }

        if (WorkItemRecoveryPolicy.IsRerunnableCheckAndActWithoutPreempt(item))
        {
            var completed = await CheckAndActFollowupRecovery.TryBuildCompletedFromPersistedVerdictAsync(
                _store, item, ct);
            if (completed is not null)
            {
                await _store.UpdateAsync(completed, ct);
                await CheckAndActFollowupRecovery.EnqueueExistingFollowupIfActionableAsync(
                    _store, _queue, item, ct);
                MarkRecoveredItem(itemId);
                _log.LogInformation(
                    "Recovery ({WorkerId}): check-and-act item {ItemId} already persisted a final verdict; completed without replaying the check",
                    workerIdContext, itemId);
                if (_webhooks is not null)
                {
                    _ = _webhooks.PublishAsync(new WebhookEvent
                    {
                        Event = "work_item.recovered",
                        WorkItem = completed,
                        Details = new
                        {
                            workItemId = itemId.ToString(),
                            projectId = item.ProjectId.Value,
                            fromState = item.State.ToString(),
                            toState = completed.State.ToString(),
                            reason = webhookReason,
                            recoveryAttempt = item.RecoveryAttempts,
                            maxRecoveryAttempts = _opts.MaxRecoveryAttempts,
                        },
                    }, CancellationToken.None);
                }
                await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery completed check-and-act item from persisted verdict", ct);
                return;
            }

            var checkAttempt = item.RecoveryAttempts + 1;
            WorkItem recovered;
            if (_opts.MaxRecoveryAttempts > 0 && checkAttempt > _opts.MaxRecoveryAttempts)
            {
                recovered = item with
                {
                    State = WorkItemState.Failed,
                    LastError = "exceeded MaxRecoveryAttempts",
                    RecoveryAttempts = checkAttempt,
                    StartedAt = null,
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                _log.LogWarning(
                    "Recovery ({WorkerId}): check-and-act item {ItemId} exceeded MaxRecoveryAttempts ({Max}); failing permanently",
                    workerIdContext, itemId, _opts.MaxRecoveryAttempts);
                AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, checkAttempt);
                await _store.UpdateAsync(recovered, ct);
                MarkRecoveredItem(itemId);
                await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery failed interrupted check-and-act item permanently without re-dispatch", ct);
                return;
            }

            recovered = WorkItemRecoveryPolicy.BuildCheckAndActRerun(item, checkAttempt);
            await _store.UpdateAsync(recovered, ct);
            await _queue.EnqueueAsync(itemId, ct);
            MarkRecoveredItem(itemId);
            _log.LogInformation(
                "Recovery ({WorkerId}): check-and-act item {ItemId} was interrupted while Working without a preempt checkpoint; re-enqueued for a fresh check run (attempt {Attempt}/{Max})",
                workerIdContext, itemId, checkAttempt, _opts.MaxRecoveryAttempts);
            if (_webhooks is not null)
            {
                _ = _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.recovered",
                    WorkItem = recovered,
                    Details = new
                    {
                        workItemId = itemId.ToString(),
                        projectId = item.ProjectId.Value,
                        fromState = item.State.ToString(),
                        toState = recovered.State.ToString(),
                        reason = webhookReason,
                        recoveryAttempt = checkAttempt,
                        maxRecoveryAttempts = _opts.MaxRecoveryAttempts,
                    },
                }, CancellationToken.None);
            }
            return;
        }

        if (WorkItemRecoveryPolicy.IsRerunnableAgentControlWithoutPreempt(item))
        {
            var controlAttempt = item.RecoveryAttempts + 1;
            WorkItem recovered;
            if (_opts.MaxRecoveryAttempts > 0 && controlAttempt > _opts.MaxRecoveryAttempts)
            {
                recovered = item with
                {
                    State = WorkItemState.Failed,
                    LastError = "exceeded MaxRecoveryAttempts",
                    RecoveryAttempts = controlAttempt,
                    StartedAt = null,
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                _log.LogWarning(
                    "Recovery ({WorkerId}): agent-control item {ItemId} exceeded MaxRecoveryAttempts ({Max}); failing permanently",
                    workerIdContext, itemId, _opts.MaxRecoveryAttempts);
                AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, controlAttempt);
                await _store.UpdateAsync(recovered, ct);
                MarkRecoveredItem(itemId);
                await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery failed interrupted agent-control item permanently without re-dispatch", ct);
                return;
            }

            recovered = WorkItemRecoveryPolicy.BuildAgentControlRerun(item, controlAttempt);
            await _store.UpdateAsync(recovered, ct);
            await _queue.EnqueueAsync(itemId, ct);
            MarkRecoveredItem(itemId);
            _log.LogInformation(
                "Recovery ({WorkerId}): agent-control item {ItemId} was interrupted while Working; re-enqueued for a fresh control run (attempt {Attempt}/{Max})",
                workerIdContext, itemId, controlAttempt, _opts.MaxRecoveryAttempts);
            if (_webhooks is not null)
            {
                _ = _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.recovered",
                    WorkItem = recovered,
                    Details = new
                    {
                        workItemId = itemId.ToString(),
                        projectId = item.ProjectId.Value,
                        fromState = item.State.ToString(),
                        toState = recovered.State.ToString(),
                        reason = webhookReason,
                        recoveryAttempt = controlAttempt,
                        maxRecoveryAttempts = _opts.MaxRecoveryAttempts,
                    },
                }, CancellationToken.None);
            }
            return;
        }

        if (preserveWorkBranchForOrphan
            && item.State is WorkItemState.Working or WorkItemState.Reworking
            && string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && !WorkItemRecoveryPolicy.IsRerunnableCheckAndActWithoutPreempt(item)
            && !WorkItemRecoveryPolicy.IsRerunnableAgentControlWithoutPreempt(item))
        {
            var orphanAttempt = item.RecoveryAttempts + 1;
            var orphanRecovered = WorkItemRecoveryPolicy.BuildStaleItemRecovery(
                item,
                orphanAttempt,
                _opts.MaxRecoveryAttempts,
                noPreemptFailedReason,
                DateTimeOffset.UtcNow);
            if (orphanRecovered is not null)
            {
                var orphanFromState = item.State;
                var orphanToState = orphanRecovered.State;
                var branchPreserved =
                    orphanToState == WorkItemState.Queued
                    && orphanRecovered.PreserveWorkBranchOnQueuedPickup
                    && !string.IsNullOrWhiteSpace(orphanRecovered.WorkBranch);

                await _store.UpdateAsync(orphanRecovered, ct);
                MarkRecoveredItem(itemId);

                if (orphanToState == WorkItemState.NeedsOperatorInput)
                {
                    _log.LogWarning(
                        "Recovery ({WorkerId}): orphaned Working work item {ItemId} exceeded MaxRecoveryAttempts ({Max}); parked at NeedsOperatorInput for triage",
                        workerIdContext, itemId, _opts.MaxRecoveryAttempts);
                    AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, orphanAttempt);
                    await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "orphan recovery exceeded MaxRecoveryAttempts; parked at NeedsOperatorInput", ct);
                }
                else
                {
                    _log.LogWarning(
                        "Recovery ({WorkerId}): reclaimed orphaned Working work item {ItemId} → {ToState} (attempt {Attempt}/{Max}); work branch {BranchPreservedNote}",
                        workerIdContext, itemId, orphanToState, orphanAttempt, _opts.MaxRecoveryAttempts,
                        branchPreserved ? "preserved for re-pickup rebase" : "not recorded");
                    AuditLog.DeadWorkerRecovered(itemId, workerIdContext, orphanFromState, orphanToState, orphanAttempt);
                }

                if (_webhooks is not null)
                {
                    _ = _webhooks.PublishAsync(new WebhookEvent
                    {
                        Event = "work_item.recovered",
                        WorkItem = orphanRecovered,
                        Details = new
                        {
                            workItemId = itemId.ToString(),
                            projectId = item.ProjectId.Value,
                            fromState = orphanFromState.ToString(),
                            toState = orphanToState.ToString(),
                            reason = webhookReason,
                            recoveryAttempt = orphanAttempt,
                            maxRecoveryAttempts = _opts.MaxRecoveryAttempts,
                            branchPreserved,
                        },
                    }, CancellationToken.None);
                }

                if (orphanToState != WorkItemState.NeedsOperatorInput
                    && orphanToState != WorkItemState.Failed)
                {
                    await _queue.EnqueueAsync(itemId, ct);
                }

                return;
            }
        }

        if (WorkItemRecoveryPolicy.TryBuildWorkingWithoutPreemptFailure(item, noPreemptFailedReason, out var failed))
        {
            await _store.UpdateAsync(failed, ct);
            MarkRecoveredItem(itemId);
            _log.LogWarning(
                "Recovery ({WorkerId}): work item {ItemId} was Working without a preempt checkpoint; marked Failed",
                workerIdContext, itemId);
            await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery marked Working item Failed without re-dispatch", ct);
            return;
        }

        var recoveryTarget = MapToRecoveryState(item.State);
        if (recoveryTarget is null)
        {
            _log.LogInformation(
                "Recovery ({WorkerId}): item {ItemId} in non-recoverable state {State} (already terminal or not worker-owned); no action",
                workerIdContext, itemId, item.State);
            await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery found non-recoverable state and did not re-dispatch", ct);
            return;
        }

        var fromState = item.State;
        var isInterruptedWork = recoveryTarget.Value != fromState;
        var attempt = isInterruptedWork ? item.RecoveryAttempts + 1 : item.RecoveryAttempts;
        WorkItem updated;

        if (isInterruptedWork && attempt > _opts.MaxRecoveryAttempts)
        {
            updated = item with
            {
                State = WorkItemState.Failed,
                LastError = "exceeded MaxRecoveryAttempts",
                RecoveryAttempts = attempt,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _log.LogWarning(
                "Recovery ({WorkerId}): work item {ItemId} exceeded MaxRecoveryAttempts ({Max}); failing permanently",
                workerIdContext, itemId, _opts.MaxRecoveryAttempts);
            AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, attempt);
        }
        else
        {
            updated = item with
            {
                State = recoveryTarget.Value,
                LastError = isInterruptedWork ? null : item.LastError,
                RecoveryAttempts = attempt,
                UpdatedAt = DateTimeOffset.UtcNow,
                // Re-queued items must not appear in-flight to CountInFlightAsync.
                StartedAt = recoveryTarget == WorkItemState.Queued ? null : item.StartedAt,
            };
            _log.LogInformation(
                "Recovery ({WorkerId}): recovering work item {ItemId} from {From} → {To} (attempt {Attempt}/{Max})",
                workerIdContext, itemId, fromState, recoveryTarget, attempt, _opts.MaxRecoveryAttempts);
            AuditLog.DeadWorkerRecovered(itemId, workerIdContext, fromState, recoveryTarget.Value, attempt);
        }

        await _store.UpdateAsync(updated, ct);

        if (_webhooks is not null && isInterruptedWork)
        {
            _ = _webhooks.PublishAsync(new WebhookEvent
            {
                Event = "work_item.recovered",
                WorkItem = updated,
                Details = new
                {
                    workItemId = itemId.ToString(),
                    projectId = item.ProjectId.Value,
                    fromState = fromState.ToString(),
                    toState = updated.State.ToString(),
                    reason = webhookReason,
                    recoveryAttempt = attempt,
                    maxRecoveryAttempts = _opts.MaxRecoveryAttempts,
                },
            }, CancellationToken.None);
        }

        if (updated.State != WorkItemState.Failed)
        {
            await _queue.EnqueueAsync(itemId, ct);
            MarkRecoveredItem(itemId);
        }
        else
        {
            MarkRecoveredItem(itemId);
            await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery failed item permanently without re-dispatch", ct);
        }
    }

    private void MarkRecoveredItem(WorkItemId itemId)
        => _recoveredItemsThisProcess[itemId] = 0;

    private async Task ReleaseRecoveredWorkerSlotAsync(
        string workerId,
        WorkItemId? itemId,
        string reason,
        CancellationToken ct)
    {
        if (_slotReleaser is not null
            && await _slotReleaser.TryReleaseRecoveredWorkerSlotAsync(
                workerId, itemId, reason, ct))
        {
            _log.LogWarning(
                "Recovery ({WorkerId}): released worker-pool slot for item {ItemId}: {Reason}",
                workerId, itemId?.ToString() ?? "<none>", reason);
        }
    }

    internal static bool HandlesRecoveryState(WorkItemState state)
        => WorkItemRecoveryPolicy.HandlesRecoveryState(state);

    /// <summary>
    /// Maps a state for which a stale worker row could exist to the state the
    /// reaper should recover or redispatch it into. Mid-flight states map back
    /// to durable resume points and consume a recovery attempt; phase-boundary
    /// resting states map to themselves and are only re-dispatched. Returns
    /// null for terminal, parked, or otherwise dispatcher-owned states.
    /// </summary>
    internal static WorkItemState? MapToRecoveryState(WorkItemState state)
        => WorkItemRecoveryPolicy.MapToRecoveryState(state);
}

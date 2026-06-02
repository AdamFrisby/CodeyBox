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
    private readonly IStartupSandboxResumeBarrier? _startupResumeBarrier;
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
        IStartupSandboxResumeBarrier? startupResumeBarrier = null)
        : this(registry, store, queue, () => opts, log, webhooks, slotReleaser, startupResumeBarrier) { }

    public DeadWorkerReaper(
        IWorkerRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        Func<DeadWorkerOptions> optionsAccessor,
        ILogger<DeadWorkerReaper> log,
        IWebhookDispatcher? webhooks = null,
        IWorkerPoolRecoverySlotReleaser? slotReleaser = null,
        IStartupSandboxResumeBarrier? startupResumeBarrier = null)
    {
        _registry = registry;
        _store = store;
        _queue = queue;
        _optsAccessor = optionsAccessor;
        _log = log;
        _webhooks = webhooks;
        _slotReleaser = slotReleaser;
        _startupResumeBarrier = startupResumeBarrier;
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
    /// the same recovery helper the periodic reaper uses.
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
        if (_startupResumeBarrier is not null)
            await _startupResumeBarrier.Completion.WaitAsync(stoppingToken);

        using var timer = new PeriodicTimer(_opts.CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await RunOnceAsync(stoppingToken);
    }

    private async Task RecoverWorkerAsync(WorkerRegistration worker, CancellationToken ct)
    {
        if (worker.CurrentWorkItemId is null)
        {
            _log.LogDebug("Dead worker {WorkerId} (host={Host}) had no active work item; row removed", worker.WorkerId, worker.HostName);
            ReleaseRecoveredWorkerSlot(worker.WorkerId, null, "dead worker row had no active work item");
            return;
        }

        if (!Guid.TryParse(worker.CurrentWorkItemId, out var guid))
        {
            _log.LogWarning("Dead worker {WorkerId} had malformed work item id '{ItemId}'; skipping", worker.WorkerId, worker.CurrentWorkItemId);
            ReleaseRecoveredWorkerSlot(worker.WorkerId, null, "dead worker row had malformed work item id");
            return;
        }

        var itemId = new WorkItemId(guid);
        var item = await _store.GetAsync(itemId, ct);
        if (item is null)
        {
            _log.LogWarning("Dead worker {WorkerId} referenced work item {ItemId} which no longer exists", worker.WorkerId, itemId);
            ReleaseRecoveredWorkerSlot(worker.WorkerId, itemId, "dead worker row referenced a missing work item");
            return;
        }

        await RecoverWorkItemAsync(
            item,
            worker.WorkerId,
            noPreemptFailedReason: "worker died while work phase was running without a preempt checkpoint",
            webhookReason: "dead worker detected",
            ct);
    }

    /// <summary>
    /// Per-item recovery logic shared by the periodic reaper (called for each
    /// worker whose heartbeat row has been claimed) and the orchestrator's
    /// startup stranded-item sweep (called for each mid-flight item with no
    /// live worker row). State transitions, recovery-attempt accounting, audit
    /// logging, state-changing recovery webhooks, and re-enqueue are identical
    /// across both callers — only the worker-identity log token, the
    /// no-preempt-checkpoint <c>LastError</c> phrasing, and the webhook reason vary.
    /// </summary>
    private async Task RecoverWorkItemAsync(
        WorkItem item,
        string workerIdContext,
        string noPreemptFailedReason,
        string webhookReason,
        CancellationToken ct)
    {
        var itemId = item.Id;

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

        if (item.State == WorkItemState.Working)
        {
            var failed = item with
            {
                State = WorkItemState.Failed,
                LastError = noPreemptFailedReason,
                RecoveryAttempts = item.RecoveryAttempts + 1,
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _store.UpdateAsync(failed, ct);
            MarkRecoveredItem(itemId);
            _log.LogWarning(
                "Recovery ({WorkerId}): work item {ItemId} was Working without a preempt checkpoint; marked Failed",
                workerIdContext, itemId);
            ReleaseRecoveredWorkerSlot(workerIdContext, itemId, "recovery marked Working item Failed without re-dispatch");
            return;
        }

        var recoveryTarget = MapToRecoveryState(item.State);
        if (recoveryTarget is null)
        {
            _log.LogInformation(
                "Recovery ({WorkerId}): item {ItemId} in non-recoverable state {State} (already terminal or not worker-owned); no action",
                workerIdContext, itemId, item.State);
            ReleaseRecoveredWorkerSlot(workerIdContext, itemId, "recovery found non-recoverable state and did not re-dispatch");
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
            ReleaseRecoveredWorkerSlot(workerIdContext, itemId, "recovery failed item permanently without re-dispatch");
        }
    }

    private void MarkRecoveredItem(WorkItemId itemId)
        => _recoveredItemsThisProcess[itemId] = 0;

    private void ReleaseRecoveredWorkerSlot(string workerId, WorkItemId? itemId, string reason)
    {
        if (_slotReleaser?.TryReleaseRecoveredWorkerSlot(workerId, itemId, reason) == true)
        {
            _log.LogWarning(
                "Recovery ({WorkerId}): released worker-pool slot for item {ItemId}: {Reason}",
                workerId, itemId?.ToString() ?? "<none>", reason);
        }
    }

    internal static bool HandlesRecoveryState(WorkItemState state)
        => state == WorkItemState.Working || MapToRecoveryState(state) is not null;

    /// <summary>
    /// Maps a state for which a stale worker row could exist to the state the
    /// reaper should recover or redispatch it into. Mid-flight states map back
    /// to durable resume points and consume a recovery attempt; phase-boundary
    /// resting states map to themselves and are only re-dispatched. Returns
    /// null for terminal, parked, or otherwise dispatcher-owned states.
    /// </summary>
    internal static WorkItemState? MapToRecoveryState(WorkItemState state) => state switch
    {
        WorkItemState.Reworking => WorkItemState.Queued,
        WorkItemState.WorkComplete => WorkItemState.WorkComplete,
        WorkItemState.Auditing => WorkItemState.WorkComplete,
        WorkItemState.AuditPassed => WorkItemState.AuditPassed,
        WorkItemState.Merging => WorkItemState.AuditPassed,
        WorkItemState.Merged => WorkItemState.Merged,
        // A dead worker mid-ReworkingForConflict resumes from AuditPassed so
        // the merge phase re-runs. The ConflictReworkAttempts counter is
        // preserved so the third-line fallback cannot fire a second time.
        WorkItemState.ReworkingForConflict => WorkItemState.AuditPassed,
        WorkItemState.UpstreamPushing => WorkItemState.Merged,
        _ => null,
    };
}

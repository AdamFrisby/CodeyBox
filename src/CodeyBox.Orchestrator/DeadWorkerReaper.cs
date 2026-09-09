using System.Collections.Concurrent;
using System.Diagnostics;
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
/// Idempotency guarantee: <see cref="IWorkerRegistry.TryClaimDeadWorkerAsync"/>
/// atomically deletes each still-stale row after any durable dispatch owner is
/// fenced. The guarded work-item write is the final recovery election when a
/// locally cancelled owner deregisters while quiescing. Concurrent or
/// restarted reapers are safe.
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
    private readonly CancellationRegistry? _cancellations;
    private readonly Func<int, bool> _localProcessMayBeRunning;
    private readonly DateTimeOffset? _currentProcessStartedAt;
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
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null,
        CancellationRegistry? cancellationRegistry = null,
        Func<int, bool>? localProcessMayBeRunning = null)
        : this(
            registry,
            store,
            queue,
            () => opts,
            log,
            webhooks,
            slotReleaser,
            startupRecoveryBarrier,
            cancellationRegistry,
            localProcessMayBeRunning) { }

    public DeadWorkerReaper(
        IWorkerRegistry registry,
        IWorkItemStore store,
        ITaskQueue queue,
        Func<DeadWorkerOptions> optionsAccessor,
        ILogger<DeadWorkerReaper> log,
        IWebhookDispatcher? webhooks = null,
        IWorkerPoolRecoverySlotReleaser? slotReleaser = null,
        IStartupInitialRecoveryBarrier? startupRecoveryBarrier = null,
        CancellationRegistry? cancellationRegistry = null,
        Func<int, bool>? localProcessMayBeRunning = null)
    {
        _registry = registry;
        _store = store;
        _queue = queue;
        _optsAccessor = optionsAccessor;
        _log = log;
        _webhooks = webhooks;
        _slotReleaser = slotReleaser;
        _startupRecoveryBarrier = startupRecoveryBarrier;
        _cancellations = cancellationRegistry;
        _localProcessMayBeRunning = localProcessMayBeRunning ?? LocalProcessMayBeRunning;
        _currentProcessStartedAt = TryReadCurrentProcessStartedAt();
    }

    internal void AttachWorkerPoolSlotReleaser(IWorkerPoolRecoverySlotReleaser slotReleaser)
        => _slotReleaser = slotReleaser;

    internal bool HasRecoveredItemInCurrentProcess(WorkItemId itemId)
        => _recoveredItemsThisProcess.ContainsKey(itemId);

    /// <summary>
    /// Runs a single reaper sweep. Safe to call concurrently or repeatedly;
    /// the registry claim and guarded work-item write prevent double-recovery.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - _opts.DeadWorkerThreshold;
            var candidates = await _registry.ListAsync(ct);
            foreach (var worker in candidates)
            {
                if (worker.LastHeartbeatAt >= cutoff)
                    continue;
                await TryRecoverStaleWorkerAsync(worker, cutoff, ct);
            }
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
    /// <see cref="WorkItemState.AbandonedAfterRecoveryAttempts"/> so it does not loop
    /// burning a slot per restart. Distinct from the periodic / heartbeat-
    /// stale path, which still uses
    /// <see cref="WorkItemRecoveryPolicy.TryBuildWorkingWithoutPreemptFailure"/>
    /// (mark Failed) — a dead worker mid-flight is a different signal from
    /// a clean restart with the work branch intact.
    /// </para>
    ///
    /// <para>
    /// Callers should invoke <see cref="RunOnceAsync"/> first. Stale rows whose
    /// owners are proven inactive are claimed; unfenceable claimed owners keep
    /// their registry row, so this sweep cannot misclassify them as stranded.
    /// Remaining rows are trusted as live and their items are left alone here.
    /// </para>
    /// </summary>
    public async Task SweepStrandedItemsAsync(CancellationToken ct)
    {
        try
        {
            // Snapshot the worker set after the periodic reaper has claimed
            // every safely recoverable stale row. Unfenceable claimed owners
            // deliberately remain registered alongside live workers; neither
            // category may be treated as a stranded item.
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
                    if (item.AgentTurnResumeCheckpoint?.DispatchClaimId is not null
                        && _cancellations?.IsActive(item.Id) == true)
                    {
                        _log.LogWarning(
                            "Startup recovery: refusing to release dispatch claim for item {ItemId}; a local pipeline is still active",
                            item.Id);
                        continue;
                    }
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

    private async Task TryRecoverStaleWorkerAsync(
        WorkerRegistration candidate,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        WorkItem? fencedItem = null;
        if (candidate.CurrentWorkItemId is { } currentWorkItemId
            && Guid.TryParse(currentWorkItemId, out var guid))
        {
            var item = await _store.GetAsync(new WorkItemId(guid), ct);
            if (item?.AgentTurnResumeCheckpoint?.DispatchClaimId is not null)
            {
                // Keep the registry row durable until the owner is fenced. A
                // remote or still-running process must remain visible to every
                // concurrent/startup sweep; deleting first would let a peer
                // mistake the claimed item for an orphan.
                fencedItem = await TryFenceDispatchClaimOwnerAsync(candidate, item, ct);
                if (fencedItem is null)
                    return;
            }
        }

        var claimed = await _registry.TryClaimDeadWorkerAsync(candidate.WorkerId, cutoff, ct);
        if (claimed is null && fencedItem is null)
            return;

        // A locally cancelled owner can deregister while the reaper waits for
        // quiescence, and a concurrent reaper can win the row claim. The work-
        // item state/updatedAt CAS remains the recovery election for an owner
        // already proven inactive.
        await RecoverWorkerAsync(claimed ?? candidate, ct, fencedItem);
    }

    private async Task RecoverWorkerAsync(
        WorkerRegistration worker,
        CancellationToken ct,
        WorkItem? fencedItem = null)
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
        var item = fencedItem ?? await _store.GetAsync(itemId, ct);
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

    private async Task<WorkItem?> TryFenceDispatchClaimOwnerAsync(
        WorkerRegistration worker,
        WorkItem item,
        CancellationToken ct)
    {
        var expectedClaimId = item.AgentTurnResumeCheckpoint?.DispatchClaimId;
        if (expectedClaimId is null)
            return item;

        var isLocalHost = string.Equals(
            worker.HostName,
            Environment.MachineName,
            StringComparison.OrdinalIgnoreCase);
        if (!isLocalHost)
        {
            return RefuseUnfencedClaimRecovery(
                worker,
                item,
                expectedClaimId.Value,
                "worker belongs to a different host");
        }

        var isCurrentProcessOwner = worker.ProcessId == Environment.ProcessId
            && _currentProcessStartedAt is { } currentProcessStartedAt
            && worker.StartedAt >= currentProcessStartedAt;
        if (isCurrentProcessOwner)
        {
            if (_cancellations is null || !_cancellations.CancelForRecovery(item.Id))
            {
                return RefuseUnfencedClaimRecovery(
                    worker,
                    item,
                    expectedClaimId.Value,
                    "current-process owner has no active cancellation registration");
            }

            var quiescenceTimeout = _opts.DeadWorkerThreshold;
            if (quiescenceTimeout <= TimeSpan.Zero)
            {
                return RefuseUnfencedClaimRecovery(
                    worker,
                    item,
                    expectedClaimId.Value,
                    "no positive claim-owner quiescence bound is configured");
            }

            using var quiescenceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                quiescenceCts.CancelAfter(quiescenceTimeout);
                await _cancellations.WaitForInactiveAsync(item.Id, quiescenceCts.Token);
            }
            catch (OperationCanceledException) when (
                !ct.IsCancellationRequested
                && quiescenceCts.IsCancellationRequested)
            {
                return RefuseUnfencedClaimRecovery(
                    worker,
                    item,
                    expectedClaimId.Value,
                    $"local owner did not quiesce within {quiescenceTimeout}");
            }
            catch (ArgumentOutOfRangeException)
            {
                return RefuseUnfencedClaimRecovery(
                    worker,
                    item,
                    expectedClaimId.Value,
                    "configured claim-owner quiescence bound is outside the supported timer range");
            }

            if (_cancellations.IsActive(item.Id))
            {
                return RefuseUnfencedClaimRecovery(
                    worker,
                    item,
                    expectedClaimId.Value,
                    "a local owner became active after cancellation quiescence");
            }
        }
        else if (worker.ProcessId == Environment.ProcessId)
        {
            if (_currentProcessStartedAt is null)
            {
                return RefuseUnfencedClaimRecovery(
                    worker,
                    item,
                    expectedClaimId.Value,
                    "current process start time is unavailable, so PID reuse cannot be excluded");
            }

            // The same numeric PID belongs to this process now, but the worker
            // registration predates this process epoch. The prior owner cannot
            // still be running, which preserves recovery across container/PID-1
            // restarts without canceling an unrelated current registration.
        }
        else if (_localProcessMayBeRunning(worker.ProcessId))
        {
            return RefuseUnfencedClaimRecovery(
                worker,
                item,
                expectedClaimId.Value,
                "same-host owner process may still be running");
        }

        // Cancellation or process-exit observation is only evidence about the
        // snapshot we inspected. The old owner may have completed a legitimate
        // transition while winding down, so never recover a changed claim.
        var current = await _store.GetAsync(item.Id, ct);
        if (current is null
            || current.State != item.State
            || current.UpdatedAt != item.UpdatedAt
            || current.AgentTurnResumeCheckpoint?.DispatchClaimId != expectedClaimId)
        {
            _log.LogDebug(
                "Dead-worker recovery: item {ItemId} advanced while dispatch claim {ClaimId} was being fenced; skipping recovery",
                item.Id,
                expectedClaimId);
            return null;
        }

        return current;
    }

    private WorkItem? RefuseUnfencedClaimRecovery(
        WorkerRegistration worker,
        WorkItem item,
        Guid claimId,
        string reason)
    {
        _log.LogWarning(
            "Dead-worker recovery: retaining dispatch claim {ClaimId} for item {ItemId}; worker {WorkerId} cannot be fenced safely (host={Host}, pid={ProcessId}): {Reason}",
            claimId,
            item.Id,
            worker.WorkerId,
            worker.HostName,
            worker.ProcessId,
            reason);
        return null;
    }

    private static bool LocalProcessMayBeRunning(int processId)
    {
        if (processId <= 0)
            return true;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception ex) when (ex is
            InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or System.Security.SecurityException)
        {
            // Permission, platform, and process-inspection failures are not
            // proof of death. Fail closed so a live owner is never duplicated.
            return true;
        }
    }

    private static DateTimeOffset? TryReadCurrentProcessStartedAt()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime).ToUniversalTime();
        }
        catch (Exception ex) when (ex is
            InvalidOperationException
            or System.ComponentModel.Win32Exception
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return null;
        }
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
    /// reclaimed preserving the work branch until the shared recovery cap is
    /// exceeded; cap exhaustion transitions to
    /// <see cref="WorkItemState.AbandonedAfterRecoveryAttempts"/>. When false
    /// (periodic dead-worker reaper), the same items are marked
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

        if (item.HasAgentTurnRecoveryBoundary
            && item.State is WorkItemState.Working or WorkItemState.Reworking)
        {
            var preemptAttempt = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);
            var preempted = WorkItemRecoveryPolicy.BuildPreemptCheckpointRecovery(
                item,
                preemptAttempt,
                _opts.MaxRecoveryAttempts,
                DateTimeOffset.UtcNow,
                "exceeded MaxRecoveryAttempts");
            var recoveryWritten = await _store.TryUpdateIfStateAndUpdatedAtAsync(
                preempted,
                item.State,
                item.UpdatedAt,
                ct);
            if (!recoveryWritten)
            {
                _log.LogInformation(
                    "Recovery ({WorkerId}): checkpointed work item {ItemId} advanced before the guarded recovery write; skipping",
                    workerIdContext,
                    itemId);
                return;
            }
            MarkRecoveredItem(itemId);

            if (preempted.State == WorkItemState.AbandonedAfterRecoveryAttempts)
            {
                _log.LogWarning(
                    "Recovery ({WorkerId}): preempt-checkpointed work item {ItemId} exceeded MaxRecoveryAttempts ({Max}); abandoning for operator triage",
                    workerIdContext, itemId, _opts.MaxRecoveryAttempts);
                AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, preemptAttempt);
                await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "checkpoint recovery exceeded MaxRecoveryAttempts; abandoned permanently", ct);
            }
            else
            {
                await _queue.EnqueueAsync(itemId, ct);
                _log.LogInformation(
                    "Recovery ({WorkerId}): work item {ItemId} has preempt checkpoint {Ref}; re-enqueued for clean resume (attempt {Attempt}/{Max})",
                    workerIdContext,
                    itemId,
                    item.PreemptCheckpoint ?? item.AgentTurnRecoveryLease!.SandboxId,
                    preemptAttempt,
                    _opts.MaxRecoveryAttempts);
            }

            if (_webhooks is not null)
            {
                _ = _webhooks.PublishAsync(new WebhookEvent
                {
                    Event = "work_item.recovered",
                    WorkItem = preempted,
                    Details = new
                    {
                        workItemId = itemId.ToString(),
                        projectId = item.ProjectId.Value,
                        fromState = item.State.ToString(),
                        toState = preempted.State.ToString(),
                        reason = webhookReason,
                        recoveryAttempt = preemptAttempt,
                        maxRecoveryAttempts = _opts.MaxRecoveryAttempts,
                    },
                }, CancellationToken.None);
            }
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

            var checkAttempt = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);
            WorkItem recovered;
            if (WorkItemRecoveryPolicy.ExceedsRecoveryAttempts(checkAttempt, _opts.MaxRecoveryAttempts))
            {
                recovered = WorkItemRecoveryPolicy.WithRecoveryAttempt(item with
                {
                    State = WorkItemState.AbandonedAfterRecoveryAttempts,
                    LastError = "exceeded MaxRecoveryAttempts",
                    StartedAt = null,
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    AgentTurnResumeCheckpoint = null,
                    AgentTurnRecoveryLease = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, checkAttempt, item.State);
                _log.LogWarning(
                    "Recovery ({WorkerId}): check-and-act item {ItemId} exceeded MaxRecoveryAttempts ({Max}); abandoning for operator triage",
                    workerIdContext, itemId, _opts.MaxRecoveryAttempts);
                AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, checkAttempt);
                await _store.UpdateAsync(recovered, ct);
                MarkRecoveredItem(itemId);
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
                await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery abandoned interrupted check-and-act item permanently without re-dispatch", ct);
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
            var controlAttempt = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);
            WorkItem recovered;
            if (WorkItemRecoveryPolicy.ExceedsRecoveryAttempts(controlAttempt, _opts.MaxRecoveryAttempts))
            {
                recovered = WorkItemRecoveryPolicy.WithRecoveryAttempt(item with
                {
                    State = WorkItemState.AbandonedAfterRecoveryAttempts,
                    LastError = "exceeded MaxRecoveryAttempts",
                    StartedAt = null,
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    AgentTurnResumeCheckpoint = null,
                    AgentTurnRecoveryLease = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                }, controlAttempt, item.State);
                _log.LogWarning(
                    "Recovery ({WorkerId}): agent-control item {ItemId} exceeded MaxRecoveryAttempts ({Max}); abandoning for operator triage",
                    workerIdContext, itemId, _opts.MaxRecoveryAttempts);
                AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, controlAttempt);
                await _store.UpdateAsync(recovered, ct);
                MarkRecoveredItem(itemId);
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
                await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery abandoned interrupted agent-control item permanently without re-dispatch", ct);
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
            && !item.HasAgentTurnRecoveryBoundary
            && !WorkItemRecoveryPolicy.IsRerunnableCheckAndActWithoutPreempt(item)
            && !WorkItemRecoveryPolicy.IsRerunnableAgentControlWithoutPreempt(item))
        {
            var orphanAttempt = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);
            var orphanNow = DateTimeOffset.UtcNow;
            var orphanRecovered = WorkItemRecoveryPolicy.ExceedsRecoveryAttempts(orphanAttempt, _opts.MaxRecoveryAttempts)
                ? WorkItemRecoveryPolicy.WithRecoveryAttempt(item with
                {
                    State = WorkItemState.AbandonedAfterRecoveryAttempts,
                    LastError = "exceeded MaxRecoveryAttempts",
                    StartedAt = null,
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    AgentTurnResumeCheckpoint = null,
                    AgentTurnRecoveryLease = null,
                    UpdatedAt = orphanNow,
                }, orphanAttempt, item.State)
                : WorkItemRecoveryPolicy.BuildStaleItemRecovery(
                    item,
                    orphanAttempt,
                    _opts.MaxRecoveryAttempts,
                    noPreemptFailedReason,
                    orphanNow);
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

                if (orphanToState == WorkItemState.AbandonedAfterRecoveryAttempts)
                {
                    _log.LogWarning(
                        "Recovery ({WorkerId}): orphaned Working/Reworking work item {ItemId} exceeded MaxRecoveryAttempts ({Max}); abandoning for operator triage",
                        workerIdContext, itemId, _opts.MaxRecoveryAttempts);
                    AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, orphanAttempt);
                    await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "orphan recovery exceeded MaxRecoveryAttempts; abandoned permanently", ct);
                }
                else if (orphanToState == WorkItemState.NeedsOperatorInput)
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

                if (orphanToState != WorkItemState.AbandonedAfterRecoveryAttempts
                    && orphanToState != WorkItemState.NeedsOperatorInput
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
        var attempt = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);
        WorkItem updated;

        if (WorkItemRecoveryPolicy.ExceedsRecoveryAttempts(attempt, _opts.MaxRecoveryAttempts))
        {
            updated = WorkItemRecoveryPolicy.WithRecoveryAttempt(item with
            {
                State = WorkItemState.AbandonedAfterRecoveryAttempts,
                LastError = "exceeded MaxRecoveryAttempts",
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                AgentTurnResumeCheckpoint = null,
                AgentTurnRecoveryLease = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            }, attempt, item.State);
            _log.LogWarning(
                "Recovery ({WorkerId}): work item {ItemId} exceeded MaxRecoveryAttempts ({Max}); abandoning for operator triage",
                workerIdContext, itemId, _opts.MaxRecoveryAttempts);
            AuditLog.DeadWorkerFailedTerminal(itemId, workerIdContext, attempt);
        }
        else
        {
            updated = WorkItemRecoveryPolicy.WithRecoveryAttempt(item with
            {
                State = recoveryTarget.Value,
                LastError = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                AgentTurnResumeCheckpoint = null,
                AgentTurnRecoveryLease = null,
                UpdatedAt = DateTimeOffset.UtcNow,
                // Re-dispatchable recovery targets must not appear in-flight to CountInFlightAsync.
                StartedAt = WorkItemRecoveryPolicy.ShouldClearStartedAtForRecoveryTarget(recoveryTarget.Value)
                    ? null
                    : item.StartedAt,
            }, attempt, item.State);
            updated = WorkItemRecoveryPolicy.ClearPlanFieldsIfQueued(updated);
            _log.LogInformation(
                "Recovery ({WorkerId}): recovering work item {ItemId} from {From} → {To} (attempt {Attempt}/{Max})",
                workerIdContext, itemId, fromState, recoveryTarget, attempt, _opts.MaxRecoveryAttempts);
            AuditLog.DeadWorkerRecovered(itemId, workerIdContext, fromState, recoveryTarget.Value, attempt);
        }

        await _store.UpdateAsync(updated, ct);

        if (_webhooks is not null)
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

        if (updated.State != WorkItemState.AbandonedAfterRecoveryAttempts)
        {
            await _queue.EnqueueAsync(itemId, ct);
            MarkRecoveredItem(itemId);
        }
        else
        {
            MarkRecoveredItem(itemId);
            await ReleaseRecoveredWorkerSlotAsync(workerIdContext, itemId, "recovery abandoned item permanently without re-dispatch", ct);
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
    /// resting states map to themselves and also consume a recovery attempt.
    /// Returns null for terminal, parked, or otherwise dispatcher-owned states.
    /// </summary>
    internal static WorkItemState? MapToRecoveryState(WorkItemState state)
        => WorkItemRecoveryPolicy.MapToRecoveryState(state);
}

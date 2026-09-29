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
/// <see cref="WorkerProgressWatchdogOptions.ItemStaleTimeout"/>. An item past
/// the <c>UpdatedAt</c> cutoff is still classified as stale only when its
/// agent shows no other liveness either: no recent append to its captured
/// agent-stream files (the direct "still producing output" signal) and no
/// newly-observed sandbox activity for its bound worker. A long turn that
/// never stamps <c>UpdatedAt</c> mid-turn but keeps appending stream output
/// is alive and must not be parked. A run producing no output for the
/// configured interval is still stale and is recovered.
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
/// Liveness inputs are deliberately narrower than the per-worker watchdog's.
/// Heartbeat and host CPU activity are ignored here on purpose: the
/// reconnect-loop wedge this detector was built for keeps heartbeating and
/// burning CPU while the item is frozen, so either signal would mask it.
/// Sandbox activity counts only as a same-sweep observation (a newly reported
/// sandbox state), never as stable ownership — a wedge holding its VM open
/// must still trip once its output goes quiet.
/// </para>
///
/// <para>
/// An item with no bound worker that the dispatcher evaluated recently (any
/// quota / cap / budget deferral or pickup inside
/// <c>ItemStaleDispatchQuietTimeout</c>, or still held deferred) is waiting
/// behind quota/cap — not wedged — and never consumes recovery attempts.
/// Only a no-worker item the dispatcher has stopped evaluating has fallen
/// out of dispatch; that is the real wedge case. A continuous quota-blocked
/// wait past <c>ItemQuotaWaitNoticeThreshold</c> emits one informational
/// notice per episode instead of a recovery.
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
    private readonly IAgentStreamStore? _streams;
    private readonly IWorkerProgressActivitySource? _activitySource;
    private readonly IItemDispatchLivenessSource? _dispatchLiveness;
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

    // Quota-wait episodes already notified, keyed by the episode's Since
    // stamp. A repeat sweep for the same episode is a no-op (once per
    // threshold crossing); when the episode ends the entry is cleared so a
    // later episode notifies fresh.
    private readonly ConcurrentDictionary<WorkItemId, DateTimeOffset> _quotaWaitNotified = new();

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
        TimeProvider? timeProvider = null,
        IAgentStreamStore? streams = null,
        IWorkerProgressActivitySource? activitySource = null,
        IItemDispatchLivenessSource? dispatchLiveness = null)
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
        _streams = streams;
        _activitySource = activitySource;
        _dispatchLiveness = dispatchLiveness;
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
        TimeProvider? timeProvider = null,
        IAgentStreamStore? streams = null,
        IWorkerProgressActivitySource? activitySource = null,
        IItemDispatchLivenessSource? dispatchLiveness = null)
        : this(store, queue, registry, () => opts, log, webhooks, slotReleaser, startupRecoveryBarrier, cancellations, timeProvider, streams, activitySource, dispatchLiveness) { }

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

    private static bool HasPerAgentItemStaleOverride(WorkerProgressWatchdogOptions opts)
    {
        foreach (var (_, per) in opts.PerAgent)
        {
            if (per?.ItemStaleTimeout is { } it && it > TimeSpan.Zero)
                return true;
        }
        return false;
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
    /// state and recovers items whose <c>UpdatedAt</c> has not advanced
    /// inside <see cref="WorkerProgressWatchdogOptions.ItemStaleTimeout"/>
    /// AND whose agent shows no other liveness (no recent agent-stream
    /// append, no newly-observed sandbox activity for the bound worker).
    /// An item past the <c>UpdatedAt</c> cutoff whose agent is still
    /// producing output is alive, not stale: the sweep skips it without
    /// touching <c>RecoveryAttempts</c>.
    /// Idempotent: an item already recovered in this process is skipped so
    /// the re-pickup window cannot double-recover before the new pickup
    /// stamps <c>UpdatedAt</c>.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var opts = _opts;
        // Per-agent overrides may keep some kinds active even when the global
        // ItemStaleTimeout is disabled, so the global short-circuit is
        // conditional on no opt-in overrides existing either. The quota-wait
        // notice below is independent of this gate: it has its own threshold
        // and sentinel, so disabling the stale detector does not silence it.
        var staleSweepEnabled =
            opts.ItemStaleTimeout > TimeSpan.Zero || HasPerAgentItemStaleOverride(opts);

        try
        {
            var now = _time.GetUtcNow();

            if (!staleSweepEnabled)
            {
                // Stale detector disabled, but the quota-wait notice has its
                // own threshold and sentinel — it still runs.
                await EmitQuotaWaitNoticesAsync(opts, now, ct);
                return;
            }

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

                    var effectiveTimeout = opts.ResolveItemStaleTimeout(item.Agent);
                    if (effectiveTimeout <= TimeSpan.Zero) continue;

                    var cutoff = now - effectiveTimeout;
                    if (item.UpdatedAt > cutoff)
                        continue;

                    // Resolve the bound worker once: both the sandbox
                    // liveness probe and the dispatch-liveness guard need it.
                    var boundWorker = await FindBoundWorkerAsync(item.Id, ct);

                    // UpdatedAt is frozen past the threshold, but that alone
                    // does not prove the agent is hung: a long turn may never
                    // stamp UpdatedAt mid-turn while still appending stream
                    // output. Classify as stale only when the agent shows no
                    // other liveness either. Skipping here leaves
                    // RecoveryAttempts untouched — a live run must not consume
                    // the recovery budget.
                    var liveness = await ObserveLivenessAsync(item, opts, cutoff, boundWorker, ct);
                    if (liveness.IsAlive)
                    {
                        _log.LogDebug(
                            "Item-stale sweep: work item {ItemId} UpdatedAt frozen for {SinceUpdated}s but agent is alive ({AliveReason}); not stale",
                            item.Id, (long)(now - item.UpdatedAt).TotalSeconds, liveness.AliveReason);
                        continue;
                    }

                    // Dispatch-liveness guard: an item with no bound worker
                    // that the dispatcher evaluated recently (any quota / cap /
                    // budget deferral or pickup inside
                    // ItemStaleDispatchQuietTimeout) or still holds deferred
                    // is waiting behind quota/cap — not wedged. Skipping here
                    // leaves RecoveryAttempts untouched so a long quota stall
                    // can never consume the recovery budget or park the item.
                    // Only an item the dispatcher has stopped evaluating has
                    // fallen out of dispatch: that is the real wedge case.
                    if (boundWorker is null
                        && IsDispatchAlive(item.Id, opts, now, out var dispatchReason))
                    {
                        _log.LogDebug(
                            "Item-stale sweep: work item {ItemId} UpdatedAt frozen for {SinceUpdated}s but dispatcher is active ({DispatchReason}); waiting, not stale",
                            item.Id, (long)(now - item.UpdatedAt).TotalSeconds, dispatchReason);
                        continue;
                    }

                    var sinceUpdated = (long)(now - item.UpdatedAt).TotalSeconds;
                    await RecoverItemAsync(
                        item,
                        reason:
                            $"item-stale: state {item.State}, UpdatedAt frozen for {sinceUpdated}s (threshold {(long)effectiveTimeout.TotalSeconds}s); " +
                            $"lastStreamWrite={liveness.StreamEvidence}; sandbox={liveness.SandboxEvidence}; " +
                            $"lastTransition={item.State}@{item.UpdatedAt:O}",
                        trigger: "watchdog",
                        ct);
                }
            }

            // Long quota waits surface an informational notice on their own
            // threshold, independent of the UpdatedAt cutoff above — a quota
            // wait typically outlasts the notice threshold (default 1 h) well
            // before it could look stale (default 2.5 h). Driven by the
            // dispatch record (not the watched-state walk) so Queued items —
            // which the stale detector never watches — are covered too.
            await EmitQuotaWaitNoticesAsync(opts, now, ct);
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
    ///   <item>When a durable agent-turn checkpoint has an active dispatch
    ///         claim, recovery-cancel its locally registered pipeline and wait
    ///         for bounded quiescence. A missing local owner or a pipeline that
    ///         does not quiesce leaves the claim and item untouched.</item>
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

        var ownerFence = await FenceClaimedCheckpointOwnerAsync(current, opts, ct);
        if (!ownerFence.CanRecover)
        {
            var observed = ownerFence.Current ?? current;
            return new RecoveryResult(
                Recovered: false,
                FromState: observed.State,
                NewState: null,
                Attempt: observed.RecoveryAttempts,
                BranchPreserved: false,
                Error: ownerFence.Error);
        }
        current = ownerFence.Current!;

        var attempts = WorkItemRecoveryPolicy.NextRecoveryAttempt(current);
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
        // The branch survives whenever the recovered row still points at the
        // same branch the wedged run produced. That covers both the
        // preserve-on-requeue path (Working → Queued with
        // PreserveWorkBranchOnQueuedPickup) and the same-state phase-boundary
        // recoveries (WorkComplete / AuditPassed / Merged / PlanReview /
        // PlanApproved map to themselves, so the branch trivially rides
        // through into the re-entered phase). Comparing against the pre-write
        // snapshot is exact: a recovery that regenerates or clears the branch
        // (Working without a branch, watchdog Working → Queued) reports false.
        var branchPreserved =
            !string.IsNullOrWhiteSpace(recovered.WorkBranch)
            && string.Equals(recovered.WorkBranch, current.WorkBranch, StringComparison.Ordinal)
            && (toState != WorkItemState.Queued || recovered.PreserveWorkBranchOnQueuedPickup);

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
            "Item-stale ({Trigger}) recovered work item {ItemId} (worker {WorkerId}) from {FromState} → {ToState} (attempt {Attempt}/{Max}); branchPreserved={BranchPreserved}; {Reason}",
            trigger, item.Id, workerId ?? "<no-live-worker>", fromState, toState, attempts, opts.ItemStaleMaxRecoveryAttempts, branchPreserved, reason);

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
                        detail = reason,
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

    /// <summary>
    /// Liveness evidence consulted before classifying an <c>UpdatedAt</c>-frozen
    /// item as stale. Recorded into the stale-park reason
    /// (<c>LastError</c>, audit log, webhook) so the next operator can tell a
    /// real hang from a long turn without reconstructing it from file
    /// timestamps.
    /// </summary>
    private sealed record ItemLiveness(
        bool IsAlive,
        string? AliveReason,
        string StreamEvidence,
        string SandboxEvidence);

    /// <summary>
    /// Checks whether an <c>UpdatedAt</c>-frozen item's agent is still alive.
    /// Stream recency is the primary signal: an agent appending captured
    /// output is producing output and must not be parked. Sandbox activity is
    /// the secondary signal, observed through
    /// <see cref="IWorkerProgressActivitySource"/> for the bound worker with
    /// the process-CPU leg disabled — a reconnect-loop wedge burns CPU while
    /// frozen, so CPU liveness would defeat this detector; only a newly
    /// reported sandbox state counts, never stable ownership. Orphaned items
    /// (no bound worker row) rely on stream evidence alone.
    /// </summary>
    private async Task<ItemLiveness> ObserveLivenessAsync(
        WorkItem item,
        WorkerProgressWatchdogOptions opts,
        DateTimeOffset cutoff,
        WorkerRegistration? boundWorker,
        CancellationToken ct)
    {
        var lastStreamAt = await GetLastStreamActivityAsync(item.Id, ct);
        string streamEvidence = lastStreamAt is null
            ? (_streams is null ? "streams-unavailable" : "none")
            : $"{lastStreamAt:O} ({(long)(_time.GetUtcNow() - lastStreamAt.Value).TotalSeconds}s ago)";
        if (lastStreamAt is not null && lastStreamAt > cutoff)
        {
            return new ItemLiveness(
                IsAlive: true,
                AliveReason: $"stream-write {lastStreamAt:O}",
                StreamEvidence: streamEvidence,
                SandboxEvidence: "not-consulted");
        }

        var (sandboxAlive, sandboxEvidence) = await ObserveSandboxLivenessAsync(item, opts, boundWorker, ct);
        if (sandboxAlive)
        {
            return new ItemLiveness(
                IsAlive: true,
                AliveReason: sandboxEvidence,
                StreamEvidence: streamEvidence,
                SandboxEvidence: sandboxEvidence);
        }

        return new ItemLiveness(
            IsAlive: false,
            AliveReason: null,
            StreamEvidence: streamEvidence,
            SandboxEvidence: sandboxEvidence);
    }

    private async Task<(bool Alive, string Evidence)> ObserveSandboxLivenessAsync(
        WorkItem item,
        WorkerProgressWatchdogOptions opts,
        WorkerRegistration? boundWorker,
        CancellationToken ct)
    {
        if (_activitySource is null)
            return (false, "activity-source-unavailable");
        if (!opts.ActiveSandboxProgressSignalEnabled)
            return (false, "sandbox-signal-disabled");

        var worker = boundWorker ?? await FindBoundWorkerAsync(item.Id, ct);
        if (worker is null)
            return (false, "no-bound-worker");

        // CPU leg off: this detector exists for wedges that stay CPU-active
        // while the item is frozen. See ObserveLivenessAsync.
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: false,
            ActiveSandboxProgressSignalEnabled: true);
        WorkerProgressActivity? activity;
        try
        {
            activity = await _activitySource.ObserveAsync(worker, item.Id, probe, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Item-stale sweep: failed to read sandbox activity for {ItemId}; treating as no activity", item.Id);
            return (false, "sandbox-probe-failed");
        }

        return activity is not null
            ? (true, activity.Reason)
            : (false, "no-sandbox-activity");
    }

    /// <summary>
    /// Dispatch-liveness guard for items with no bound worker. Returns true
    /// when the dispatcher is actively handling the item: it still holds the
    /// item deferred, or it evaluated the item inside
    /// <see cref="WorkerProgressWatchdogOptions.ItemStaleDispatchQuietTimeout"/>.
    /// Such an item is waiting behind quota/cap/budget — not wedged. A
    /// missing dispatch record (or a record older than the quiet window with
    /// no live deferral) means the item has fallen out of dispatch: the real
    /// wedge case, and the caller may treat it as stale.
    /// </summary>
    private bool IsDispatchAlive(
        WorkItemId itemId,
        WorkerProgressWatchdogOptions opts,
        DateTimeOffset now,
        out string reason)
    {
        reason = "";
        if (_dispatchLiveness is null)
            return false;
        if (!_dispatchLiveness.TryGetLiveness(itemId, out var liveness))
            return false;
        if (liveness.IsDeferred)
        {
            reason = "dispatcher holds it deferred";
            return true;
        }
        var quietTimeout = opts.ItemStaleDispatchQuietTimeout;
        if (quietTimeout > TimeSpan.Zero
            && now - liveness.LastEvaluatedAt <= quietTimeout)
        {
            reason =
                $"dispatcher evaluated it {(long)(now - liveness.LastEvaluatedAt).TotalSeconds}s ago " +
                $"(quiet window {(long)quietTimeout.TotalSeconds}s)";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Emits one informational quota-wait notice per continuous quota-blocked
    /// episode once the episode outlasts
    /// <see cref="WorkerProgressWatchdogOptions.ItemQuotaWaitNoticeThreshold"/>.
    /// The item is never parked for this — the notice (audit event + webhook,
    /// mirrored in queue status) exists so a long quota stall is visible as
    /// waiting rather than mistaken for a wedge. Repeat sweeps inside the
    /// same episode are no-ops; when the episode ends the marker is cleared
    /// so a later episode notifies fresh. Only items still in a
    /// dispatch-awaiting state (Queued or an item-stale watched state) with
    /// no bound worker notify — a picked-up, parked, or terminal item with a
    /// leftover record must not page the operator.
    /// </summary>
    private async Task EmitQuotaWaitNoticesAsync(
        WorkerProgressWatchdogOptions opts,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var threshold = opts.ItemQuotaWaitNoticeThreshold;
        if (threshold <= TimeSpan.Zero || _dispatchLiveness is null)
            return;

        var waits = _dispatchLiveness.GetQuotaWaits();
        var liveIds = new HashSet<WorkItemId>();
        foreach (var wait in waits)
        {
            liveIds.Add(wait.WorkItemId);
            if (_quotaWaitNotified.TryGetValue(wait.WorkItemId, out var notified)
                && notified == wait.Since)
                continue;
            if (now - wait.Since < threshold)
                continue;

            var item = await _store.GetAsync(wait.WorkItemId, ct);
            if (item is null)
            {
                _dispatchLiveness.NoteRemoved(wait.WorkItemId);
                continue;
            }
            if (!IsQuotaWaitNotifiableState(item.State))
                continue;

            // A worker may have picked the item up since the episode stamp;
            // the bound-worker path owns it from there, so only notify for
            // items that are still waiting. The re-check is cheap and runs at
            // most once per threshold crossing per episode.
            if (await FindBoundWorkerAsync(item.Id, ct) is not null)
                continue;

            var agent = wait.Agent;
            if (string.IsNullOrWhiteSpace(agent) || agent == "eligible agents")
                agent = item.Agent?.Value ?? agent;

            AuditLog.ItemWaitingOnQuota(item.Id, agent, wait.Since);
            _log.LogInformation(
                "Item-stale sweep: work item {ItemId} waiting on quota for {Agent} since {Since:O} ({WaitSeconds}s >= {ThresholdSeconds}s); waiting, not stale",
                item.Id, agent, wait.Since,
                (long)(now - wait.Since).TotalSeconds, (long)threshold.TotalSeconds);

            if (_webhooks is not null)
            {
                try
                {
                    await _webhooks.PublishAsync(new WebhookEvent
                    {
                        Event = "work_item.waiting_on_quota",
                        WorkItem = item,
                        Details = new
                        {
                            workItemId = item.Id.ToString(),
                            agent,
                            since = wait.Since,
                            waitSeconds = (long)(now - wait.Since).TotalSeconds,
                            thresholdSeconds = (long)threshold.TotalSeconds,
                            reason = $"waiting on quota for {agent} since {wait.Since:O}",
                        },
                    }, CancellationToken.None);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Item-stale sweep: failed to publish work_item.waiting_on_quota for {ItemId}", item.Id);
                }
            }

            _quotaWaitNotified[item.Id] = wait.Since;
        }

        foreach (var id in _quotaWaitNotified.Keys)
        {
            if (!liveIds.Contains(id))
                _quotaWaitNotified.TryRemove(id, out _);
        }
    }

    private static bool IsQuotaWaitNotifiableState(WorkItemState state)
        => state == WorkItemState.Queued || WorkItemRecoveryPolicy.IsItemStaleWatchedState(state);

    private async Task<WorkerRegistration?> FindBoundWorkerAsync(WorkItemId itemId, CancellationToken ct)
    {
        IReadOnlyList<WorkerRegistration> workers;
        try
        {
            workers = await _registry.ListAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Item-stale sweep: failed to list workers while probing sandbox liveness for {ItemId}", itemId);
            return null;
        }

        var idStr = itemId.ToString();
        foreach (var worker in workers)
        {
            if (string.IsNullOrEmpty(worker.CurrentWorkItemId)) continue;
            if (string.Equals(worker.CurrentWorkItemId, idStr, StringComparison.OrdinalIgnoreCase))
                return worker;
        }

        return null;
    }

    private async Task<DateTimeOffset?> GetLastStreamActivityAsync(WorkItemId itemId, CancellationToken ct)
    {
        if (_streams is null) return null;
        try
        {
            var files = await _streams.ListAsync(itemId, limit: AgentStreamStore.MaxListLimit, includeLineCount: false, ct);
            if (files.Count == 0) return null;
            // LastActivityAt (last append), not CapturedAt (creation), is the
            // liveness signal: a long turn advances it on every stream append
            // while UpdatedAt legitimately stays frozen mid-turn.
            DateTimeOffset newest = files[0].LastActivityAt;
            for (var i = 1; i < files.Count; i++)
            {
                if (files[i].LastActivityAt > newest)
                    newest = files[i].LastActivityAt;
            }
            return newest;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Item-stale sweep: failed to read stream activity for {ItemId}; treating as no-stream", itemId);
            return null;
        }
    }

    private readonly record struct ClaimOwnerFenceResult(
        bool CanRecover,
        WorkItem? Current,
        string? Error);

    /// <summary>
    /// Fences an active durable-resume dispatch before stale recovery releases
    /// its claim. A successful local cancellation is the ownership proof: a
    /// missing registration could belong to another process/host, so recovery
    /// fails closed instead of treating absence from this process as death.
    /// Once the local registration has drained, the authoritative row is read
    /// again and the caller's state/UpdatedAt CAS remains the final write guard.
    /// </summary>
    private async Task<ClaimOwnerFenceResult> FenceClaimedCheckpointOwnerAsync(
        WorkItem current,
        WorkerProgressWatchdogOptions opts,
        CancellationToken ct)
    {
        var claimId = current.AgentTurnResumeCheckpoint?.DispatchClaimId;
        if (claimId is null)
            return new ClaimOwnerFenceResult(true, current, null);

        if (_cancellations is null)
        {
            const string error =
                "durable checkpoint dispatch is claimed, but no local cancellation registry is available; remote or unfenceable owner left intact";
            _log.LogWarning(
                "Item-stale recovery refused work item {ItemId}: {Reason}",
                current.Id,
                error);
            return new ClaimOwnerFenceResult(false, current, error);
        }

        var quiescenceTimeout = opts.PostAgentTransitionTimeout;
        if (quiescenceTimeout <= TimeSpan.Zero)
        {
            const string error =
                "durable checkpoint dispatch is claimed, but no positive quiescence timeout is configured; owner and claim left intact";
            _log.LogWarning(
                "Item-stale recovery refused work item {ItemId} claim {ClaimId}: {Reason}",
                current.Id,
                claimId,
                error);
            return new ClaimOwnerFenceResult(false, current, error);
        }

        if (!_cancellations.CancelForRecovery(current.Id))
        {
            const string error =
                "durable checkpoint dispatch is claimed without an active local pipeline; remote or unfenceable owner left intact";
            _log.LogWarning(
                "Item-stale recovery refused work item {ItemId} claim {ClaimId}: {Reason}",
                current.Id,
                claimId,
                error);
            return new ClaimOwnerFenceResult(false, current, error);
        }

        using var quiescenceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiescenceCts.CancelAfter(quiescenceTimeout);
        try
        {
            await _cancellations.WaitForInactiveAsync(current.Id, quiescenceCts.Token);
        }
        catch (OperationCanceledException) when (
            !ct.IsCancellationRequested
            && quiescenceCts.IsCancellationRequested)
        {
            var error =
                $"active local pipeline did not quiesce within {quiescenceTimeout}; durable checkpoint claim left intact";
            _log.LogWarning(
                "Item-stale recovery timed out fencing work item {ItemId} claim {ClaimId} after {Timeout}",
                current.Id,
                claimId,
                quiescenceTimeout);
            return new ClaimOwnerFenceResult(false, current, error);
        }

        if (_cancellations.IsActive(current.Id))
        {
            const string error =
                "a local pipeline became active after recovery quiescence; durable checkpoint claim left intact";
            _log.LogWarning(
                "Item-stale recovery refused work item {ItemId} claim {ClaimId}: {Reason}",
                current.Id,
                claimId,
                error);
            return new ClaimOwnerFenceResult(false, current, error);
        }

        var authoritative = await _store.GetAsync(current.Id, ct);
        if (authoritative is null)
        {
            return new ClaimOwnerFenceResult(
                false,
                null,
                "work item disappeared while its claimed dispatch owner was quiescing");
        }

        if (authoritative.State != current.State
            || authoritative.UpdatedAt != current.UpdatedAt
            || authoritative.AgentTurnResumeCheckpoint?.DispatchClaimId != claimId)
        {
            return new ClaimOwnerFenceResult(
                false,
                authoritative,
                "work item or durable checkpoint claim advanced while its local pipeline was quiescing; recovery skipped");
        }

        return new ClaimOwnerFenceResult(true, authoritative, null);
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

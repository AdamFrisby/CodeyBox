using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;


/// <summary>
/// Background service that drives a concurrency-capped, spawn-paced worker
/// pool over the task queue. A single dispatch loop dequeues items; a
/// <see cref="SemaphoreSlim"/> of size <see cref="OrchestratorOptions.MaxConcurrentWorkers"/>
/// caps how many run simultaneously; <see cref="OrchestratorOptions.MinSpawnInterval"/>
/// enforces a minimum wall-clock gap between successive spawns.
/// </summary>
public sealed class OrchestratorService : BackgroundService, IAgentRunningCounters, IAgentSlotGate, IShutdownDispatchGate, IWorkerPoolRecoverySlotReleaser, IWorkerPoolOccupancy, IInfrastructureDeferralScheduler
{
    // Flipped by PauseDispatch() — the SandboxShutdownTeardownService calls it
    // from its IHostedLifecycleService.StoppingAsync BEFORE it begins freezing
    // VMs, so the dispatch loop stops picking up new items and creating new
    // sandboxes that would race the snapshot. In-flight workers continue to
    // completion until the BackgroundService cancellation token fires later in
    // the shutdown sequence. Read on every dispatch loop iteration.
    private int _shutdownDispatchPaused;
    public bool IsDispatchPaused => Volatile.Read(ref _shutdownDispatchPaused) != 0;
    public void PauseDispatch()
    {
        if (Interlocked.Exchange(ref _shutdownDispatchPaused, 1) == 0)
        {
            _log.LogInformation(
                "OrchestratorService: dispatch paused for shutdown — no new work will be picked up");
            // Wake the dispatch loop so it observes the flag immediately rather
            // than blocking on the next natural kick.
            // The loop checks IsDispatchPaused immediately after dequeue, before
            // it can call PickNextEligibleAsync.
            //
            // ContinueWith observes (rather than discards) any fault on the
            // returned task so an asynchronous channel-writer exception during
            // shutdown surfaces as a debug log instead of an unobserved task
            // exception bubbling up to TaskScheduler.UnobservedTaskException
            // (which some deployments promote to a fatal AppDomain.Unhandled —
            // SIGKILL during shutdown is exactly the wedge case this gate
            // exists to prevent).
            try
            {
                var kickTask = _queue.EnqueueDispatchWakeAsync(CancellationToken.None);
                if (!kickTask.IsCompletedSuccessfully)
                {
                    kickTask.AsTask().ContinueWith(
                        t => _log.LogDebug(t.Exception, "PauseDispatch wake-up kick faulted"),
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "PauseDispatch wake-up kick threw synchronously; queue likely already shutting down");
            }
        }
    }


    private readonly ITaskQueue _queue;
    private readonly IWorkItemStore _store;
    private readonly IPipelineRunner _pipeline;
    private readonly CancellationRegistry _cancellations;
    private readonly OrchestratorOptions _opts;
    private readonly ILogger<OrchestratorService> _log;
    private readonly AgentClassRouter? _router;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IAgentDispatchAvailability? _dispatchAvailability;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IWorkerRegistry? _workerRegistry;
    private readonly DeadWorkerOptions? _deadWorkerOpts;
    private readonly DeadWorkerReaper? _reaper;
    private readonly IStartupRecoveryInputBarrier? _startupRecoveryBarrier;
    private readonly IStartupInitialRecoverySink? _startupRecoveryCompletion;
    private readonly ReleaseService? _releaseService;
    // B1 baseline-pinning: stamps the work/headless baseline ref at pickup time
    // so matching work-profile sandboxes keep using that baseline even after an
    // operator edits config. Later phases with different profiles or graphical
    // flavor resolve their own target baselines rather than reusing this pin.
    // For providers that don't model baselines (process / bubblewrap) the DI
    // factory hands in NullBaselineImageResolver which returns null for every
    // resolve — pickup proceeds without a stamp.
    private readonly IBaselineImageResolver _baselineResolver;
    private readonly OrchestratorProgressClock _progressClock;
    // Shared swappable holder. Both this service AND PipelineRunner (the
    // pickup-time rebase-resolver's cap-aware router) read through the same
    // AgentConcurrencySnapshot, so ApplyAgentConcurrencyReload's swap is
    // visible to both consumers — without the shared holder, the resolver
    // would keep gating against the pre-reload caps until process restart.
    // In-flight items are not retroactively gated; caps are only consulted at
    // dispatch time.
    private readonly AgentConcurrencySnapshot _concurrencySnapshot;

    // Live in-flight count keyed by routed agent instance. For class-routed items
    // the count is incremented atomically inside AgentClassRouter via the
    // IAgentSlotGate (this service); for direct-agent items the orchestrator
    // reserves the slot itself after routing. In both cases the outer finally
    // block releases the slot. Surfaced via /concurrency and consumed by the
    // rate-aware gate.
    private readonly ConcurrentDictionary<string, int> _runningPerRoute = new(StringComparer.OrdinalIgnoreCase);

    // Re-pickup delay applied when a direct-agent item hits its per-agent
    // cap. Class-routed items use QuotaRouterOptions.CapRetryRecheckInterval
    // (the router surfaces it via AgentRoutingDecision.SuggestedRecheckIn).
    // Short enough that the deferred item is reconsidered as soon as another
    // worker on the same agent finishes; long enough not to busy-loop.
    // Read from the shared QuotaRouterOptions singleton so the hot-reload
    // coordinator's edits take effect without restart.
    private readonly QuotaRouterOptions? _quotaRouterOptions;
    private readonly BudgetDeferralRecheckSnapshot? _budgetDeferralRecheck;

    // Tracks work item IDs that are currently being processed by a worker.
    // Guards against double-execution when two workers both enqueue the same
    // item (e.g., both see it as the last satisfied dependent simultaneously).
    // Also used by the priority-aware pickup query to skip items that have been
    // dispatched but whose persisted state has not yet flipped out of Queued.
    private readonly ConcurrentDictionary<WorkItemId, byte> _activeItems = new();

    // One idempotent lease per spawned worker-pool slot. The worker task's
    // finally block normally releases the lease; the dead-worker reaper can
    // release the same lease early by registry worker id if it claims a stale
    // row for this process and decides not to re-dispatch the item. The lease's
    // single-shot release prevents a later task exit from double-decrementing
    // _currentlyRunning or over-releasing the semaphore.
    private readonly ConcurrentDictionary<string, WorkerSlotLease> _workerSlotsByRegistryId = new(StringComparer.Ordinal);

    // Tracks work item IDs that are currently sleeping in a deferred-requeue
    // delay (budget / quota / project-pause defer). They remain Queued in the
    // store; the pickup query skips them until the delay fires and removes them.
    private readonly ConcurrentDictionary<WorkItemId, byte> _deferredItems = new();

    // Concurrency gate: at most MaxConcurrentWorkers items running at once.
    private readonly SemaphoreSlim _concurrencyGate;

    // Spawn pacing: UTC ticks of the last worker spawn (0 = never).
    // Written under a lock so the read-modify-write is atomic.
    private long _lastSpawnAtTicks = 0;
    private readonly object _spawnTimeLock = new();

    // Worker index counter — monotonically increasing, used for log identity.
    private int _nextWorkerId = 0;

    // Snapshot for the /workers/status endpoint.
    private int _currentlyRunning = 0;

    // Count of background deferral tasks currently waiting to re-enqueue items.
    private int _pendingDeferrals = 0;
    private const int DeferralWarningThreshold = 100;

    // No-progress re-dispatch backoff (incident 2026-06-04). When a worker is
    // dispatched but the pipeline returns without advancing the item's state
    // (e.g. a poisoned work branch whose pickup phase no-ops), the slot-release
    // dispatch wake would re-pick the still-dispatchable item instantly — a tight
    // ~160/sec spawn loop. MinSpawnInterval stays 0 for normal operation; instead
    // we count consecutive no-progress re-dispatches per item and defer with an
    // escalating backoff (0.5s → 15s cap), turning a loop into delayed/no work
    // rather than a high-load event. After a cap the item is Failed so a genuinely
    // stuck item is cleared instead of looping forever. Reset to 0 on any progress.
    private readonly ConcurrentDictionary<WorkItemId, int> _noProgressRedispatch = new();
    private static readonly TimeSpan NoProgressBackoffBase = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan NoProgressBackoffMax = TimeSpan.FromSeconds(15);
    private const int MaxNoProgressRedispatches = 10;

    /// <summary>
    /// Fallback deferral interval when <c>QuotaRouterOptions</c> is not wired
    /// (DI omits it). 15s keeps the deferred item visible without busy-looping.
    /// Matches the hardcoded default that <see cref="AgentClassRouter"/>
    /// surfaces through <c>QuotaRouterOptions.CapRetryRecheckInterval</c>.
    /// </summary>
    private static readonly TimeSpan DefaultCapRetryRecheckInterval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan SlotReleasedDispatchWakeRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SpawnPacingPausePollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SpawnPacingPauseObservationWindow = TimeSpan.FromMilliseconds(250);
    // WaitIfPausedAsync re-checks the queue controller's in-memory volatile
    // state field on this cadence. Keeping it short (250ms) means a Resume
    // takes effect promptly without an extra signal channel — matches the
    // spawn-pacing pause poll. The read is a constant-time volatile field
    // load on SqliteQueueController so cadence is not a contention concern.
    private static readonly TimeSpan QueuePauseResumePollInterval = TimeSpan.FromMilliseconds(250);

    private enum SpawnPacingWaitResult
    {
        Completed,
        QueuePaused,
        DispatchPaused,
        Cancelled,
    }

    // Per-project semaphores: serialise budget check + StartedAt write to prevent
    // TOCTOU races where multiple concurrent workers all pass the budget check before
    // any of them has committed StartedAt to the database.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _budgetLocks = new();

    public OrchestratorService(
        ITaskQueue queue,
        IWorkItemStore store,
        IPipelineRunner pipeline,
        CancellationRegistry cancellations,
        OrchestratorOptions opts,
        ILogger<OrchestratorService> log,
        AgentClassRouter? router = null,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IWebhookDispatcher? webhooks = null,
        IWorkerRegistry? workerRegistry = null,
        DeadWorkerOptions? deadWorkerOpts = null,
        DeadWorkerReaper? reaper = null,
        ReleaseService? releaseService = null,
        AgentConcurrencyOptions? agentConcurrency = null,
        AgentConcurrencySnapshot? agentConcurrencySnapshot = null,
        IBaselineImageResolver? baselineResolver = null,
        OrchestratorProgressClock? progressClock = null,
        QuotaRouterOptions? quotaRouterOptions = null,
        BudgetDeferralRecheckSnapshot? budgetDeferralRecheck = null,
        IStartupRecoveryInputBarrier? startupRecoveryBarrier = null,
        IStartupInitialRecoverySink? startupRecoveryCompletion = null,
        IAgentDispatchAvailability? dispatchAvailability = null)
    {
        _queue = queue;
        _store = store;
        _pipeline = pipeline;
        _cancellations = cancellations;
        _opts = opts;
        _log = log;
        _router = router;
        _projects = projects;
        _queueController = queueController;
        _dispatchAvailability = dispatchAvailability;
        _webhooks = webhooks;
        _workerRegistry = workerRegistry;
        _deadWorkerOpts = deadWorkerOpts;
        _reaper = reaper;
        _startupRecoveryBarrier = startupRecoveryBarrier;
        _startupRecoveryCompletion = startupRecoveryCompletion;
        _releaseService = releaseService;
        _baselineResolver = baselineResolver ?? NullBaselineImageResolver.Instance;
        _progressClock = progressClock ?? new OrchestratorProgressClock();
        _reaper?.AttachWorkerPoolSlotReleaser(this);
        _quotaRouterOptions = quotaRouterOptions;
        _budgetDeferralRecheck = budgetDeferralRecheck;
        // Prefer the shared snapshot when DI provides one (production path —
        // PipelineRunner reads from the same instance, so hot-reload swaps
        // here are visible there). Test fixtures that pass only the legacy
        // options-shaped parameter get a fresh, unshared snapshot.
        _concurrencySnapshot = agentConcurrencySnapshot
            ?? new AgentConcurrencySnapshot(agentConcurrency ?? new AgentConcurrencyOptions());
        // Reject MaxConcurrent <= 0 loudly at startup. The same call runs again
        // on every hot-reload via ApplyAgentConcurrencyReload — keeping the
        // semantics identical between cold-start and config-edit paths.
        AgentConcurrencyOptions.ValidateAndThrow(_concurrencySnapshot.Current);
        _concurrencyGate = new SemaphoreSlim(opts.MaxConcurrentWorkers, opts.MaxConcurrentWorkers);
        LogResolvedAgentCaps(_concurrencySnapshot.Current, reason: "startup");
    }

    /// <inheritdoc />
    public int GetRunning(AgentKind agent)
    {
        var total = 0;
        foreach (var kv in _runningPerRoute)
        {
            if (string.Equals(AgentInstanceIds.KindFromRouteKey(kv.Key), agent.Value, StringComparison.OrdinalIgnoreCase))
                total += kv.Value;
        }
        return total;
    }

    /// <inheritdoc />
    public int GetRunning(AgentMembership member) =>
        _runningPerRoute.TryGetValue(member.RouteKey, out var n) ? n : 0;

    /// <inheritdoc />
    public IReadOnlyDictionary<AgentKind, int> Snapshot()
    {
        // Materialise so callers can iterate safely while the dispatcher mutates.
        var snap = new Dictionary<AgentKind, int>();
        foreach (var kv in _runningPerRoute)
        {
            if (kv.Value <= 0) continue;
            var kind = new AgentKind(AgentInstanceIds.KindFromRouteKey(kv.Key));
            snap[kind] = snap.TryGetValue(kind, out var existing) ? existing + kv.Value : kv.Value;
        }
        return snap;
    }

    /// <summary>
    /// Returns the per-agent cap configured for <paramref name="agent"/>, or 0
    /// when no cap is configured (treated as "unlimited within global pool").
    /// Values <c>&lt;= 0</c> in the stored entry are rejected at load by
    /// <see cref="AgentConcurrencyOptions.ValidateAndThrow"/>, so the
    /// <c>entry.MaxConcurrent &gt; 0</c> guard here is defence-in-depth — any
    /// non-positive value reaching this read indicates the validator was
    /// bypassed (e.g. test constructor passing a hand-built options instance).
    /// </summary>
    internal int GetAgentCap(AgentKind agent)
    {
        var opts = _concurrencySnapshot.Current;
        return opts.Members.TryGetValue(agent.Value, out var entry) && entry is { MaxConcurrent: > 0 }
            ? entry.MaxConcurrent
            : 0;
    }

    internal int GetAgentCap(AgentMembership member)
    {
        var opts = _concurrencySnapshot.Current;
        if (opts.Members.TryGetValue(member.RouteKey, out var exact) && exact is { MaxConcurrent: > 0 })
            return exact.MaxConcurrent;
        return opts.Members.TryGetValue(member.Agent.Value, out var entry) && entry is { MaxConcurrent: > 0 }
            ? entry.MaxConcurrent
            : 0;
    }

    public bool HasCapacity(AgentKind agent)
    {
        var cap = GetAgentCap(agent);
        return cap <= 0 || GetRunning(agent) < cap;
    }

    public bool HasCapacity(AgentMembership member)
    {
        var cap = GetAgentCap(member);
        return cap <= 0 || GetRunning(member) < cap;
    }

    private int GetRunningForRoute(string routeKey) =>
        _runningPerRoute.TryGetValue(routeKey, out var n) ? n : 0;

    private int GetAgentCapForRoute(AgentKind agent, string routeKey)
    {
        var opts = _concurrencySnapshot.Current;
        if (opts.Members.TryGetValue(routeKey, out var exact) && exact is { MaxConcurrent: > 0 })
            return exact.MaxConcurrent;
        return opts.Members.TryGetValue(agent.Value, out var byKind) && byKind is { MaxConcurrent: > 0 }
            ? byKind.MaxConcurrent
            : 0;
    }

    private static string ResolveDirectRouteKey(AgentKind agent, string? routeKeyOrInstanceId)
    {
        if (string.IsNullOrWhiteSpace(routeKeyOrInstanceId))
            return agent.Value;
        return AgentInstanceIds.RouteKey(agent, routeKeyOrInstanceId);
    }

    /// <summary>
    /// Replaces the per-agent concurrency cap dictionary with <paramref name="next"/>.
    /// Called by the hot-reload coordinator when <c>CodeyBox:AgentConcurrency</c>
    /// changes on disk. The swap is atomic against in-progress reservation
    /// reads; in-flight items already past the gate are unaffected (caps are
    /// only consulted at dispatch time).
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="next"/> contains a <c>MaxConcurrent &lt;= 0</c>
    /// entry. The hot-reload coordinator catches and surfaces this so the prior
    /// config remains in effect rather than the dangerously-permissive default.
    /// </exception>
    public void ApplyAgentConcurrencyReload(AgentConcurrencyOptions next)
    {
        ArgumentNullException.ThrowIfNull(next);
        AgentConcurrencyOptions.ValidateAndThrow(next);
        _concurrencySnapshot.Replace(next);
        LogResolvedAgentCaps(next, reason: "hot-reload");
    }

    /// <summary>
    /// Emits the effective per-agent caps to the log so operators can confirm
    /// (a) what the config-binder actually produced at startup, and (b) what a
    /// hot-reload landed. Agents with no entry are listed as "unlimited" so
    /// the line includes every agent that has work routed to it in this pool.
    /// </summary>
    private void LogResolvedAgentCaps(AgentConcurrencyOptions opts, string reason)
    {
        // Capped agents first (sorted for stable log output), so the line reads
        // top-down by tightest constraint.
        var rendered = opts.Members
            .Where(kv => kv.Value is { MaxConcurrent: > 0 })
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value.MaxConcurrent}")
            .ToList();
        var summary = rendered.Count == 0 ? "<none>" : string.Join(", ", rendered);
        _log.LogInformation(
            "AgentConcurrency caps resolved ({Reason}): {Caps} (agents not listed are uncapped within global pool of {GlobalCap})",
            reason, summary, _opts.MaxConcurrentWorkers);
    }

    /// <inheritdoc />
    public int CurrentlyRunningTotal => Volatile.Read(ref _currentlyRunning);

    /// <summary>
    /// Snapshot of concurrency state for the <c>/concurrency</c> endpoint:
    /// global cap, configured per-agent caps, and live per-agent in-flight counts.
    /// </summary>
    public ConcurrencyStateSnapshot GetConcurrencyState()
    {
        var opts = _concurrencySnapshot.Current;
        var caps = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in opts.Members)
        {
            // Defence-in-depth: validation rejects MaxConcurrent <= 0 entries
            // at load, so the guard only fires when a test constructed an
            // options instance directly without going through the validator.
            if (kv.Value is { MaxConcurrent: > 0 })
                caps[kv.Key] = kv.Value.MaxConcurrent;
        }
        var running = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _runningPerRoute)
            if (kv.Value > 0) running[kv.Key] = kv.Value;

        return new ConcurrencyStateSnapshot(
            GlobalMaxConcurrent: _opts.MaxConcurrentWorkers,
            CurrentlyRunningTotal: Volatile.Read(ref _currentlyRunning),
            PerAgentCaps: caps,
            CurrentlyRunningPerAgent: running);
    }

    /// <summary>
    /// <see cref="IAgentSlotGate.TryReserve"/> implementation.
    /// Atomically tries to reserve a per-agent slot for <paramref name="agent"/>.
    /// Returns true and increments the count when the routed agent has no cap
    /// or running &lt; cap; returns false when the cap is at ceiling.
    ///
    /// <para>
    /// Lock-free; the read-modify-write uses
    /// <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate(TKey, Func{TKey, TValue}, Func{TKey, TValue, TValue})"/>
    /// with a check-before-update factory so multiple dispatchers/workers can
    /// race without exceeding the cap.
    /// </para>
    /// </summary>
    public bool TryReserve(AgentKind agent)
    {
        return TryReserveRoute(agent.Value, GetAgentCap(agent));
    }

    /// <inheritdoc />
    public bool TryReserve(AgentMembership member)
    {
        return TryReserveRoute(member.RouteKey, GetAgentCap(member));
    }

    private bool TryReserveRoute(string routeKey, int cap)
    {
        if (cap <= 0)
        {
            // No per-agent cap configured — still increment so /concurrency reflects reality.
            _runningPerRoute.AddOrUpdate(routeKey, 1, static (_, v) => v + 1);
            return true;
        }

        while (true)
        {
            if (_runningPerRoute.TryGetValue(routeKey, out var current))
            {
                if (current >= cap) return false;
                if (_runningPerRoute.TryUpdate(routeKey, current + 1, current)) return true;
                // Lost a race; retry the read and re-evaluate the cap.
            }
            else
            {
                // First reservation for this route key in this process.
                if (_runningPerRoute.TryAdd(routeKey, 1)) return true;
                // Lost the add race against another reserver; fall through to the
                // TryGetValue branch which will TryUpdate against the observed value.
            }
        }
    }

    /// <summary>
    /// <see cref="IAgentSlotGate.Release"/> implementation. Decrements the
    /// in-flight count for <paramref name="agent"/>.
    /// </summary>
    public void Release(AgentKind agent)
    {
        ReleaseRoute(agent.Value);
    }

    /// <inheritdoc />
    public void Release(AgentMembership member)
    {
        ReleaseRoute(member.RouteKey);
    }

    private void ReleaseRoute(string routeKey)
    {
        // Decrement-or-remove: drop the key when it hits 0 so the next
        // TryReserveAgentSlot takes the TryAdd branch cleanly. Holding the key
        // at 0 would cause TryUpdate(..., 1, 0) to be the only valid path —
        // which works, but leaves stale zero-valued entries accumulating in
        // the dictionary and turns Snapshot/GetConcurrencyState into a fuller scan.
        while (true)
        {
            if (!_runningPerRoute.TryGetValue(routeKey, out var current)) return;
            if (current <= 1)
            {
                if (_runningPerRoute.TryRemove(new KeyValuePair<string, int>(routeKey, current))) return;
            }
            else
            {
                if (_runningPerRoute.TryUpdate(routeKey, current - 1, current)) return;
            }
            // Lost a race; retry.
        }
    }

    /// <summary>Snapshot for the /workers/status endpoint.</summary>
    public async Task<WorkerPoolStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var ticks = Interlocked.Read(ref _lastSpawnAtTicks);
        var queuedCount = await _store.CountByStateAsync(WorkItemState.Queued, ct);
        return new(
            _opts.MaxConcurrentWorkers,
            Volatile.Read(ref _currentlyRunning),
            queuedCount,
            ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    public override void Dispose()
    {
        _concurrencyGate.Dispose();
        foreach (var sem in _budgetLocks.Values) sem.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Releases the concurrency gate, swallowing <see cref="ObjectDisposedException"/>
    /// which can occur when the host's shutdown timeout fires before in-flight worker
    /// tasks finish draining: <see cref="Dispose"/> disposes the gate, then the still-
    /// running task's finally tries to Release on the now-disposed semaphore. Without
    /// this guard the exception faults the inFlight task, propagates through
    /// <c>Task.WhenAll</c> in <see cref="ExecuteAsync"/>, and trips the host's
    /// <c>BackgroundServiceExceptionBehavior=StopHost</c> path — which manifests as a
    /// fatal exit during shutdown and can cause work items to be marked Failed
    /// rather than left mid-flight for recovery.
    /// </summary>
    private void TryReleaseConcurrencyGate()
    {
        try { _concurrencyGate.Release(); }
        catch (ObjectDisposedException) { /* shutdown teardown race; gate already disposed */ }
    }

    public async ValueTask<bool> TryReleaseRecoveredWorkerSlotAsync(
        string workerId,
        WorkItemId? workItemId,
        string reason,
        CancellationToken ct = default)
    {
        if (!_workerSlotsByRegistryId.TryGetValue(workerId, out var lease))
            return false;

        if (workItemId is not null && lease.WorkItemId != workItemId.Value)
        {
            _log.LogWarning(
                "Recovery ({WorkerId}): stale worker row referenced item {RecoveredItemId}, but active pool lease is for {ActiveItemId}; slot release skipped",
                workerId, workItemId.Value, lease.WorkItemId);
            return false;
        }

        if (!ReleaseWorkerSlotLease(lease))
            return false;

        _log.LogWarning(
            "Worker pool: worker {WorkerIndex} slot for work item {WorkItemId} released by recovery ({WorkerId}): {Reason}",
            lease.WorkerIndex, lease.WorkItemId, workerId, reason);

        if (await ShouldWakeAfterRecoveredWorkerSlotReleaseAsync(workItemId, ct))
            await EnqueueSlotReleasedDispatchWakeAsync(lease, ct);
        else
            _log.LogDebug(
                "Worker pool: recovered slot release for worker {WorkerId} item {WorkItemId} did not wake dispatch because durable state is still worker-owned",
                workerId, workItemId?.ToString() ?? "<none>");

        return true;
    }

    private async ValueTask<bool> ShouldWakeAfterRecoveredWorkerSlotReleaseAsync(
        WorkItemId? workItemId,
        CancellationToken ct)
    {
        if (workItemId is null)
            return true;

        var item = await _store.GetAsync(workItemId.Value, ct);
        if (item is null)
            return true;

        return !IsStillWorkerOwnedAfterRecoveryRelease(item);
    }

    private static bool IsStillWorkerOwnedAfterRecoveryRelease(WorkItem item)
    {
        if (item.State is WorkItemState.Working or WorkItemState.Reworking
            && !string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && item.StartedAt is null)
            return false;

        return item.State is WorkItemState.Working
            or WorkItemState.Auditing
            or WorkItemState.Reworking
            or WorkItemState.Merging
            or WorkItemState.UpstreamPushing
            or WorkItemState.ReworkingForConflict;
    }

    private void AttachRegistryWorkerId(WorkerSlotLease lease, string workerId)
    {
        if (!lease.TryAttachRegistryWorkerId(workerId))
            return;

        _workerSlotsByRegistryId[workerId] = lease;
        if (lease.IsReleased)
            _workerSlotsByRegistryId.TryRemove(workerId, out _);
    }

    private bool ReleaseWorkerSlotLease(WorkerSlotLease lease)
    {
        if (!lease.TryMarkReleased())
            return false;

        _activeItems.TryRemove(lease.WorkItemId, out _);
        if (lease.RegistryWorkerId is { } workerId)
            _workerSlotsByRegistryId.TryRemove(workerId, out _);
        Interlocked.Decrement(ref _currentlyRunning);
        AuditLog.WorkerPoolWorkerFinished(lease.WorkerIndex, lease.WorkItemId);
        TryReleaseConcurrencyGate();
        return true;
    }

    private async ValueTask ReleaseCompletedWorkerSlotLeaseAsync(WorkerSlotLease lease, CancellationToken ct)
    {
        if (!ReleaseWorkerSlotLease(lease))
            return;

        await EnqueueSlotReleasedDispatchWakeAsync(lease, ct);
    }

    private async ValueTask EnqueueSlotReleasedDispatchWakeAsync(WorkerSlotLease lease, CancellationToken ct)
    {
        var freeSlots = Math.Max(1, _opts.MaxConcurrentWorkers - Math.Max(0, Volatile.Read(ref _currentlyRunning)));
        for (var wake = 0; wake < freeSlots; wake++)
            await EnqueueRequiredDispatchWakeAsync(lease, ct);
    }

    private async ValueTask EnqueueRequiredDispatchWakeAsync(WorkerSlotLease lease, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                await _queue.EnqueueDispatchWakeAsync(ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                attempt++;
                _log.LogError(
                    ex,
                    "Worker pool: required slot-release wake-up kick failed for work item {WorkItemId} on attempt {Attempt}; retrying",
                    lease.WorkItemId,
                    attempt);
                await Task.Delay(SlotReleasedDispatchWakeRetryDelay, ct);
            }
        }
    }

    private bool IsQueuePaused =>
        _queueController is not null && _queueController.State == QueueState.Paused;

    private ValueTask RequeueDispatchWakeAsync(CancellationToken ct)
        => _queue.EnqueueDispatchWakeAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // R8-core suspend/resume: SandboxResumeOnStartupService now runs in the
        // background by default so Kestrel can bind first. Keep startup recovery
        // ordered by waiting here before the dead-worker startup sweep touches
        // any items that still carry SuspendedVmName.
        if (_startupRecoveryBarrier is not null)
            await _startupRecoveryBarrier.RecoveryInputReady.WaitAsync(stoppingToken);

        // Run the reaper once at startup before replaying pending items.
        // This transitions any items that were mid-flight when the previous
        // process crashed back to a recoverable state, so ReplayPendingAsync
        // finds them in their correct target states (Queued, WorkComplete, …)
        // rather than the stale worker-owned states.
        //
        // Order matters: RunOnceAsync claims and DELETEs every stale worker
        // row, so the subsequent SweepStrandedItemsAsync can treat any
        // remaining registry rows as authoritatively "live" and safely leave
        // their owned items alone. Without this ordering an HA peer mid-flight
        // could be misclassified as stranded.
        if (_reaper is not null)
        {
            await _reaper.RunOnceAsync(stoppingToken);
            await _reaper.SweepStrandedItemsAsync(stoppingToken);
            _progressClock.Stamp(DateTimeOffset.UtcNow);
        }

        _startupRecoveryCompletion?.MarkInitialRecoveryCompleted();

        await ReplayPendingAsync(stoppingToken);

        // Collect in-flight item tasks so we can await them all on shutdown.
        // List is safe here: only the dispatch loop (single logical thread) touches it.
        var inFlight = new List<Task>();
        var stopDispatchLoop = false;

        while (!stoppingToken.IsCancellationRequested && !stopDispatchLoop)
        {
            // Shutdown dispatch gate (R8.1 fix for VM-wedging incident
            // 2026-05-29): the shutdown teardown handler calls PauseDispatch()
            // BEFORE it snapshots SnapshotActiveSandboxes(), so once the flag
            // is set the dispatch loop MUST stop picking up new work and
            // creating new sandboxes that would race the snapshot — those
            // would otherwise be left mid-launch when the BackgroundService
            // cancellation token fires later in the shutdown sequence. The
            // in-flight worker tasks already in flight continue normally.
            if (IsDispatchPaused) break;

            // Pause gate: spin-wait while the queue is paused, without consuming
            // from the channel. In-flight workers continue normally during pause.
            if (!await WaitIfPausedAsync(stoppingToken)) break;

            // Wait for a kick. The queue payload is no longer the source of
            // truth — we use it as a "something changed, re-check the DB"
            // signal so that priority and equal-priority FIFO ordering come
            // from a single ORDER BY query rather than channel insertion order.
            bool gotDispatchSignal;
            try { gotDispatchSignal = await _queue.DequeueDispatchSignalAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (!gotDispatchSignal) break;

            // Re-check after dequeue: PauseDispatch may have fired while
            // the dispatch signal wait was blocked, and the wake-up kick must not
            // be allowed to flow through to PickNextEligibleAsync — that would
            // happily pick up a real queued item from the store and spawn a new
            // sandbox that races the snapshot.
            if (IsDispatchPaused) break;

            // Post-dequeue pause check: handles the race where the queue was paused
            // while we were blocked in DequeueAsync. Just loop; we'll re-check
            // WaitIfPausedAsync on the next iteration.
            if (IsQueuePaused)
            {
                await RequeueDispatchWakeAsync(stoppingToken);
                continue;
            }

            var blockForFirstSlot = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                // Acquire the gate BEFORE resolving which item to pick up so the
                // pickup decision uses the freshest store state. The first pass
                // may block because a buffered kick can arrive while the pool is
                // full; refill passes only use slots already free for this turn.
                if (blockForFirstSlot)
                {
                    try { await _concurrencyGate.WaitAsync(stoppingToken); }
                    catch (OperationCanceledException)
                    {
                        stopDispatchLoop = true;
                        break;
                    }
                }
                else if (!_concurrencyGate.Wait(0))
                {
                    break;
                }

                // Late dispatch-pause check: PauseDispatch may have fired while we
                // were blocked on the concurrency gate. Without this check, one
                // final worker could be spawned after dispatch was paused (the
                // very race this gate exists to close — that final sandbox would
                // miss the SnapshotActiveSandboxes snapshot and be torn down
                // uncleanly when the BackgroundService cancellation token fires).
                if (IsDispatchPaused)
                {
                    TryReleaseConcurrencyGate();
                    stopDispatchLoop = true;
                    break;
                }

                // Queue pause can also happen while a consumed kick is waiting on
                // a full pool. Preserve a wake for resume, but do not pick work
                // while the operator-visible queue state is Paused.
                if (IsQueuePaused)
                {
                    TryReleaseConcurrencyGate();
                    await RequeueDispatchWakeAsync(CancellationToken.None);
                    break;
                }

                // Resolve the next eligible item by priority: highest Priority first,
                // ties broken by CreatedAt ascending. Skips items currently in-flight
                // (_activeItems) and items currently sleeping in a defer-requeue delay
                // (_deferredItems). When nothing eligible is found, this kick was
                // spurious — release the slot and loop back for the next kick.
                WorkItemId? id = await PickNextEligibleAsync(stoppingToken);
                if (id is null)
                {
                    TryReleaseConcurrencyGate();
                    break;
                }

                // Reserve the slot now so the next dispatch iteration's pickup query
                // skips this ID. The Task.Run cleanup below removes the reservation
                // when the worker exits.
                if (!_activeItems.TryAdd(id.Value, 0))
                {
                    TryReleaseConcurrencyGate();
                    blockForFirstSlot = false;
                    continue;
                }

                if (_opts.OnWorkerReservedForTest is { } onWorkerReservedForTest)
                {
                    try
                    {
                        await onWorkerReservedForTest(id.Value);
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "OnWorkerReservedForTest callback threw; releasing concurrency slot and skipping item {Id}", id);
                        _activeItems.TryRemove(id.Value, out _);
                        TryReleaseConcurrencyGate();
                        blockForFirstSlot = false;
                        continue;
                    }
                }

                // Spawn pacing: enforce MinSpawnInterval between successive spawns.
                if (_opts.MinSpawnInterval > TimeSpan.Zero)
                {
                    long lastTicks;
                    lock (_spawnTimeLock) { lastTicks = _lastSpawnAtTicks; }
                    if (lastTicks != 0)
                    {
                        var lastSpawnAt = new DateTimeOffset(lastTicks, TimeSpan.Zero);
                        var nextEligible = lastSpawnAt + _opts.MinSpawnInterval;
                        var wait = nextEligible - DateTimeOffset.UtcNow;
                        if (wait <= TimeSpan.Zero && _queueController is not null)
                        {
                            wait = _opts.MinSpawnInterval < SpawnPacingPauseObservationWindow
                                ? _opts.MinSpawnInterval
                                : SpawnPacingPauseObservationWindow;
                        }
                        if (wait > TimeSpan.Zero)
                        {
                            if (nextEligible > DateTimeOffset.UtcNow)
                                AuditLog.WorkerPoolSpawnThrottled((long)wait.TotalMilliseconds);
                            var pacing = await WaitForSpawnPacingAsync(wait, stoppingToken);
                            if (pacing == SpawnPacingWaitResult.Cancelled)
                            {
                                _activeItems.TryRemove(id.Value, out _);
                                TryReleaseConcurrencyGate();
                                stopDispatchLoop = true;
                                break;
                            }
                            if (pacing == SpawnPacingWaitResult.DispatchPaused)
                            {
                                _activeItems.TryRemove(id.Value, out _);
                                TryReleaseConcurrencyGate();
                                stopDispatchLoop = true;
                                break;
                            }
                            if (pacing == SpawnPacingWaitResult.QueuePaused)
                            {
                                _activeItems.TryRemove(id.Value, out _);
                                TryReleaseConcurrencyGate();
                                await _queue.EnqueueDispatchWakeAsync(stoppingToken);
                                break;
                            }
                        }
                    }
                }

                if (IsDispatchPaused)
                {
                    _activeItems.TryRemove(id.Value, out _);
                    TryReleaseConcurrencyGate();
                    stopDispatchLoop = true;
                    break;
                }

                if (IsQueuePaused)
                {
                    _activeItems.TryRemove(id.Value, out _);
                    TryReleaseConcurrencyGate();
                    await RequeueDispatchWakeAsync(CancellationToken.None);
                    break;
                }

                // Record spawn timestamp before launching the task.
                lock (_spawnTimeLock) { _lastSpawnAtTicks = DateTimeOffset.UtcNow.Ticks; }
                try { _opts.OnWorkerSpawned?.Invoke(); }
                catch (Exception ex)
                {
                    _log.LogError(ex, "OnWorkerSpawned callback threw; releasing concurrency slot and skipping item {Id}", id);
                    _activeItems.TryRemove(id.Value, out _);
                    TryReleaseConcurrencyGate();
                    blockForFirstSlot = false;
                    continue;
                }
                var workerIndex = Interlocked.Increment(ref _nextWorkerId);

                var capturedId = id.Value;
                CodeyBoxMeters.Dispatches.Add(1);
                // Increment before Task.Run so the counter is never transiently negative
                // if the task's finally block executes before we reach the increment.
                Interlocked.Increment(ref _currentlyRunning);
                var slotLease = new WorkerSlotLease(workerIndex, capturedId);
                var task = Task.Run(async () =>
                {
                    AuditLog.WorkerPoolWorkerStarted(workerIndex, capturedId);
                    try
                    {
                        if (IsQueuePaused)
                        {
                            _log.LogInformation(
                                "Worker {WorkerId} skipping {Id}: queue paused after spawn reservation but before pipeline start",
                                workerIndex,
                                capturedId);
                            return;
                        }
                        await RunItemAsync(workerIndex, capturedId, slotLease, stoppingToken);
                    }
                    finally
                    {
                        await ReleaseCompletedWorkerSlotLeaseAsync(slotLease, stoppingToken);
                    }
                });

                inFlight.Add(task);
                // Prune completed tasks on every iteration to prevent unbounded growth.
                inFlight.RemoveAll(t => t.IsCompleted);

                break;
            }
        }

        // Drain in-flight tasks before the hosted service exits, but do not
        // let a host-shutdown path wait forever on a worker that ignores the
        // shutdown token. Recovery writes are only safe once the worker task has
        // stopped; startup recovery handles rows left live past this bounded
        // drain after the previous process is gone.
        await DrainInFlightWorkersAsync(inFlight).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        PauseDispatch();
        await base.StopAsync(cancellationToken);
    }

    private async Task DrainInFlightWorkersAsync(List<Task> inFlight)
    {
        inFlight.RemoveAll(t => t.IsCompleted);
        if (inFlight.Count == 0)
            return;

        var all = Task.WhenAll(inFlight);
        var drain = _opts.ShutdownDrainTimeout;
        if (drain <= TimeSpan.Zero)
        {
            await all.ConfigureAwait(false);
            return;
        }

        var completed = await Task.WhenAny(all, Task.Delay(drain)).ConfigureAwait(false);
        if (completed == all)
        {
            await all.ConfigureAwait(false);
            return;
        }

        _log.LogCritical(
            "OrchestratorService shutdown drain timed out after {Timeout} with {Count} worker(s) still active; not re-queueing active items until the workers stop or startup recovery proves they are gone",
            drain,
            inFlight.Count(t => !t.IsCompleted));
    }

    private async Task RecoverHostShutdownAbortedItemAsync(WorkItemId id)
    {
        try
        {
            await TryRecoverActiveItemForGracefulShutdownAsync(
                id,
                "host shutdown cancellation",
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(
                ex,
                "Host-shutdown recovery failed for active work item {Id}; startup recovery will reconcile persisted state",
                id);
        }
    }

    private async Task TryRecoverActiveItemForGracefulShutdownAsync(
        WorkItemId id,
        string recoveryReason,
        CancellationToken ct)
    {
        var item = await _store.GetAsync(id, ct).ConfigureAwait(false);
        if (item is null)
            return;

        var recovered = BuildGracefulShutdownRecoveryState(item, recoveryReason);
        if (recovered is null)
            return;

        var updated = await _store.TryUpdateIfStateAsync(recovered, item.State, ct)
            .ConfigureAwait(false);
        if (!updated)
        {
            _log.LogInformation(
                "Shutdown recovery skipped {Id}: state changed from {State} before recovery write",
                id, item.State);
            return;
        }

        await _queue.EnqueueAsync(id, ct).ConfigureAwait(false);
        _log.LogWarning(
            "Shutdown recovery re-queued {Id}: {FromState} -> {ToState} ({Reason})",
            id, item.State, recovered.State, recoveryReason);
    }

    private static WorkItem? BuildGracefulShutdownRecoveryState(
        WorkItem item,
        string recoveryReason = "graceful shutdown drain timed out")
        => WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            item,
            DateTimeOffset.UtcNow,
            recoveryReason);

    /// <summary>
    /// Walks dispatch-eligible non-terminal items by priority order and returns
    /// the first one that is not already in <c>_activeItems</c> or
    /// <c>_deferredItems</c> and whose <see cref="WorkItem.DependsOn"/> gate
    /// is satisfied per <see cref="WorkItemDependencies.SatisfyingStates"/>
    /// (currently: every dep is in <see cref="WorkItemState.Done"/>). Returns
    /// null when no eligible item exists or every candidate is blocked.
    ///
    /// <para>
    /// Dependency satisfaction is checked in C# against a freshly-built state
    /// map (one ListAsync snapshot per pickup) — never a cached
    /// <see cref="WorkItem.DependsOnSatisfied"/>-style boolean, so a dep
    /// transition that landed after the channel kick is honored on the very
    /// next tick.
    /// </para>
    ///
    /// <para>
    /// Items found blocked here are logged so an unsatisfied-deps backlog is
    /// visible in operator dashboards — important when a dependency chain
    /// is stuck on an upstream item that needs operator action.
    /// </para>
    /// </summary>
    private async Task<WorkItemId?> PickNextEligibleAsync(CancellationToken stoppingToken)
    {
        var skipIds = new HashSet<WorkItemId>(_activeItems.Keys);
        foreach (var deferredId in _deferredItems.Keys) skipIds.Add(deferredId);

        // Build the state map lazily only when we encounter an item with deps.
        Dictionary<WorkItemId, WorkItemState>? statesById = null;

        await foreach (var candidate in _store.ListDispatchEligibleByPriorityAsync(skipIds, stoppingToken))
        {
            if (candidate.DependsOn.Count == 0)
                return candidate.Id;

            if (statesById is null)
            {
                var snapshot = new List<WorkItem>();
                await foreach (var i in _store.ListAsync(stoppingToken)) snapshot.Add(i);
                statesById = WorkItemDependencies.BuildStateMap(snapshot);
            }

            if (WorkItemDependencies.AreSatisfied(candidate.DependsOn, statesById))
                return candidate.Id;

            // Gate blocked this candidate. Log so a sustained unsatisfied-deps
            // backlog (e.g. a parent stuck in Failed awaiting operator retry)
            // is observable instead of silently absorbed by the dispatcher.
            _log.LogDebug(
                "Dispatch skip {Id}: dependsOn gate not satisfied (state={State}, deps={Deps})",
                candidate.Id, candidate.State, candidate.DependsOn.Count);
        }

        return null;
    }

    /// <summary>
    /// Waits until the queue is no longer paused, then returns true.
    /// Returns false if the stopping token fires while waiting.
    /// </summary>
    private async Task<bool> WaitIfPausedAsync(CancellationToken stoppingToken)
    {
        if (_queueController is null) return true;
        while (_queueController.State == QueueState.Paused)
        {
            try { await Task.Delay(QueuePauseResumePollInterval, stoppingToken); }
            catch (OperationCanceledException) { return false; }
        }
        return true;
    }

    private async Task<SpawnPacingWaitResult> WaitForSpawnPacingAsync(
        TimeSpan wait,
        CancellationToken stoppingToken)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            if (IsDispatchPaused)
                return SpawnPacingWaitResult.DispatchPaused;
            if (IsQueuePaused)
                return SpawnPacingWaitResult.QueuePaused;

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return SpawnPacingWaitResult.Completed;

            var delay = remaining <= SpawnPacingPausePollInterval
                ? remaining
                : SpawnPacingPausePollInterval;
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return SpawnPacingWaitResult.Cancelled; }
        }
    }

    // Exposed as internal so tests can invoke recovery in isolation without
    // starting the full worker loop.
    internal Task ReplayPendingForTestAsync(CancellationToken ct) => ReplayPendingAsync(ct);
    internal WorkItem? TryBuildRecoveredStateForTest(WorkItem item) => TryBuildRecoveredState(item);
    internal WorkItem? BuildGracefulShutdownRecoveryStateForTest(WorkItem item) =>
        BuildGracefulShutdownRecoveryState(item);

    // Exposed as internal so tests can verify the deferred-pickup contract
    // without spinning the BackgroundService: PickNextEligibleAsync must skip
    // items in _deferredItems, and an item-specific kick on the queue must
    // clear the deferral.
    internal Task<WorkItemId?> PickNextEligibleForTestAsync(CancellationToken ct)
        => PickNextEligibleAsync(ct);
    internal void MarkDeferredForTest(WorkItemId id) => _deferredItems[id] = 0;
    internal bool IsDeferredForTest(WorkItemId id) => _deferredItems.ContainsKey(id);
    internal bool IsActiveForTest(WorkItemId id) => _activeItems.ContainsKey(id);
    internal void SetLastSpawnAtForTest(DateTimeOffset at)
    {
        lock (_spawnTimeLock) { _lastSpawnAtTicks = at.Ticks; }
    }

    internal IReadOnlyCollection<WorkItemId> ActiveWorkItemIdsForHealthCheck() =>
        _activeItems.Keys.ToList();

    internal void ClearDeferredForHealthRecovery(WorkItemId id) =>
        _deferredItems.TryRemove(id, out _);

    // Exposed as internal so tests can directly exercise the per-agent cap
    // reservation/release cycle without spinning the full BackgroundService.
    // The hot-spin bug in the first revision of this code (TryAdd against an
    // existing key) is only visible across a Release-then-Reserve cycle, which
    // PinnedPipelineRunner-based integration tests do not produce.
    internal bool TryReserveAgentSlotForTest(AgentKind agent) => TryReserve(agent);
    internal void ReleaseAgentSlotForTest(AgentKind agent) => Release(agent);

    /// <summary>
    /// On startup, re-enqueue work items that were mid-flight when we last
    /// stopped. Items in non-Queued non-terminal states are reset to a recoverable
    /// state and re-enqueued. Queued items are only re-enqueued if all their
    /// dependencies are currently terminal; those that are still waiting will be
    /// enqueued by <see cref="EnqueueSatisfiedDependentsAsync"/> when their deps
    /// complete.
    ///
    /// Recovery state mapping:
    ///   Working         → Failed      (crashed work phase without a preempt checkpoint)
    ///   Auditing        → WorkComplete (work commit is real; re-run the audit suite)
    ///   Reworking       → WorkComplete (re-run audit to confirm or re-rework)
    ///   Merging         → AuditPassed  (audit verdict is real; retry the merge)
    ///   UpstreamPushing → Merged     (keeping UpstreamPushing leaves skipWork/skipAudit/skipMerge
    ///                                  all false, triggering a full pipeline replay from scratch)
    ///   WorkComplete / AuditPassed / Merged → (re-enqueued as-is; pipeline resumes at correct phase)
    ///
    /// State-changing interrupted recovery increments
    /// <see cref="WorkItem.RecoveryAttempts"/>. Items that exceed
    /// <see cref="OrchestratorOptions.MaxRecoveryAttempts"/> are transitioned to
    /// <see cref="WorkItemState.AbandonedAfterRecoveryAttempts"/> instead.
    /// Durable phase-boundary pass-throughs also consume a recovery attempt:
    /// being redispatched from the same boundary is still an automatic recovery
    /// handoff, and the pipeline clears the counter when a later phase actually
    /// completes.
    /// </summary>
    private async Task ReplayPendingAsync(CancellationToken ct)
    {
        // Collect all items once to build the state map for dep checking.
        var allItems = new List<WorkItem>();
        await foreach (var item in _store.ListAsync(ct))
            allItems.Add(item);
        var statesById = WorkItemDependencies.BuildStateMap(allItems);

        // Warn about legacy Cancelled items that may have been buried by a host shutdown
        // before this fix was deployed (cancellation_reason IS NULL AND last_error = 'cancelled').
        var legacyBuried = allItems
            .Where(i => i.State == WorkItemState.Cancelled
                && i.CancellationReason is null
                && i.LastError == "cancelled")
            .ToList();
        if (legacyBuried.Count > 0)
        {
            _log.LogWarning(
                "Found {Count} work item(s) in Cancelled state with ambiguous reason " +
                "(may have been interrupted by a prior host shutdown before the no-shutdown-cancel fix): {Ids}. " +
                "Use POST /workitems/{{id}}/uncancel to restore any that should be re-queued.",
                legacyBuried.Count,
                string.Join(", ", legacyBuried.Select(i => i.Id.ToString())));
        }

        foreach (var item in allItems)
        {
            if (_deferredItems.ContainsKey(item.Id))
                continue;

            if (_reaper is not null && _reaper.HasRecoveredItemInCurrentProcess(item.Id))
                continue;

            var recovered = await TryBuildRecoveredStateAsync(item, ct);
            if (recovered is not null)
            {
                if (recovered.State == WorkItemState.AbandonedAfterRecoveryAttempts)
                {
                    await _store.UpdateAsync(recovered, ct);
                    AuditLog.WorkItemAbandonedAfterRecovery(item.Id, _opts.MaxRecoveryAttempts);
                    _log.LogWarning(
                        "Work item {Id} has been abandoned after {Max} recovery attempts; operator intervention required",
                        item.Id, _opts.MaxRecoveryAttempts);
                }
                else if (recovered.State == WorkItemState.Failed)
                {
                    await _store.UpdateAsync(recovered, ct);
                    _log.LogWarning(
                        "Work item {Id} was left Working without a preempt checkpoint; marked Failed as a crash case",
                        item.Id);
                }
                else if (recovered.State == WorkItemState.Done)
                {
                    await _store.UpdateAsync(recovered, ct);
                    await CheckAndActFollowupRecovery.EnqueueExistingFollowupIfActionableAsync(
                        _store, _queue, item, ct);
                    _log.LogInformation(
                        "Work item {Id} check-and-act verdict was already persisted; completed startup recovery without replaying the check",
                        item.Id);
                }
                else
                {
                    await _store.UpdateAsync(recovered, ct);
                    AuditLog.WorkItemRecovered(item.Id, item.State.ToString(), recovered.State.ToString(), recovered.RecoveryAttempts);
                    await _queue.EnqueueAsync(recovered.Id, ct);
                }
            }
            else if (item.State == WorkItemState.Queued)
            {
                if (WorkItemDependencies.AreSatisfied(item.DependsOn, statesById))
                {
                    _log.LogInformation("Recovering queued work item {Id} (dependencies satisfied)", item.Id);
                    await _queue.EnqueueAsync(item.Id, ct);
                }
                else
                {
                    _log.LogInformation(
                        "Skipping queued work item {Id} at startup: waiting for dependencies", item.Id);
                }
            }
        }
    }

    private async Task<WorkItem?> TryBuildRecoveredStateAsync(WorkItem item, CancellationToken ct)
    {
        if (WorkItemRecoveryPolicy.IsRerunnableCheckAndActWithoutPreempt(item))
        {
            var completed = await CheckAndActFollowupRecovery.TryBuildCompletedFromPersistedVerdictAsync(
                _store, item, ct);
            if (completed is not null)
                return completed;
        }

        return TryBuildRecoveredState(item);
    }

    private async Task HeartbeatLoopAsync(string workerId, string currentWorkItemId, TimeSpan interval, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await _workerRegistry!.HeartbeatAsync(workerId, currentWorkItemId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Fail-soft: transient storage failures must not kill the worker.
                _log.LogWarning(ex, "Heartbeat failed for worker {WorkerId}; will retry on next interval", workerId);
            }
        }
    }

    /// <summary>
    /// Builds the recovered state for a mid-flight work item, or returns null
    /// if the item does not need recovery (terminal or Queued).
    ///
    /// <para>
    /// When a <see cref="DeadWorkerReaper"/> is wired, states handled by
    /// <see cref="DeadWorkerReaper.HandlesRecoveryState"/> are handled earlier by
    /// <see cref="DeadWorkerReaper.RunOnceAsync"/> or
    /// <see cref="DeadWorkerReaper.SweepStrandedItemsAsync"/>
    /// — that path consults the worker registry to skip items owned by a live
    /// worker, which this unconditional replay cannot do. Returning null here
    /// for those states prevents double-recovery (duplicate <c>RecoveryAttempts</c>
    /// increments, duplicate webhook fires, double queue kicks). Test fixtures
    /// without a reaper fall through to the legacy inline handling so the
    /// recovery contract for those states remains exercised end-to-end.
    /// </para>
    /// </summary>
    private WorkItem? TryBuildRecoveredState(WorkItem item)
    {
        // Reaper-owned states are handled by the registry-aware startup sweep
        // when a reaper is wired. Skip them here to avoid clobbering items held
        // by a live peer and to avoid duplicate recovery / queue kicks.
        if (_reaper is not null && DeadWorkerReaper.HandlesRecoveryState(item.State))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && item.State is WorkItemState.Working or WorkItemState.Reworking)
        {
            return item with
            {
                StartedAt = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        if (WorkItemRecoveryPolicy.IsRerunnableCheckAndActWithoutPreempt(item))
        {
            var checkAttempts = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);
            if (WorkItemRecoveryPolicy.ExceedsRecoveryAttempts(checkAttempts, _opts.MaxRecoveryAttempts))
            {
                return item with
                {
                    State = WorkItemState.AbandonedAfterRecoveryAttempts,
                    LastError = $"abandoned after {_opts.MaxRecoveryAttempts} recovery attempts; was {item.State}",
                    RecoveryAttempts = checkAttempts,
                    StartedAt = null,
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            return WorkItemRecoveryPolicy.BuildCheckAndActRerun(item, checkAttempts);
        }

        if (WorkItemRecoveryPolicy.IsRerunnableAgentControlWithoutPreempt(item))
        {
            var controlAttempts = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);
            if (WorkItemRecoveryPolicy.ExceedsRecoveryAttempts(controlAttempts, _opts.MaxRecoveryAttempts))
            {
                return item with
                {
                    State = WorkItemState.AbandonedAfterRecoveryAttempts,
                    LastError = $"abandoned after {_opts.MaxRecoveryAttempts} recovery attempts; was {item.State}",
                    RecoveryAttempts = controlAttempts,
                    StartedAt = null,
                    PreemptedAt = null,
                    PreemptCheckpoint = null,
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
            }

            return WorkItemRecoveryPolicy.BuildAgentControlRerun(item, controlAttempts);
        }

        if (item.State == WorkItemState.Working)
        {
            return item with
            {
                State = WorkItemState.Failed,
                LastError = "worker died while work phase was running without a preempt checkpoint",
                RecoveryAttempts = WorkItemRecoveryPolicy.NextRecoveryAttempt(item),
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        // WaitingForQuotaReset is owned by QuotaRetryScheduler; the periodic
        // sweep re-enqueues when any member becomes available. Treat it as a
        // resting point on startup so a routine restart doesn't burn a recovery
        // credit or jump the queue.
        if (item.State is WorkItemState.WaitingForQuotaReset or WorkItemState.WaitingForAgentResume)
            return null;

        WorkItemState? targetState = WorkItemRecoveryPolicy.MapToRecoveryState(item.State);

        if (targetState is null) return null;

        // Every automatic recovery handoff counts against the same budget,
        // including same-state redispatches from durable phase-boundary states.
        // Otherwise a WorkComplete -> Auditing -> WorkComplete livelock can reset
        // itself forever without reaching the cap. The production pipeline clears
        // RecoveryAttempts only when a phase actually completes.
        var newAttempts = WorkItemRecoveryPolicy.NextRecoveryAttempt(item);

        // MaxRecoveryAttempts <= 0 means unlimited (no cap). Only enforce when > 0.
        if (WorkItemRecoveryPolicy.ExceedsRecoveryAttempts(newAttempts, _opts.MaxRecoveryAttempts))
        {
            return item with
            {
                State = WorkItemState.AbandonedAfterRecoveryAttempts,
                LastError = $"abandoned after {_opts.MaxRecoveryAttempts} recovery attempts; was {item.State}",
                RecoveryAttempts = newAttempts,
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        return item.With(targetState.Value) with { RecoveryAttempts = newAttempts };
    }

    private async Task RunItemAsync(int workerIndex, WorkItemId id, WorkerSlotLease slotLease, CancellationToken ct)
    {
        var item = await _store.GetAsync(id, ct);
        if (item is null)
        {
            _log.LogWarning("Worker {WorkerId} dequeued unknown work item {Id}", workerIndex, id);
            _activeItems.TryRemove(id, out _);
            return;
        }
        if (item.State is WorkItemState.Cancelled or WorkItemState.Done
            or WorkItemState.Failed or WorkItemState.AuditFailed
            or WorkItemState.MergeConflictResolutionFailed
            or WorkItemState.AbandonedAfterRecoveryAttempts)
        {
            _log.LogInformation("Worker {WorkerId} skipping {Id} in terminal state {State}", workerIndex, id, item.State);
            _activeItems.TryRemove(id, out _);
            return;
        }

        // Items parked waiting for operator input must not be processed by a worker.
        // They are re-enqueued by the answer/dismiss-question endpoints when all questions resolve.
        if (item.State is WorkItemState.NeedsOperatorInput)
        {
            _log.LogWarning("Worker {WorkerId} skipping {Id}: still in NeedsOperatorInput state", workerIndex, id);
            _activeItems.TryRemove(id, out _);
            return;
        }

        // Items parked waiting for quota to reset must not be processed.
        // The quota retry scheduler will re-enqueue when any class member becomes
        // available again; running here would just repeat the exhaustion that
        // got us into this state.
        if (item.State is WorkItemState.WaitingForQuotaReset)
        {
            _log.LogInformation("Worker {WorkerId} skipping {Id}: still WaitingForQuotaReset", workerIndex, id);
            return;
        }
        if (item.State is WorkItemState.WaitingForAgentResume)
        {
            _log.LogInformation("Worker {WorkerId} skipping {Id}: still WaitingForAgentResume", workerIndex, id);
            return;
        }

        // _activeItems was reserved by the dispatch loop before this task was
        // spawned, so the priority-aware pickup query already skips this ID and
        // we cannot double-dispatch. The TryRemove in the finally block below
        // releases the reservation when the worker exits.

        // Register this execution in the worker registry so the dead-worker
        // reaper can detect and recover it if this process crashes mid-flight.
        string? registeredWorkerId = null;
        CancellationTokenSource? heartbeatCts = null;
        if (_workerRegistry is not null && _deadWorkerOpts is not null)
        {
            registeredWorkerId = Guid.NewGuid().ToString();
            var reg = new WorkerRegistration
            {
                WorkerId = registeredWorkerId,
                HostName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                StartedAt = DateTimeOffset.UtcNow,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                CurrentWorkItemId = id.ToString(),
            };
            try
            {
                await _workerRegistry.RegisterAsync(reg, ct);
                AttachRegistryWorkerId(slotLease, registeredWorkerId);
                AuditLog.WorkerRegistered(registeredWorkerId, reg.HostName, reg.ProcessId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to register worker {WorkerId} for item {Id}; continuing without heartbeat", registeredWorkerId, id);
                registeredWorkerId = null;
            }

            if (registeredWorkerId is not null)
            {
                heartbeatCts = new CancellationTokenSource();
                _ = HeartbeatLoopAsync(registeredWorkerId, id.ToString(), _deadWorkerOpts.HeartbeatInterval, heartbeatCts.Token);
            }
        }

        // Per-agent slot tracking: set when the router pins the item to an agent
        // and the reservation succeeds. Cleared in the outer finally so a deferral
        // or crash cannot leak the slot.
        string? agentRouteForRelease = null;
        bool agentSlotReserved = false;

        try
        {
            var current = await _store.GetAsync(id, ct);
            if (current is null)
            {
                _log.LogWarning("Worker {WorkerId} dequeued unknown work item {Id} after claiming active slot", workerIndex, id);
                return;
            }

            if (current.State is WorkItemState.Cancelled or WorkItemState.Done
                or WorkItemState.Failed or WorkItemState.AuditFailed
                or WorkItemState.MergeConflictResolutionFailed
                or WorkItemState.AbandonedAfterRecoveryAttempts)
            {
                _log.LogInformation("Worker {WorkerId} skipping {Id} after active claim: terminal state {State}", workerIndex, id, current.State);
                return;
            }

            item = current;

            if (item.State is WorkItemState.WaitingForQuotaReset or WorkItemState.WaitingForAgentResume)
            {
                _log.LogInformation(
                    "Worker {WorkerId} skipping {Id} after active claim: parked state {State}",
                    workerIndex, id, item.State);
                return;
            }

            var isAgentControlItem = item.JobType == JobType.AgentControl;

            // Dependency gate: skip items whose deps aren't all satisfied yet
            // (see WorkItemDependencies.SatisfyingStates — currently only Done
            // satisfies; Failed / AuditFailed / Cancelled all block). Recomputed
            // from a fresh ListAsync snapshot on every pickup so the gate is
            // never served from a stale cache. This is the worker-side mirror
            // of the dispatch-side check in PickNextEligibleAsync; running both
            // closes the narrow TOCTOU window where a dep regresses out of a
            // satisfying state between candidate enumeration and worker spawn.
            if (item.DependsOn.Count > 0)
            {
                var allItems = new List<WorkItem>();
                await foreach (var i in _store.ListAsync(ct)) allItems.Add(i);
                var statesById = WorkItemDependencies.BuildStateMap(allItems);
                if (!WorkItemDependencies.AreSatisfied(item.DependsOn, statesById))
                {
                    _log.LogInformation(
                        "Worker {WorkerId} skipping {Id}: dependsOn gate not satisfied", workerIndex, id);
                    return;
                }
            }

            // Run the refactor project lock before project/release/router gates so
            // blocked same-project items do not mutate release state, consume quota,
            // park for paused agents, or fail for routing while a refactor already
            // owns the project. The locked check below remains the final race guard
            // immediately before StartedAt is written.
            var refactorGateLock = GetBudgetLock(item.ProjectId);
            await refactorGateLock.WaitAsync(ct);
            try
            {
                if (await TryDeferForRefactorExclusivityAsync(item, ct))
                    return;
            }
            finally
            {
                refactorGateLock.Release();
            }

            // Load the project once for quota routing and budget caps.
            Project? project = null;
            if (_projects is not null)
            {
                try { project = await _projects.GetAsync(item.ProjectId, ct); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Worker {WorkerId}: could not load project for {Id}; routing/budget skipped", workerIndex, id);
                }
            }

            // Release branch override: if this work item is linked to a release,
            // ensure the release branch exists and point BaseBranch at it so the
            // pipeline checks out and pushes to the release branch rather than main.
            if (item.ReleaseId is { } releaseId && project is not null && _releaseService is not null
                && item.BaseBranch is null)
            {
                try
                {
                    var releaseBranch = await _releaseService.EnsureReleaseBranchForItemAsync(releaseId, project, ct);
                    if (releaseBranch is not null)
                        item = item with { BaseBranch = releaseBranch };
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Worker {WorkerId}: release branch setup failed for {Id}; item will use configured base branch",
                        workerIndex, id);
                }
            }

            // Pin the work/headless baseline image ref BEFORE routing so the
            // in-VM smoke gate probes (and caches against) the exact image a
            // matching work-profile dispatch will clone, not whatever baseline
            // happens to be active now. Without this, the router gates on a null
            // ref → the gate's active-baseline fallback, while pickup below
            // stamps the project work-phase baseline hash that PipelineRunner
            // later re-gates on for matching work targets; a mismatch can route
            // to an agent that passed the wrong image and then fail the item
            // (AC#1). The persisted StartedAt/BaselineImageRef write still
            // happens under the budget lock below; this only settles the
            // in-memory ref.
            if (item.BaselineImageRef is null)
            {
                var pinnedRef = ResolveBaselineRefForPickup(item, project);
                if (pinnedRef is not null)
                    item = item with { BaselineImageRef = pinnedRef };
            }

            // Quota routing: resolve which agent to use, or decide to wait.
            // Skipped entirely (no probe, no wait) when no agent class is configured.
            //
            // We pass `this` (IAgentSlotGate) as the router's per-agent cap
            // gate so that when the top-ranked eligible member is at its cap,
            // routing spills to the next eligible member atomically inside
            // the candidate walk instead of pinning the item to a saturated
            // agent and deferring. When ResolveAsync returns Chosen != null
            // it has already test-and-reserved the chosen member's slot via
            // the gate; the outer finally releases on every exit path.
            var shouldRouteWorkAgentAtPickup = ShouldRouteWorkAgentAtPickup(item);
            if (_router is not null && !isAgentControlItem)
            {
                var decision = await _router.ResolveAsync(item, project, ct, slotGate: this);
                if (decision.ShouldWait)
                {
                    if (decision.WaitingForPausedAgent && !shouldRouteWorkAgentAtPickup)
                    {
                        _log.LogInformation(
                            "Work item {Id}: paused class-routing verdict deferred to pipeline phase handling for state {State}",
                            item.Id, item.State);
                    }
                    else
                    {
                        if (decision.WaitingForPausedAgent)
                        {
                            await ParkForAgentResumeAsync(
                                item,
                                decision.Reason,
                                decision.PausedAgents.Count == 1 ? decision.PausedAgents[0] : null,
                                ct);
                            return;
                        }

                        // Honour the router's suggested delay verbatim — when a
                        // quota-passing member was at cap, the router has already
                        // picked the short cap-retry interval; pure quota stalls
                        // use the longer QuotaRecheckInterval. AnyMemberAtCap only
                        // drives the per-agent ConcurrencyGated audit emission.
                        var deferDelay = decision.SuggestedRecheckIn;
                        if (decision.AnyMemberAtCap)
                        {
                            if (decision.AtCapMembers.Count > 0)
                            {
                                foreach (var atCapMember in decision.AtCapMembers)
                                {
                                    AuditLog.ConcurrencyGated(item.Id, atCapMember.Agent,
                                        GetRunning(atCapMember), GetAgentCap(atCapMember));
                                }
                            }
                            else
                            {
                                foreach (var atCapAgent in decision.AtCapAgents)
                                {
                                    AuditLog.ConcurrencyGated(item.Id, atCapAgent,
                                        GetRunning(atCapAgent), GetAgentCap(atCapAgent));
                                }
                            }
                        }
                        AuditLog.QuotaRouterDeferred(item.Id, deferDelay);
                        ScheduleDeferredRequeue(item.Id, deferDelay, ct);
                        return;
                    }
                }
                if (decision.Chosen is { } chosen)
                {
                    item = item with
                    {
                        Agent = chosen.Agent,
                        AgentInstanceId = chosen.RouteKey,
                        ModelId = chosen.ModelId,
                        ReasoningMode = chosen.ReasoningMode,
                    };
                    if (decision.SlotReserved)
                    {
                        // Router already reserved the slot through our gate —
                        // outer finally releases on every exit path.
                        agentRouteForRelease = chosen.RouteKey;
                        agentSlotReserved = true;
                    }
                }
                else if (decision.NoEligibleMembers)
                {
                    _log.LogError("Work item {Id}: {Reason}", item.Id, decision.Reason);
                    AuditLog.WorkItemFailed(item.Id, decision.Reason);
                    await _store.UpdateAsync(item.With(WorkItemState.Failed, decision.Reason), ct);
                    return;
                }
            }

            // Per-agent concurrency cap reservation for items that did NOT go
            // through class routing (no AgentClassRouter, or no class configured
            // for this item). Class-routed items already had their slot
            // reserved atomically inside ResolveAsync via the IAgentSlotGate;
            // re-reserving here would double-count. When the cap is hit on a
            // direct-agent item, defer-requeue with the short cap-retry delay
            // so the next pickup (after some agent slot frees up) finds it
            // again. Reservation is held for the life of the worker and
            // released in the outer finally.
            if (!agentSlotReserved && !isAgentControlItem)
            {
                var effectiveDirectAgent = item.Agent ?? project?.DefaultAgent;
                var directAvailability = shouldRouteWorkAgentAtPickup && effectiveDirectAgent is { } directAgent
                    ? _dispatchAvailability?.GetAvailability(new AgentMembership
                    {
                        Agent = directAgent,
                        InstanceId = item.AgentInstanceId,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 100,
                    })
                    : null;
                if (effectiveDirectAgent is { } pausedCandidate
                    && IsOperatorPaused(directAvailability))
                {
                    var reason = directAvailability?.Reason ?? AgentDispatchAvailability.PausedReasonPrefix;
                    await ParkForAgentResumeAsync(
                        item,
                        $"agent '{pausedCandidate.Value}' {reason}",
                        pausedCandidate,
                        ct);
                    return;
                }

                if (item.Agent is { } routedAgent)
                {
                    var routeKey = ResolveDirectRouteKey(routedAgent, item.AgentInstanceId);
                    var cap = GetAgentCapForRoute(routedAgent, routeKey);
                    if (!TryReserveRoute(routeKey, cap))
                    {
                        var running = GetRunningForRoute(routeKey);
                        _log.LogInformation(
                            "Worker {WorkerId} deferring {Id}: per-agent cap reached for {RouteKey} (running={Running} cap={Cap})",
                            workerIndex, id, routeKey, running, cap);
                        AuditLog.ConcurrencyGated(item.Id, routedAgent, running, cap);
                        ScheduleDeferredRequeue(item.Id, _quotaRouterOptions?.CapRetryRecheckInterval ?? DefaultCapRetryRecheckInterval, ct);
                        return;
                    }
                    // Reservation successful — outer finally releases on exit.
                    agentRouteForRelease = routeKey;
                    agentSlotReserved = true;
                }
            }

            // Per-project pause gate: check before the budget lock so paused projects
            // don't consume a budget lock slot. Block is pickup-only; in-flight items
            // already running are not cancelled (same semantics as the global pause).
            if (project is not null && _queueController is not null)
            {
                var projState = await _queueController.GetProjectStateAsync(item.ProjectId, ct);
                if (projState is { Paused: true })
                {
                    _log.LogInformation(
                        "Worker {WorkerId} skipping {Id}: project {ProjectId} queue is paused — {Reason}",
                        workerIndex, id, item.ProjectId.Value, projState.PausedReason);
                    ScheduleDeferredRequeue(item.Id, _budgetDeferralRecheck?.Current.PausedProjectRecheck ?? TimeSpan.FromMinutes(1), ct);
                    return;
                }
            }

            // Budget gate + StartedAt write held under a per-project lock to prevent
            // TOCTOU: without the lock, concurrent workers for the same project can all
            // pass the budget check before any of them has committed StartedAt, allowing
            // the per-project caps to be exceeded by up to MaxConcurrentWorkers−1 items.
            var budgetLock = GetBudgetLock(item.ProjectId);
            await budgetLock.WaitAsync(ct);
            try
            {
                // Final refactor exclusivity check stays inside the
                // per-project budget lock, adjacent to the StartedAt write,
                // so a concurrent same-project pickup cannot slip between
                // the split-read and the in-flight marker. This does not
                // depend on project metadata being available.
                if (await TryDeferForRefactorExclusivityAsync(item, ct))
                    return;

                if (project is not null)
                {
                    var deferReason = await CheckBudgetAsync(item, project.Budget, ct);
                    if (deferReason is not null)
                    {
                        AuditLog.BudgetDeferred(item.Id, item.ProjectId, deferReason.Reason);
                        if (_webhooks is not null)
                        {
                            _ = _webhooks.PublishAsync(new WebhookEvent
                            {
                                Event = "budget.deferred",
                                WorkItem = item,
                                Project = project,
                                Details = new { deferReason.Reason, suggestedRetryAt = DateTimeOffset.UtcNow + deferReason.RecheckIn },
                            }, CancellationToken.None);
                        }
                        ScheduleDeferredRequeue(item.Id, deferReason.RecheckIn, ct);
                        return;
                    }
                }

                // Record first pickup time inside the lock so the count is visible
                // to the next worker before it runs its own budget/refactor check.
                if (item.StartedAt is null)
                {
                    var pipelineItem = item;
                    var baselineRef = ResolveBaselineRefForPickup(item, project);
                    item = item with
                    {
                        StartedAt = DateTimeOffset.UtcNow,
                        BaselineImageRef = item.BaselineImageRef ?? baselineRef,
                    };
                    await _store.UpdateAsync(item, ct);
                    item = pipelineItem with { StartedAt = item.StartedAt, BaselineImageRef = item.BaselineImageRef };
                }
            }
            finally
            {
                budgetLock.Release();
            }

            using var registration = _cancellations.Register(item.Id);
            AuditLog.WorkItemPickedUp(workerIndex, item.Id);
            try
            {
                await _pipeline.RunAsync(item, registration.Token, ct);
            }
            catch (PhaseCancellationException pex) when (ct.IsCancellationRequested)
            {
                // Host shutdown that bubbled all the way back up — log structured
                // attribution so post-incident triage can correlate the worker
                // exit with the phase + source the pipeline saw.
                _log.LogInformation(
                    "Worker {WorkerId} item {Id} aborted by host shutdown: phase={Phase} source={CancellationSource}",
                    workerIndex, id, pex.Phase, pex.Source);
                await RecoverHostShutdownAbortedItemAsync(id);
                return;
            }
            catch (PhaseCancellationException pex)
            {
                _log.LogInformation(
                    "Worker {WorkerId} item {Id} cancelled in phase {Phase}: source={CancellationSource}",
                    workerIndex, id, pex.Phase, pex.Source);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await RecoverHostShutdownAbortedItemAsync(id);
                return;
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("Worker {WorkerId} item {Id} cancelled", workerIndex, id);
            }
            catch (SandboxDiskDeferredException dskEx)
            {
                // Disk-guard preflight refused to launch a sandbox. Same
                // semantics as the budget-cap deferral: emit the audit + the
                // disk.deferred webhook so existing alerting fires, then
                // schedule a re-pickup. In-flight items already running on
                // other workers are not touched.
                var deferredItem = await ResetInfrastructureDeferredItemAsync(item, CancellationToken.None);
                AuditLog.DiskDeferred(deferredItem.Id, dskEx.MountPath, dskEx.FreeBytes, dskEx.ThresholdBytes);
                if (_webhooks is not null)
                {
                    _ = _webhooks.PublishAsync(new WebhookEvent
                    {
                        Event = "disk.deferred",
                        WorkItem = deferredItem,
                        Project = project,
                        Details = new
                        {
                            mountPath = dskEx.MountPath,
                            freeBytes = dskEx.FreeBytes,
                            thresholdBytes = dskEx.ThresholdBytes,
                            suggestedRetryAt = DateTimeOffset.UtcNow + dskEx.RecheckIn,
                        },
                    }, CancellationToken.None);
                }
                ScheduleDeferredRequeue(item.Id, dskEx.RecheckIn, ct);
                return;
            }
            catch (SandboxProvisioningDeferredException provEx)
            {
                var deferredItem = await ResetInfrastructureDeferredItemAsync(item, CancellationToken.None);
                AuditLog.SandboxProvisioningDeferred(
                    deferredItem.Id,
                    provEx.Provider,
                    provEx.Operation,
                    provEx.ErrorClass,
                    deferredItem.State.ToString(),
                    provEx.RecheckIn);

                if (_webhooks is not null)
                {
                    _ = _webhooks.PublishAsync(new WebhookEvent
                    {
                        Event = "sandbox.provisioning_deferred",
                        WorkItem = deferredItem,
                        Project = project,
                        Details = new
                        {
                            provider = provEx.Provider,
                            operation = provEx.Operation,
                            errorClass = provEx.ErrorClass,
                            resumeState = deferredItem.State.ToString(),
                            suggestedRetryAt = DateTimeOffset.UtcNow + provEx.RecheckIn,
                        },
                    }, CancellationToken.None);
                }

                _log.LogWarning(
                    "Worker {WorkerId} deferring {Id}: sandbox provisioning transient ({Provider}/{Operation}, {ErrorClass}); resumeState={ResumeState}",
                    workerIndex, id, provEx.Provider, provEx.Operation, provEx.ErrorClass, deferredItem.State);
                ScheduleDeferredRequeue(item.Id, provEx.RecheckIn, ct);
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker {WorkerId} unexpected failure on {Id}", workerIndex, id);
            }
        }
        finally
        {
            _activeItems.TryRemove(id, out _);
            _progressClock.Stamp(DateTimeOffset.UtcNow);

            // Release the per-agent slot if we reserved one (the only state in
            // which it was incremented). Doing this here — rather than at the
            // call site — guarantees we never leak a slot on the disk-deferred /
            // budget-deferred / pipeline-exception code paths.
            if (agentSlotReserved && agentRouteForRelease is { } releaseRoute)
            {
                ReleaseRoute(releaseRoute);
            }

            // Stop the heartbeat and remove the registry row on any exit path
            // (success, failure, or cancellation). On clean exit this clears
            // the current_work_item_id linkage; on crash the row stays and the
            // reaper cleans it up after DeadWorkerThreshold elapses.
            if (registeredWorkerId is not null)
            {
                heartbeatCts?.Cancel();
                heartbeatCts?.Dispose();
                try
                {
                    await _workerRegistry!.DeregisterAsync(registeredWorkerId, CancellationToken.None);
                    AuditLog.WorkerDeregistered(registeredWorkerId);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to deregister worker {WorkerId}; row will be reaped by DeadWorkerReaper", registeredWorkerId);
                }
            }
        }

        // No-progress re-dispatch backoff (incident 2026-06-04). Reached only on
        // the pipeline-ran fall-through — the quota/budget/cap/disk deferrals all
        // `return` earlier and already set _deferredItems. If the worker ran but
        // the item is STILL in the same re-pickable state it was dispatched in
        // (item.State is the dispatched state; the pipeline transitions the store,
        // not this local), it made no progress and the slot-release dispatch wake
        // would re-pick it instantly. Defer with escalating backoff so a loop is
        // delayed/no work, never a high-load spawn storm; fail after a cap so a
        // genuinely poisoned item is cleared rather than looping forever.
        if (!_deferredItems.ContainsKey(id))
        {
            var afterRun = await _store.GetAsync(id, CancellationToken.None);
            if (afterRun is not null
                && afterRun.State == item.State
                && afterRun.State is WorkItemState.Queued
                    or WorkItemState.WorkComplete or WorkItemState.AuditPassed)
            {
                var attempt = _noProgressRedispatch.AddOrUpdate(id, 1, static (_, c) => c + 1);
                if (attempt >= MaxNoProgressRedispatches)
                {
                    _noProgressRedispatch.TryRemove(id, out _);
                    _log.LogWarning(
                        "Worker {WorkerId} failing {Id}: no progress after {Attempts} consecutive re-dispatches in state {State}",
                        workerIndex, id, attempt, afterRun.State);
                    await _store.UpdateAsync(
                        afterRun.With(WorkItemState.Failed,
                            $"no progress after {attempt} consecutive re-dispatches (dispatched but the pipeline made no progress; likely a poisoned work branch or a stuck pickup phase)"),
                        CancellationToken.None);
                }
                else
                {
                    var backoff = TimeSpan.FromMilliseconds(Math.Min(
                        NoProgressBackoffBase.TotalMilliseconds * Math.Pow(2, attempt - 1),
                        NoProgressBackoffMax.TotalMilliseconds));
                    _log.LogDebug(
                        "Worker {WorkerId} backing off {Id} {Ms}ms: no-progress re-dispatch #{Attempt}",
                        workerIndex, id, backoff.TotalMilliseconds, attempt);
                    ScheduleDeferredRequeue(id, backoff, ct);
                }
            }
            else
            {
                // Progressed (or item gone) — clear the counter so a future
                // unrelated re-pickup starts fresh.
                _noProgressRedispatch.TryRemove(id, out _);
            }
        }

        // After the pipeline finishes (any outcome), check whether any
        // Queued items were waiting on this item and are now unblocked.
        await EnqueueSatisfiedDependentsAsync(id, ct);

        // Notify the release state machine that a release-linked item has completed.
        // ReleaseService checks whether all items for the release are now terminal
        // and, if so, triggers the closed→in_review transition automatically.
        if (item.ReleaseId is { } completedReleaseId && _releaseService is not null)
        {
            var svc = _releaseService;
            _ = Task.Run(async () =>
            {
                try { await svc.OnWorkItemTerminalAsync(completedReleaseId, CancellationToken.None); }
                catch (Exception ex) { _log.LogError(ex, "OnWorkItemTerminalAsync threw for release {Id}", completedReleaseId); }
            });
        }
    }

    private static bool ShouldRouteWorkAgentAtPickup(WorkItem item) =>
        item.State is WorkItemState.Queued or WorkItemState.Reworking or WorkItemState.ReworkingForConflict;

    private static bool IsOperatorPaused(AgentAvailability? availability) =>
        AgentDispatchAvailability.IsPausedVerdict(availability);

    private async Task ParkForAgentResumeAsync(
        WorkItem item,
        string reason,
        AgentKind? pausedAgent,
        CancellationToken ct)
        => await WorkItemAgentPauseParking.ParkAsync(
            _store,
            _webhooks,
            _log,
            item,
            reason,
            project: null,
            pausedAgent,
            ct);

    private sealed class WorkerSlotLease
    {
        private int _released;

        public WorkerSlotLease(int workerIndex, WorkItemId workItemId)
        {
            WorkerIndex = workerIndex;
            WorkItemId = workItemId;
        }

        public int WorkerIndex { get; }
        public WorkItemId WorkItemId { get; }
        public string? RegistryWorkerId { get; private set; }
        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public bool TryAttachRegistryWorkerId(string workerId)
        {
            if (IsReleased)
                return false;
            RegistryWorkerId = workerId;
            return true;
        }

        public bool TryMarkReleased()
            => Interlocked.Exchange(ref _released, 1) == 0;
    }

    private sealed record BudgetDeferral(string Reason, TimeSpan RecheckIn);

    /// <summary>
    /// Checks per-project budget caps against the store. Returns a
    /// <see cref="BudgetDeferral"/> if any cap is exceeded, null otherwise.
    /// Single SQLite query per cap; each query hits the index on (project_id, started_at).
    /// </summary>
    private async Task<BudgetDeferral?> CheckBudgetAsync(WorkItem item, ProjectBudget budget, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (budget.MaxItemsPerHour > 0)
        {
            var count = await _store.CountStartedInWindowAsync(item.ProjectId, now.AddHours(-1), ct);
            if (count >= budget.MaxItemsPerHour)
                return new BudgetDeferral(
                    $"hourly limit: {count}/{budget.MaxItemsPerHour} items started in last hour",
                    _budgetDeferralRecheck?.Current.HourlyLimitRecheck ?? TimeSpan.FromMinutes(5));
        }

        if (budget.MaxItemsPerDay > 0)
        {
            var count = await _store.CountStartedInWindowAsync(item.ProjectId, now.AddHours(-24), ct);
            if (count >= budget.MaxItemsPerDay)
                return new BudgetDeferral(
                    $"daily limit: {count}/{budget.MaxItemsPerDay} items started in last 24h",
                    _budgetDeferralRecheck?.Current.DailyLimitRecheck ?? TimeSpan.FromHours(1));
        }

        if (budget.MaxConcurrentForProject > 0)
        {
            var count = await _store.CountInFlightAsync(item.ProjectId, ct);
            if (count >= budget.MaxConcurrentForProject)
                return new BudgetDeferral(
                    $"concurrent limit: {count}/{budget.MaxConcurrentForProject} items in flight",
                    _budgetDeferralRecheck?.Current.ConcurrentLimitRecheck ?? TimeSpan.FromMinutes(1));
        }

        return null;
    }

    private SemaphoreSlim GetBudgetLock(ProjectId projectId) =>
        _budgetLocks.GetOrAdd(projectId.Value, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// B1 stamping: returns the baseline image ref the registered sandbox
    /// provider would currently bake for this work item's work-phase profile,
    /// or null when no pinning applies. Returns null when the provider does
    /// not model baselines (no <see cref="IBaselineImageResolver"/> capability),
    /// when the project's work-phase network profile is unset, or when the
    /// resolver returns null (provider says "no baseline for this combo" —
    /// e.g. <c>UseBaselineImages=false</c>). The dispatcher stamps the result
    /// on the work item right alongside the StartedAt write; downstream sandbox
    /// target resolution uses it only when the requested profile/flavor matches
    /// this work/headless target. Never throws — a resolver that throws would
    /// have to fail open: pinning is an optimisation, not a correctness primitive.
    /// </summary>
    private string? ResolveBaselineRefForPickup(WorkItem item, Project? project)
    {
        // Default to the project's work-phase profile. Later phases may use
        // different profiles; PipelineRunner/AgentClassRouter only forward this
        // pin to matching work/headless targets.
        var profile = project?.NetworkProfiles.Work;
        // Default to Headless; graphical targets resolve their own baseline in
        // the pipeline instead of reusing this work pin.
        try
        {
            return _baselineResolver.ResolveBaselineRef(profile, SandboxProfileFlavor.Headless);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Baseline-ref resolver threw for work item {Id}; proceeding without pin", item.Id);
            return null;
        }
    }

    private async Task<WorkItem> ResetInfrastructureDeferredItemAsync(WorkItem item, CancellationToken ct)
    {
        var current = await _store.GetAsync(item.Id, ct).ConfigureAwait(false) ?? item;
        var reset = WorkItemRecoveryPolicy.BuildInfrastructureDeferredResumeState(
            current,
            DateTimeOffset.UtcNow);
        if (reset is null)
            return current;

        if (reset == current)
            return current;

        var updated = await _store.TryUpdateIfStateAsync(reset, current.State, ct).ConfigureAwait(false);
        if (updated)
            return reset;

        return await _store.GetAsync(item.Id, ct).ConfigureAwait(false) ?? current;
    }

    /// <summary>
    /// Fires a background task that re-enqueues <paramref name="id"/> after
    /// <paramref name="delay"/>. Used for quota, budget, and infrastructure
    /// deferrals. The item remains in its recoverable state; the deferred task
    /// simply puts it back on the channel after the recheck window. On shutdown
    /// (stoppingToken cancelled), the delayed task exits cleanly; the item is
    /// recovered via ReplayPendingAsync on the next start.
    /// </summary>
    public void ScheduleInfrastructureDeferredRequeue(WorkItemId id, TimeSpan delay, CancellationToken stoppingToken = default)
        => ScheduleDeferredRequeue(id, delay, stoppingToken);

    private async Task<bool> TryDeferForRefactorExclusivityAsync(WorkItem item, CancellationToken ct)
    {
        // Reads the same in-flight population CountInFlightAsync sees, split by
        // JobType, while excluding the candidate itself. The exclusion matters
        // for recovered pass-through states (WorkComplete, AuditPassed, Merged):
        // they keep StartedAt and are dispatch-eligible continuations, so a
        // resumed Refactor must not mistake its own row for a competing refactor.
        var (refactorInFlight, otherInFlight) =
            await _store.CountInFlightSplitByRefactorAsync(item.ProjectId, ct, item.Id);
        var refactorRecheck =
            _budgetDeferralRecheck?.Current.RefactorExclusivityRecheck
            ?? TimeSpan.FromMinutes(1);

        if (item.JobType == JobType.Refactor)
        {
            if (refactorInFlight == 0 && otherInFlight == 0)
                return false;

            var reason = refactorInFlight > 0
                ? $"refactor exclusivity: another refactor is in flight for project '{item.ProjectId.Value}' (refactor={refactorInFlight}, other={otherInFlight})"
                : $"refactor exclusivity: project '{item.ProjectId.Value}' has {otherInFlight} in-flight non-refactor item(s); refactor waits for the project to drain";
            AuditLog.RefactorExclusivityDeferred(item.Id, item.ProjectId, reason);
            ScheduleDeferredRequeue(item.Id, refactorRecheck, ct);
            return true;
        }

        if (refactorInFlight == 0)
            return false;

        var nonRefactorReason =
            $"refactor exclusivity: a refactor is in flight for project '{item.ProjectId.Value}'; non-refactor items wait until it completes";
        AuditLog.RefactorExclusivityDeferred(item.Id, item.ProjectId, nonRefactorReason);
        ScheduleDeferredRequeue(item.Id, refactorRecheck, ct);
        return true;
    }

    private void ScheduleDeferredRequeue(WorkItemId id, TimeSpan delay, CancellationToken stoppingToken)
    {
        var count = Interlocked.Increment(ref _pendingDeferrals);
        if (count > DeferralWarningThreshold)
            _log.LogWarning(
                "Deferred requeue backlog is {Count} items; deferrals may be sustained across many work items",
                count);

        // Mark the item as currently-deferred so the priority pickup query skips it
        // while the Task.Delay is sleeping. Without this guard, every dispatch tick
        // would re-pick the same Queued item, hit the same defer condition, and
        // accumulate redundant deferral tasks.
        _deferredItems[id] = 0;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, stoppingToken);
                _log.LogInformation("Re-enqueueing deferred work item {Id} after defer interval", id);
                _deferredItems.TryRemove(id, out _);
                await _queue.EnqueueAsync(id, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service is shutting down; item will be recovered on next start.
                _deferredItems.TryRemove(id, out _);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to re-enqueue deferred work item {Id}", id);
                _deferredItems.TryRemove(id, out _);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingDeferrals);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Called after a work item reaches a terminal state. Scans the store for
    /// Queued items that were waiting on <paramref name="completedId"/> and
    /// enqueues those whose every dependency is now terminal.
    /// </summary>
    internal async Task EnqueueSatisfiedDependentsAsync(WorkItemId completedId, CancellationToken ct)
    {
        var allItems = new List<WorkItem>();
        await foreach (var item in _store.ListAsync(ct)) allItems.Add(item);
        var statesById = WorkItemDependencies.BuildStateMap(allItems);

        foreach (var candidate in WorkItemDependencies.FindSatisfiedDependents(completedId, allItems, statesById))
        {
            _log.LogInformation(
                "Enqueuing work item {Id}: all dependencies are now terminal", candidate.Id);
            AuditLog.WorkItemDependenciesResolved(candidate.Id);
            await _queue.EnqueueAsync(candidate.Id, ct);
        }
    }
}

/// <summary>
/// Configuration for the orchestrator worker pool.
/// Bound from DI; consumers should prefer <see cref="WorkerPoolOptions"/>
/// via <c>CodeyBox:WorkerPool</c> config; this type bridges the two.
/// </summary>
public sealed record OrchestratorOptions
{
    public int MaxConcurrentWorkers { get; init; } = 1;
    public int MaxConcurrentSandboxes { get; init; } = 2;
    public TimeSpan MinSpawnInterval { get; init; } = TimeSpan.Zero;
    public TimeSpan ShutdownDrainTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of times the recovery loop will reset a mid-flight work
    /// item before giving up and transitioning it to
    /// <see cref="WorkItemState.AbandonedAfterRecoveryAttempts"/>. Default 10.
    /// The cap exists to catch genuinely stuck items (an item that fails on
    /// every pickup attempt for the same reason), not routine host restarts.
    /// 3 turned out to be too tight in active development environments where
    /// the orchestrator process gets restarted several times an hour to pick
    /// up config or code changes — a healthy long-running work item could
    /// burn through 3 attempts in an afternoon without ever actually being
    /// broken. 10 still cleanly catches a permanently-stuck item, but gives
    /// real work room to survive routine operator activity.
    /// Set to 0 (or any negative value) to disable the cap and recover
    /// indefinitely (not recommended in production — a permanently-stuck
    /// item will be re-enqueued on every orchestrator restart without bound).
    /// </summary>
    public int MaxRecoveryAttempts { get; init; } = 10;

    /// <summary>
    /// Maximum number of times a work item will be silently re-queued after a
    /// transient host-side cancellation — i.e. an <see cref="OperationCanceledException"/>
    /// whose contributor the pipeline couldn't attribute to an operator cancel,
    /// configured per-phase timeout, host shutdown, or stuck-probe. Past this
    /// cap, the item transitions to <see cref="WorkItemState.Failed"/> with
    /// <c>failureKind=cancelled</c> and a pointed error message instead of
    /// being retried again.
    ///
    /// <para>
    /// Default 3: expensive items can survive a small number of transient
    /// host hiccups (TaskCanceledException from a leaked supervisor token,
    /// a brief network glitch surfaced as cancellation, etc.) without
    /// burning a $1K rework iteration. Set to 0 to disable the auto-retry
    /// path entirely — every unattributed cancellation will surface as Failed
    /// immediately, preserving the operator's full attention.
    /// </para>
    /// </summary>
    public int MaxTransientCancelRetries { get; init; } = 3;

    public AutoRetryOnQuotaFailureOptions AutoRetryOnQuotaFailure { get; init; } = new();

    /// <summary>
    /// Called by the dispatch loop immediately after the spawn timestamp is
    /// written, before <see cref="Task.Run"/>. Used by tests to capture the
    /// true spawn time rather than the thread-pool scheduling time.
    /// </summary>
    internal Action? OnWorkerSpawned { get; init; }

    /// <summary>
    /// Called by the dispatch loop after an item is reserved in <c>_activeItems</c>
    /// and before spawn pacing. Used by tests to close pause/reservation races.
    /// </summary>
    internal Func<WorkItemId, Task>? OnWorkerReservedForTest { get; init; }

    /// <summary>
    /// Legacy alias for <see cref="MaxConcurrentWorkers"/>.
    /// Preserved so existing tests that construct this record directly
    /// continue to compile; prefer <see cref="MaxConcurrentWorkers"/>.
    /// </summary>
    [Obsolete("Use MaxConcurrentWorkers instead. This property will be removed in a future version.")]
    public int Concurrency
    {
        get => MaxConcurrentWorkers;
        init => MaxConcurrentWorkers = value;
    }
}

public sealed record AutoRetryOnQuotaFailureOptions
{
    public bool Enabled { get; init; } = false;
    public TimeSpan PeriodicCheckInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ClockDriftSafetyMargin { get; init; } = TimeSpan.FromMinutes(2);
    public int MaxAutoRetriesPerWorkItem { get; init; } = 3;
}

/// <summary>Snapshot of worker pool state for the /workers/status endpoint.</summary>
public sealed record WorkerPoolStatus(
    int MaxConcurrent,
    int CurrentlyRunning,
    int QueuedCount,
    DateTimeOffset? LastSpawnAt);

/// <summary>
/// Snapshot of the per-agent concurrency state surfaced by the
/// <c>/concurrency</c> endpoint. <see cref="PerAgentCaps"/> reflects the
/// configured ceiling per agent kind; <see cref="CurrentlyRunningPerAgent"/>
/// is the live in-flight count.
/// </summary>
public sealed record ConcurrencyStateSnapshot(
    int GlobalMaxConcurrent,
    int CurrentlyRunningTotal,
    IReadOnlyDictionary<string, int> PerAgentCaps,
    IReadOnlyDictionary<string, int> CurrentlyRunningPerAgent);

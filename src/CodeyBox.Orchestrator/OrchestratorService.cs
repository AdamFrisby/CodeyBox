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
public sealed class OrchestratorService : BackgroundService
{
    private readonly ITaskQueue _queue;
    private readonly IWorkItemStore _store;
    private readonly IPipelineRunner _pipeline;
    private readonly CancellationRegistry _cancellations;
    private readonly OrchestratorOptions _opts;
    private readonly ILogger<OrchestratorService> _log;
    private readonly AgentClassRouter? _router;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IWebhookDispatcher? _webhooks;
    private readonly IWorkerRegistry? _workerRegistry;
    private readonly DeadWorkerOptions? _deadWorkerOpts;
    private readonly DeadWorkerReaper? _reaper;
    private readonly ReleaseService? _releaseService;

    // Tracks work item IDs that are currently being processed by a worker.
    // Guards against double-execution when two workers both enqueue the same
    // item (e.g., both see it as the last satisfied dependent simultaneously).
    private readonly ConcurrentDictionary<WorkItemId, byte> _activeItems = new();

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
        ReleaseService? releaseService = null)
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
        _webhooks = webhooks;
        _workerRegistry = workerRegistry;
        _deadWorkerOpts = deadWorkerOpts;
        _reaper = reaper;
        _releaseService = releaseService;
        _concurrencyGate = new SemaphoreSlim(opts.MaxConcurrentWorkers, opts.MaxConcurrentWorkers);
    }

    /// <summary>Snapshot for the /workers/status endpoint.</summary>
    public WorkerPoolStatus GetStatus()
    {
        var ticks = Interlocked.Read(ref _lastSpawnAtTicks);
        return new(
            _opts.MaxConcurrentWorkers,
            Volatile.Read(ref _currentlyRunning),
            _queue.Count,
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run the reaper once at startup before replaying pending items.
        // This transitions any items that were mid-flight when the previous
        // process crashed back to a recoverable state, so ReplayPendingAsync
        // finds them in their correct target states (Queued, WorkComplete, …)
        // rather than the stale worker-owned states.
        if (_reaper is not null)
            await _reaper.RunOnceAsync(stoppingToken);

        await ReplayPendingAsync(stoppingToken);

        // Collect in-flight item tasks so we can await them all on shutdown.
        // List is safe here: only the dispatch loop (single logical thread) touches it.
        var inFlight = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            // Pause gate: spin-wait while the queue is paused, without consuming
            // from the channel. In-flight workers continue normally during pause.
            if (!await WaitIfPausedAsync(stoppingToken)) break;

            WorkItemId? id;
            try { id = await _queue.DequeueAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            if (id is null) break;

            // Post-dequeue pause check: handles the race where the queue was paused
            // while we were blocked in DequeueAsync. Put the item back and re-check.
            if (_queueController is not null && _queueController.State == QueueState.Paused)
            {
                await _queue.EnqueueAsync(id.Value, stoppingToken);
                continue;
            }

            // Block until a concurrency slot is free.
            try { await _concurrencyGate.WaitAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }

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
                    if (wait > TimeSpan.Zero)
                    {
                        AuditLog.WorkerPoolSpawnThrottled((long)wait.TotalMilliseconds);
                        try { await Task.Delay(wait, stoppingToken); }
                        catch (OperationCanceledException)
                        {
                            TryReleaseConcurrencyGate();
                            break;
                        }
                    }
                }
            }

            // Record spawn timestamp before launching the task.
            lock (_spawnTimeLock) { _lastSpawnAtTicks = DateTimeOffset.UtcNow.Ticks; }
            try { _opts.OnWorkerSpawned?.Invoke(); }
            catch (Exception ex)
            {
                _log.LogError(ex, "OnWorkerSpawned callback threw; releasing concurrency slot and skipping item {Id}", id);
                TryReleaseConcurrencyGate();
                continue;
            }
            var workerIndex = Interlocked.Increment(ref _nextWorkerId);

            var capturedId = id.Value;
            // Increment before Task.Run so the counter is never transiently negative
            // if the task's finally block executes before we reach the increment.
            Interlocked.Increment(ref _currentlyRunning);
            var task = Task.Run(async () =>
            {
                AuditLog.WorkerPoolWorkerStarted(workerIndex, capturedId);
                try
                {
                    await RunItemAsync(workerIndex, capturedId, stoppingToken);
                }
                finally
                {
                    Interlocked.Decrement(ref _currentlyRunning);
                    AuditLog.WorkerPoolWorkerFinished(workerIndex, capturedId);
                    TryReleaseConcurrencyGate();
                }
            });

            inFlight.Add(task);
            // Prune completed tasks on every iteration to prevent unbounded growth.
            inFlight.RemoveAll(t => t.IsCompleted);
        }

        // Drain in-flight tasks before the hosted service exits.
        await Task.WhenAll(inFlight).ConfigureAwait(false);
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
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return false; }
        }
        return true;
    }

    // Exposed as internal so tests can invoke recovery in isolation without
    // starting the full worker loop.
    internal Task ReplayPendingForTestAsync(CancellationToken ct) => ReplayPendingAsync(ct);
    internal WorkItem? TryBuildRecoveredStateForTest(WorkItem item) => TryBuildRecoveredState(item);

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
    /// Each recovery increments <see cref="WorkItem.RecoveryAttempts"/>. Items that
    /// exceed <see cref="OrchestratorOptions.MaxRecoveryAttempts"/> are transitioned
    /// to <see cref="WorkItemState.AbandonedAfterRecoveryAttempts"/> instead.
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
            var recovered = TryBuildRecoveredState(item);
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
    /// </summary>
    private WorkItem? TryBuildRecoveredState(WorkItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && item.State is WorkItemState.Working or WorkItemState.Reworking)
        {
            return item with
            {
                StartedAt = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        if (item.State == WorkItemState.Working)
        {
            return item with
            {
                State = WorkItemState.Failed,
                LastError = "worker died while work phase was running without a preempt checkpoint",
                RecoveryAttempts = item.RecoveryAttempts + 1,
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        WorkItemState? targetState = item.State switch
        {
            WorkItemState.Auditing => WorkItemState.WorkComplete,
            WorkItemState.Reworking => WorkItemState.WorkComplete,
            WorkItemState.Merging => WorkItemState.AuditPassed,
            // WorkComplete / AuditPassed / Merged: pipeline resumes at the correct
            // phase; no state change needed, just re-enqueue.
            // UpstreamPushing → Merged: the skip flags in PipelineRunner treat Merged
            // as "all phases done, go straight to RunUpstreamPushPhaseAsync". Keeping
            // UpstreamPushing would leave skipWork/skipAudit/skipMerge all false and
            // trigger a full pipeline replay from scratch.
            WorkItemState.WorkComplete or WorkItemState.AuditPassed or WorkItemState.Merged
                => item.State,
            WorkItemState.UpstreamPushing => WorkItemState.Merged,
            _ => null,
        };

        if (targetState is null) return null;

        // Only backward-reset transitions (Auditing→WorkComplete, etc.) and
        // UpstreamPushing→Merged represent genuinely interrupted in-flight work and count
        // against the recovery cap. Passthrough re-enqueues (WorkComplete/AuditPassed/Merged
        // left as-is) are natural resting points — a routine rolling restart should not burn
        // a recovery credit for items waiting between pipeline phases.
        bool isInterruptedWork = targetState.Value != item.State;
        var newAttempts = isInterruptedWork ? item.RecoveryAttempts + 1 : item.RecoveryAttempts;

        // MaxRecoveryAttempts <= 0 means unlimited (no cap). Only enforce when > 0.
        if (isInterruptedWork && _opts.MaxRecoveryAttempts > 0 && newAttempts > _opts.MaxRecoveryAttempts)
        {
            return item with
            {
                State = WorkItemState.AbandonedAfterRecoveryAttempts,
                LastError = $"abandoned after {_opts.MaxRecoveryAttempts} recovery attempts; was {item.State}",
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        return item.With(targetState.Value) with { RecoveryAttempts = newAttempts };
    }

    private async Task RunItemAsync(int workerIndex, WorkItemId id, CancellationToken ct)
    {
        var item = await _store.GetAsync(id, ct);
        if (item is null)
        {
            _log.LogWarning("Worker {WorkerId} dequeued unknown work item {Id}", workerIndex, id);
            return;
        }
        if (item.State is WorkItemState.Cancelled or WorkItemState.Done
            or WorkItemState.Failed or WorkItemState.AuditFailed
            or WorkItemState.MergeConflictResolutionFailed
            or WorkItemState.AbandonedAfterRecoveryAttempts)
        {
            _log.LogInformation("Worker {WorkerId} skipping {Id} in terminal state {State}", workerIndex, id, item.State);
            return;
        }

        // Items parked waiting for operator input must not be processed by a worker.
        // They are re-enqueued by the answer/dismiss-question endpoints when all questions resolve.
        if (item.State is WorkItemState.NeedsOperatorInput)
        {
            _log.LogWarning("Worker {WorkerId} skipping {Id}: still in NeedsOperatorInput state", workerIndex, id);
            return;
        }

        // Double-enqueue guard: when two workers simultaneously complete
        // the last dependency of the same downstream item, both may enqueue
        // it. Only one worker should run the pipeline for a given item at a
        // time. TryAdd is atomic; the losing worker skips gracefully.
        if (!_activeItems.TryAdd(id, 0))
        {
            _log.LogInformation(
                "Worker {WorkerId} skipping {Id}: already being processed by another worker", workerIndex, id);
            return;
        }

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

            // Dependency gate: skip items whose deps aren't all terminal yet.
            if (item.DependsOn.Count > 0)
            {
                var allItems = new List<WorkItem>();
                await foreach (var i in _store.ListAsync(ct)) allItems.Add(i);
                var statesById = WorkItemDependencies.BuildStateMap(allItems);
                if (!WorkItemDependencies.AreSatisfied(item.DependsOn, statesById))
                {
                    _log.LogInformation(
                        "Worker {WorkerId} skipping {Id}: dependencies not yet terminal", workerIndex, id);
                    return;
                }
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

            // Quota routing: resolve which agent to use, or decide to wait.
            // Skipped entirely (no probe, no wait) when no agent class is configured.
            if (_router is not null)
            {
                var decision = await _router.ResolveAsync(item, project, ct);
                if (decision.ShouldWait)
                {
                    AuditLog.QuotaRouterDeferred(item.Id, decision.SuggestedRecheckIn);
                    ScheduleDeferredRequeue(item.Id, decision.SuggestedRecheckIn, ct);
                    return;
                }
                if (decision.Chosen is { } chosen)
                    item = item with { Agent = chosen.Agent, ModelId = chosen.ModelId, ReasoningMode = chosen.ReasoningMode };
                else if (decision.NoEligibleMembers)
                {
                    _log.LogError("Work item {Id}: {Reason}", item.Id, decision.Reason);
                    AuditLog.WorkItemFailed(item.Id, decision.Reason);
                    await _store.UpdateAsync(item.With(WorkItemState.Failed, decision.Reason), ct);
                    return;
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
                    ScheduleDeferredRequeue(item.Id, TimeSpan.FromMinutes(1), ct);
                    return;
                }
            }

            // Budget gate + StartedAt write held under a per-project lock to prevent
            // TOCTOU: without the lock, concurrent workers for the same project can all
            // pass the budget check before any of them has committed StartedAt, allowing
            // the per-project caps to be exceeded by up to MaxConcurrentWorkers−1 items.
            if (project is not null)
            {
                var budgetLock = GetBudgetLock(item.ProjectId);
                await budgetLock.WaitAsync(ct);
                try
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
                    // Record first pickup time inside the lock so the count is visible
                    // to the next worker before it runs its own budget check.
                    if (item.StartedAt is null)
                    {
                        var pipelineItem = item;
                        item = item with
                        {
                            StartedAt = DateTimeOffset.UtcNow,
                        };
                        await _store.UpdateAsync(item, ct);
                        item = pipelineItem with { StartedAt = item.StartedAt };
                    }
                }
                finally
                {
                    budgetLock.Release();
                }
            }
            else
            {
                // No project → no budget check; still record first pickup time.
                if (item.StartedAt is null)
                {
                    var pipelineItem = item;
                    item = item with
                    {
                        StartedAt = DateTimeOffset.UtcNow,
                    };
                    await _store.UpdateAsync(item, ct);
                    item = pipelineItem with { StartedAt = item.StartedAt };
                }
            }

            using var registration = _cancellations.Register(item.Id);
            AuditLog.WorkItemPickedUp(workerIndex, item.Id);
            try
            {
                await _pipeline.RunAsync(item, registration.Token, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("Worker {WorkerId} item {Id} cancelled", workerIndex, id);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Worker {WorkerId} unexpected failure on {Id}", workerIndex, id);
            }
        }
        finally
        {
            _activeItems.TryRemove(id, out _);

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
                    TimeSpan.FromMinutes(5));
        }

        if (budget.MaxItemsPerDay > 0)
        {
            var count = await _store.CountStartedInWindowAsync(item.ProjectId, now.AddHours(-24), ct);
            if (count >= budget.MaxItemsPerDay)
                return new BudgetDeferral(
                    $"daily limit: {count}/{budget.MaxItemsPerDay} items started in last 24h",
                    TimeSpan.FromHours(1));
        }

        if (budget.MaxConcurrentForProject > 0)
        {
            var count = await _store.CountInFlightAsync(item.ProjectId, ct);
            if (count >= budget.MaxConcurrentForProject)
                return new BudgetDeferral(
                    $"concurrent limit: {count}/{budget.MaxConcurrentForProject} items in flight",
                    TimeSpan.FromMinutes(1));
        }

        return null;
    }

    private SemaphoreSlim GetBudgetLock(ProjectId projectId) =>
        _budgetLocks.GetOrAdd(projectId.Value, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Fires a background task that re-enqueues <paramref name="id"/> after
    /// <paramref name="delay"/>. Used when the quota router defers a work item
    /// because all subscription-billed members are exhausted. The item remains
    /// in Queued state; the deferred task simply puts it back on the channel so
    /// the next pickup attempt re-probes quota. On shutdown (stoppingToken
    /// cancelled), the delayed task exits cleanly; the item is recovered via
    /// ReplayPendingAsync on the next start.
    /// </summary>
    private void ScheduleDeferredRequeue(WorkItemId id, TimeSpan delay, CancellationToken stoppingToken)
    {
        var count = Interlocked.Increment(ref _pendingDeferrals);
        if (count > DeferralWarningThreshold)
            _log.LogWarning(
                "Deferred requeue backlog is {Count} items; quota exhaustion may be sustained across many work items",
                count);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, stoppingToken);
                _log.LogInformation("Re-enqueueing deferred work item {Id} after quota recheck interval", id);
                await _queue.EnqueueAsync(id, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service is shutting down; item will be recovered on next start.
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to re-enqueue deferred work item {Id}", id);
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
    public TimeSpan MinSpawnInterval { get; init; } = TimeSpan.Zero;

    /// <summary>
    /// Maximum number of times the recovery loop will reset a mid-flight work
    /// item before giving up and transitioning it to
    /// <see cref="WorkItemState.AbandonedAfterRecoveryAttempts"/>. Default 3.
    /// Set to 0 (or any negative value) to disable the cap and recover indefinitely
    /// (not recommended in production — a permanently-stuck item will be re-enqueued
    /// on every orchestrator restart without bound).
    /// </summary>
    public int MaxRecoveryAttempts { get; init; } = 3;

    public AutoRetryOnQuotaFailureOptions AutoRetryOnQuotaFailure { get; init; } = new();

    /// <summary>
    /// Called by the dispatch loop immediately after the spawn timestamp is
    /// written, before <see cref="Task.Run"/>. Used by tests to capture the
    /// true spawn time rather than the thread-pool scheduling time.
    /// </summary>
    internal Action? OnWorkerSpawned { get; init; }

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

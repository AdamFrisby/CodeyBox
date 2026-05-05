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
        IWebhookDispatcher? webhooks = null)
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                            _concurrencyGate.Release();
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
                _concurrencyGate.Release();
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
                    _concurrencyGate.Release();
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

    /// <summary>
    /// On startup, re-enqueue work items that were mid-flight when we last
    /// stopped. Items in non-Queued non-terminal states (Working, Merging,
    /// etc.) are always re-enqueued — they were already past the dependency
    /// gate. Queued items are only re-enqueued if all their dependencies are
    /// currently terminal; those that are still waiting will be enqueued by
    /// <see cref="EnqueueSatisfiedDependentsAsync"/> when their deps complete.
    /// </summary>
    private async Task ReplayPendingAsync(CancellationToken ct)
    {
        // Collect all items once to build the state map for dep checking.
        var allItems = new List<WorkItem>();
        await foreach (var item in _store.ListAsync(ct))
            allItems.Add(item);
        var statesById = WorkItemDependencies.BuildStateMap(allItems);

        var nonTerminalNonQueued = new[]
        {
            WorkItemState.Working, WorkItemState.WorkComplete,
            WorkItemState.Merging, WorkItemState.Merged, WorkItemState.UpstreamPushing,
            WorkItemState.Auditing, WorkItemState.Reworking, WorkItemState.AuditPassed,
        };

        foreach (var item in allItems)
        {
            if (nonTerminalNonQueued.Contains(item.State))
            {
                _log.LogInformation("Recovering work item {Id} (was {State})", item.Id, item.State);
                await _queue.EnqueueAsync(item.Id, ct);
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

    private async Task RunItemAsync(int workerIndex, WorkItemId id, CancellationToken ct)
    {
        var item = await _store.GetAsync(id, ct);
        if (item is null)
        {
            _log.LogWarning("Worker {WorkerId} dequeued unknown work item {Id}", workerIndex, id);
            return;
        }
        if (item.State is WorkItemState.Cancelled or WorkItemState.Done
            or WorkItemState.Failed or WorkItemState.AuditFailed)
        {
            _log.LogInformation("Worker {WorkerId} skipping {Id} in terminal state {State}", workerIndex, id, item.State);
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

        try
        {
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
                    item = item with { Agent = chosen.Agent, ModelId = chosen.ModelId };
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
                        item = item with { StartedAt = DateTimeOffset.UtcNow };
                        await _store.UpdateAsync(item, ct);
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
                    item = item with { StartedAt = DateTimeOffset.UtcNow };
                    await _store.UpdateAsync(item, ct);
                }
            }

            using var registration = _cancellations.Register(item.Id);
            AuditLog.WorkItemPickedUp(workerIndex, item.Id);
            try
            {
                await _pipeline.RunAsync(item, registration.Token);
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
        }

        // After the pipeline finishes (any outcome), check whether any
        // Queued items were waiting on this item and are now unblocked.
        await EnqueueSatisfiedDependentsAsync(id, ct);
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

/// <summary>Snapshot of worker pool state for the /workers/status endpoint.</summary>
public sealed record WorkerPoolStatus(
    int MaxConcurrent,
    int CurrentlyRunning,
    int QueuedCount,
    DateTimeOffset? LastSpawnAt);

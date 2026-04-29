using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Background service that drives a fixed-size worker pool over the task
/// queue. Each worker pulls one work item ID at a time and runs the full
/// pipeline before pulling the next. Concurrency is the number of workers.
/// </summary>
public sealed class OrchestratorService : BackgroundService
{
    private readonly ITaskQueue _queue;
    private readonly IWorkItemStore _store;
    private readonly IPipelineRunner _pipeline;
    private readonly CancellationRegistry _cancellations;
    private readonly OrchestratorOptions _opts;
    private readonly ILogger<OrchestratorService> _log;

    // Tracks work item IDs that are currently being processed by a worker.
    // Guards against double-execution when two workers both enqueue the same
    // item (e.g., both see it as the last satisfied dependent simultaneously).
    private readonly ConcurrentDictionary<WorkItemId, byte> _activeItems = new();

    public OrchestratorService(
        ITaskQueue queue,
        IWorkItemStore store,
        IPipelineRunner pipeline,
        CancellationRegistry cancellations,
        OrchestratorOptions opts,
        ILogger<OrchestratorService> log)
    {
        _queue = queue;
        _store = store;
        _pipeline = pipeline;
        _cancellations = cancellations;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReplayPendingAsync(stoppingToken);

        var workers = Enumerable.Range(0, _opts.Concurrency)
            .Select(i => RunWorkerAsync(i, stoppingToken))
            .ToArray();
        await Task.WhenAll(workers);
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

    private async Task RunWorkerAsync(int workerId, CancellationToken ct)
    {
        _log.LogInformation("Worker {WorkerId} started", workerId);
        while (!ct.IsCancellationRequested)
        {
            WorkItemId? id;
            try { id = await _queue.DequeueAsync(ct); }
            catch (OperationCanceledException) { break; }

            if (id is null) break; // queue closed

            var item = await _store.GetAsync(id.Value, ct);
            if (item is null)
            {
                _log.LogWarning("Worker {WorkerId} dequeued unknown work item {Id}", workerId, id);
                continue;
            }
            if (item.State is WorkItemState.Cancelled or WorkItemState.Done
                or WorkItemState.Failed or WorkItemState.AuditFailed)
            {
                _log.LogInformation("Worker {WorkerId} skipping {Id} in terminal state {State}", workerId, id, item.State);
                continue;
            }

            // Double-enqueue guard: when two workers simultaneously complete
            // the last dependency of the same downstream item, both may enqueue
            // it. Only one worker should run the pipeline for a given item at a
            // time. TryAdd is atomic; the losing worker skips gracefully.
            if (!_activeItems.TryAdd(id.Value, 0))
            {
                _log.LogInformation(
                    "Worker {WorkerId} skipping {Id}: already being processed by another worker", workerId, id);
                continue;
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
                            "Worker {WorkerId} skipping {Id}: dependencies not yet terminal", workerId, id);
                        continue; // finally removes from _activeItems
                    }
                }

                using var registration = _cancellations.Register(item.Id);
                AuditLog.WorkItemPickedUp(workerId, item.Id);
                try
                {
                    await _pipeline.RunAsync(item, registration.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break; // finally removes from _activeItems, then exits loop
                }
                catch (OperationCanceledException)
                {
                    _log.LogInformation("Worker {WorkerId} item {Id} cancelled", workerId, id);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Worker {WorkerId} unexpected failure on {Id}", workerId, id);
                }
            }
            finally
            {
                _activeItems.TryRemove(id.Value, out _);
            }

            // After the pipeline finishes (any outcome), check whether any
            // Queued items were waiting on this item and are now unblocked.
            await EnqueueSatisfiedDependentsAsync(id.Value, ct);
        }
        _log.LogInformation("Worker {WorkerId} stopped", workerId);
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

public sealed record OrchestratorOptions
{
    public int Concurrency { get; init; } = 2;
}

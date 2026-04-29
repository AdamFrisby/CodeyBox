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
    private readonly PipelineRunner _pipeline;
    private readonly CancellationRegistry _cancellations;
    private readonly OrchestratorOptions _opts;
    private readonly ILogger<OrchestratorService> _log;

    public OrchestratorService(
        ITaskQueue queue,
        IWorkItemStore store,
        PipelineRunner pipeline,
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
    /// On startup, re-enqueue any work item that was mid-flight when we last
    /// stopped. The pipeline is idempotent at the phase boundaries so re-runs
    /// of partially-completed items resume cleanly (work-phase commits land
    /// on the same branch; merge phase is a fast-forward if it already ran).
    /// </summary>
    private async Task ReplayPendingAsync(CancellationToken ct)
    {
        var nonTerminal = new[]
        {
            WorkItemState.Queued, WorkItemState.Working, WorkItemState.WorkComplete,
            WorkItemState.Merging, WorkItemState.Merged, WorkItemState.UpstreamPushing,
        };
        foreach (var state in nonTerminal)
        {
            await foreach (var item in _store.ListByStateAsync(state, ct))
            {
                _log.LogInformation("Recovering work item {Id} (was {State})", item.Id, item.State);
                await _queue.EnqueueAsync(item.Id, ct);
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
            if (item.State is WorkItemState.Cancelled or WorkItemState.Done or WorkItemState.Failed)
            {
                _log.LogInformation("Worker {WorkerId} skipping {Id} in terminal state {State}", workerId, id, item.State);
                continue;
            }

            using var registration = _cancellations.Register(item.Id);
            AuditLog.WorkItemPickedUp(workerId, item.Id);
            try
            {
                await _pipeline.RunAsync(item, registration.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
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
        _log.LogInformation("Worker {WorkerId} stopped", workerId);
    }
}

public sealed record OrchestratorOptions
{
    public int Concurrency { get; init; } = 2;
}

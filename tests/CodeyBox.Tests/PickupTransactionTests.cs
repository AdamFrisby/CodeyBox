using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the orchestrator registers a worker in the registry
/// (with <c>current_work_item_id</c> set) when it picks up a work item.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PickupTransactionTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-pickup-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;

    public PickupTransactionTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task Pickup_RegistersWorkerWithCurrentWorkItemId()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        await queue.EnqueueAsync(item.Id);

        // Pipeline blocks until the CancellationToken fires; gives us time to
        // observe the registry row before the worker finishes.
        var holdSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new HoldingPipeline(_store, holdSignal.Task);

        var deadWorkerOpts = new DeadWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        };
        var reaper = new DeadWorkerReaper(
            _registry, _store, queue, deadWorkerOpts,
            NullLogger<DeadWorkerReaper>.Instance);

        var cancellations = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        using var svc = new OrchestratorService(
            queue, _store, pipeline, cancellations, opts,
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: _registry,
            deadWorkerOpts: deadWorkerOpts,
            reaper: reaper);

        await svc.StartAsync(CancellationToken.None);

        // Poll until the worker has registered itself in the registry.
        IReadOnlyList<WorkerRegistration> workers = [];
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            workers = await _registry.ListAsync();
            if (workers.Count > 0 && workers[0].CurrentWorkItemId == item.Id.ToString())
                break;
            await Task.Delay(20);
        }

        holdSignal.SetResult(); // Let the pipeline finish.
        await svc.StopAsync(CancellationToken.None);

        Assert.NotEmpty(workers);
        Assert.Equal(item.Id.ToString(), workers[0].CurrentWorkItemId);
        Assert.Equal(Environment.MachineName, workers[0].HostName);
    }
}

/// <summary>
/// Pipeline that marks the item Working, then waits on a task before finishing.
/// </summary>
internal sealed class HoldingPipeline : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly Task _holdUntil;

    public HoldingPipeline(IWorkItemStore store, Task holdUntil)
    {
        _store = store;
        _holdUntil = holdUntil;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        await _store.UpdateAsync(item.With(WorkItemState.Working), ct);
        await _holdUntil.WaitAsync(ct).ConfigureAwait(false);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that a paused IQueueController prevents new work-item pickup
/// while leaving in-flight items unaffected.
/// </summary>
public sealed class PausePickupTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-pausepickup-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public PausePickupTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(string projectId = "test") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    [Fact]
    public async Task PausedQueue_DoesNotPickUpNewItems()
    {
        // Create a controller pre-paused so no items are picked up.
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        await controller.PauseAsync("hold-for-test");

        var pickupCount = 0;
        var pipeline = new CountingPipelineRunner(_store, onRun: () => Interlocked.Increment(ref pickupCount));
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        var item = MakeItem();
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        using var cts = new CancellationTokenSource();
        await svc.StartAsync(CancellationToken.None);

        // Give the dispatch loop a generous window; it should spin on the pause gate.
        await Task.Delay(300);

        Assert.Equal(0, pickupCount);
        var stored = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        // Now resume and allow pickup.
        await controller.ResumeAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && pickupCount == 0)
            await Task.Delay(50);

        cts.Cancel();
        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(1, pickupCount);
    }

    [Fact]
    public async Task RunningQueue_PicksUpItems()
    {
        // Sanity: with a Running controller, items are processed.
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);

        var pickupCount = 0;
        var pipeline = new CountingPipelineRunner(_store, onRun: () => Interlocked.Increment(ref pickupCount));
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 2 };
        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, reg, opts,
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        for (var i = 0; i < 3; i++)
        {
            var item = MakeItem();
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);
        }

        await svc.StartAsync(CancellationToken.None);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref pickupCount) < 3)
            await Task.Delay(50);

        await svc.StopAsync(CancellationToken.None);
        Assert.Equal(3, pickupCount);
    }
}

/// <summary>
/// Pipeline stub that increments a counter each time RunAsync is called
/// and immediately transitions the item to Done.
/// </summary>
internal sealed class CountingPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly Action _onRun;

    public CountingPipelineRunner(IWorkItemStore store, Action onRun)
    {
        _store = store;
        _onRun = onRun;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        _onRun();
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}

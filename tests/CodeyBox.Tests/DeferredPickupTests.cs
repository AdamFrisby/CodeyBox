using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the _deferredItems contract introduced by the priority pickup:
///   (a) PickNextEligibleAsync skips items currently in _deferredItems even
///       though their store state is still Queued.
///   (b) A queued kick does not clear the deferred mark; the deferral owner
///       clears it when its recheck interval actually elapses.
/// </summary>
public sealed class DeferredPickupTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-deferred-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public DeferredPickupTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private OrchestratorService BuildOrchestrator(
        ITaskQueue queue,
        IPipelineRunner pipeline,
        IProjectRepository? projects = null)
        => new(
            queue, _store, pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            projects: projects);

    private static WorkItem QueuedItem(string projectId, int priority = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId(projectId),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        Priority = priority,
    };

    [Fact]
    public async Task PickNextEligible_SkipsDeferredItems()
    {
        // Two Queued items: one marked deferred, one not. Pickup must return the
        // non-deferred one regardless of insertion order or priority equality.
        var deferred = QueuedItem("p", priority: 100); // higher priority
        var ready = QueuedItem("p", priority: 0);
        await _store.CreateAsync(deferred);
        await _store.CreateAsync(ready);

        var queue = new InMemoryTaskQueue();
        var svc = BuildOrchestrator(queue, new NoopPipelineRunner());

        svc.MarkDeferredForTest(deferred.Id);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);
        Assert.Equal(ready.Id, picked);
    }

    [Fact]
    public async Task PickNextEligible_ReturnsNull_WhenOnlyCandidateIsDeferred()
    {
        // Sole Queued item is deferred → pickup query must yield nothing.
        var deferred = QueuedItem("p");
        await _store.CreateAsync(deferred);

        var queue = new InMemoryTaskQueue();
        var svc = BuildOrchestrator(queue, new NoopPipelineRunner());

        svc.MarkDeferredForTest(deferred.Id);

        var picked = await svc.PickNextEligibleForTestAsync(CancellationToken.None);
        Assert.Null(picked);
    }

    [Fact]
    public async Task StaleKick_DoesNotClearDeferral_OrPickUpBeforeRecheck()
    {
        // Mark an item deferred (simulating ScheduleDeferredRequeue's pre-sleep
        // mark), then send an older queued kick for the same item. The dispatch
        // loop must not treat that buffered kick as a fresh retry-now signal,
        // or quota/budget/disk deferrals can be bypassed immediately.
        var item = QueuedItem("p");
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var pickupCount = 0;
        var pipeline = new CountingPipelineRunner(
            _store, onRun: () => Interlocked.Increment(ref pickupCount));
        var svc = BuildOrchestrator(queue, pipeline);

        svc.MarkDeferredForTest(item.Id);
        Assert.True(svc.IsDeferredForTest(item.Id));

        await svc.StartAsync(CancellationToken.None);

        // Send a stale kick; the item must stay deferred until the deferral
        // owner clears the marker.
        await queue.EnqueueAsync(item.Id);

        await Task.Delay(250);

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(0, Volatile.Read(ref pickupCount));
        Assert.True(svc.IsDeferredForTest(item.Id));
    }

    private sealed class NoopPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
            => Task.CompletedTask;
    }
}

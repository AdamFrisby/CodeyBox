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
    private static readonly TimeSpan FullSuiteSchedulingTimeout = TimeSpan.FromSeconds(30);

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

        var pipeline = new CountingPipelineRunner(_store, signalAtCount: 1);
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

        await svc.StartAsync(CancellationToken.None);

        // Give the dispatch loop a generous window; it should spin on the pause
        // gate. (A negative assertion — the pause gate blocks pickup regardless
        // of scheduling latency, so a slow loop only reinforces this, never
        // breaks it.)
        await Task.Delay(300);

        Assert.Equal(0, pipeline.RunCount);
        var stored = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        // Now resume and allow pickup — await the pickup via the event signal
        // rather than polling a wall-clock deadline that races ThreadPool
        // starvation under the capped full-suite load.
        await controller.ResumeAsync();
        await pipeline.ReachedTargetCount.WaitAsync(FullSuiteSchedulingTimeout);

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(1, pipeline.RunCount);
    }

    [Fact]
    public async Task RunningQueue_PicksUpItems()
    {
        // Sanity: with a Running controller, items are processed.
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);

        var pipeline = new CountingPipelineRunner(_store, signalAtCount: 3);
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
        // Await all three pickups via the event signal rather than polling a
        // wall-clock deadline that races ThreadPool starvation under the capped
        // full-suite load.
        await pipeline.ReachedTargetCount.WaitAsync(FullSuiteSchedulingTimeout);

        await svc.StopAsync(CancellationToken.None);
        Assert.Equal(3, pipeline.RunCount);
    }

    [Fact]
    public async Task PausedQueue_DoesNotCancelInFlightItems()
    {
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);

        var itemStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var itemCanProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var itemCompletedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var pipeline = new BlockingPipelineRunner(
            _store,
            onStart: () => itemStarted.TrySetResult(),
            proceedGate: itemCanProceed.Task,
            onComplete: () => itemCompletedTcs.TrySetResult());

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

        await svc.StartAsync(CancellationToken.None);

        // Wait until the item is in-flight. Under capped full-suite load the
        // dispatcher can experience the same ThreadPool starvation as the
        // other event-driven waits in this class.
        await itemStarted.Task.WaitAsync(FullSuiteSchedulingTimeout);

        // Pause while the item is running — must not cancel it.
        await controller.PauseAsync("in-flight continuity test");
        Assert.Equal(QueueState.Paused, controller.State);

        // Allow the in-flight item to finish.
        itemCanProceed.TrySetResult();

        // Await the completion signal rather than polling a wall-clock
        // deadline: the dispatcher/worker run on the ThreadPool, which the
        // capped full-suite suite can starve, so a polling deadline races the
        // very starvation it is waiting out. The completion signal fires only
        // after the worker has committed the Done state (see
        // BlockingPipelineRunner), so observing it guarantees Done is durable.
        await itemCompletedTcs.Task.WaitAsync(FullSuiteSchedulingTimeout);

        await svc.StopAsync(CancellationToken.None);

        Assert.True(itemCompletedTcs.Task.IsCompletedSuccessfully);
        var stored = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, stored!.State);
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
    private int _runCount;
    private readonly int _signalAtCount;
    private readonly TaskCompletionSource _reachedTarget =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CountingPipelineRunner(IWorkItemStore store, Action? onRun = null, int signalAtCount = int.MaxValue)
    {
        _store = store;
        _onRun = onRun ?? (static () => { });
        _signalAtCount = signalAtCount;
    }

    public int RunCount => Volatile.Read(ref _runCount);

    /// <summary>
    /// Completes once RunAsync has been invoked <c>signalAtCount</c> times.
    /// Event-driven so the waiting test does not poll a wall-clock deadline
    /// that competes for CPU with the very dispatcher it is waiting on.
    /// </summary>
    public Task ReachedTargetCount => _reachedTarget.Task;

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        _onRun();
        // Commit Done before signalling so the count is observed only after the
        // store transition is durable.
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
        if (Interlocked.Increment(ref _runCount) >= _signalAtCount)
            _reachedTarget.TrySetResult();
    }
}

/// <summary>
/// Pipeline stub that signals when it starts, waits for a gate, then completes.
/// Used to test that in-flight items finish normally when the queue is paused.
/// </summary>
internal sealed class BlockingPipelineRunner : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly Action _onStart;
    private readonly Task _proceedGate;
    private readonly Action _onComplete;

    public BlockingPipelineRunner(IWorkItemStore store, Action onStart, Task proceedGate, Action onComplete)
    {
        _store = store;
        _onStart = onStart;
        _proceedGate = proceedGate;
        _onComplete = onComplete;
    }

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        _onStart();
        await _proceedGate;
        // Commit the Done state BEFORE signalling completion. The test awaits
        // the completion signal and then asserts the item reached Done;
        // signalling first leaves a window where the test reads the store
        // before the Done write has committed (and StopAsync may begin
        // draining), so under capped full-suite load the Done assertion could
        // observe a not-yet-persisted state. Ordering the write first makes
        // "completion observed" a true happens-after of the durable Done state.
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
        _onComplete();
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkerPoolSlotReleaseWakeTests : IDisposable
{
    private static readonly TimeSpan DispatchWaitTimeout = TimeSpan.FromSeconds(10);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-slot-release-wake-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkerPoolSlotReleaseWakeTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task SlotReleaseWake_RefillsPoolFromIndependentReadyBacklogWithoutExternalKick()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var first = MakeItem(createdAt: now);
        var second = MakeItem(createdAt: now.AddMilliseconds(1));
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(2));
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await _store.CreateAsync(readyBacklog);

        await queue.EnqueueAsync(first.Id);
        await queue.EnqueueAsync(second.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(first.Id, DispatchWaitTimeout));
        Assert.True(await pipeline.WaitForEnteredAsync(second.Id, DispatchWaitTimeout));
        Assert.False(pipeline.HasEntered(readyBacklog.Id));

        pipeline.Release(first.Id);

        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "Releasing a worker slot should wake the dispatcher to rescan independent ready backlog rows.");
        Assert.Equal(0, queue.EnqueueCount(readyBacklog.Id));

        pipeline.Release(second.Id);
        pipeline.Release(readyBacklog.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SlotReleaseWake_DoesNotClearDeferredBacklogItem()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var deferred = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(deferred);
        svc.MarkDeferredForTest(deferred.Id);

        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));

        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The slot-release wake should be consumed as a generic rescan.");

        await Task.Delay(500);

        Assert.True(
            svc.IsDeferredForTest(deferred.Id),
            "A generic slot-release wake must not clear deferred items as retry-now signals.");
        Assert.False(pipeline.HasEntered(deferred.Id));

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SlotReleaseWake_DoesNotDispatchWhileQueuePaused()
    {
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));

        await controller.PauseAsync("slot release wake suppression test");
        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await WaitUntilAsync(() => queue.TotalEnqueueCount >= 2, DispatchWaitTimeout),
            "The slot-release wake should be enqueued even when the queue is paused.");

        await Task.Delay(500);

        Assert.False(pipeline.HasEntered(readyBacklog.Id));
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StopAsync(stopCts.Token);
    }

    [Fact]
    public async Task SlotReleaseWake_DoesNotDispatchAfterShutdownPause()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await queue.WaitForDequeueCallsAsync(2, DispatchWaitTimeout),
            "The dispatch loop should be blocked on the next queue wake before shutdown is paused.");

        queue.DropDefaultEnqueues = true;
        svc.PauseDispatch();
        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await WaitUntilAsync(() => queue.TotalEnqueueCount >= 3, DispatchWaitTimeout),
            "The slot-release wake should be enqueued even after shutdown dispatch is paused.");
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The slot-release wake should be delivered to the loop and suppressed by IsDispatchPaused.");

        await Task.Delay(500);

        Assert.False(pipeline.HasEntered(readyBacklog.Id));
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await svc.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(EnqueueFailureMode.ThrowSynchronously, "threw synchronously")]
    [InlineData(EnqueueFailureMode.FaultAsynchronously, "faulted")]
    public async Task SlotReleaseWake_EnqueueFailure_DoesNotFaultWorkerOrHostedService(
        EnqueueFailureMode failureMode,
        string expectedLogText)
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        var logger = new CapturingLogger<OrchestratorService>();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            logger);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var running = MakeItem(DateTimeOffset.UtcNow);
        await _store.CreateAsync(running);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));

        queue.FailureMode = failureMode;
        pipeline.Release(running.Id);

        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await WaitUntilAsync(
                () => logger.Entries.Any(e =>
                    e.Message.Contains("slot-release wake-up kick", StringComparison.Ordinal)
                    && e.Message.Contains(expectedLogText, StringComparison.Ordinal)),
                DispatchWaitTimeout),
            "A slot-release enqueue failure should be caught and logged without faulting the worker task.");

        await svc.StopAsync(CancellationToken.None);
    }

    private static WorkItem MakeItem(DateTimeOffset createdAt) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate()) return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    private sealed class ObservedTaskQueue : ITaskQueue
    {
        private readonly InMemoryTaskQueue _inner = new();
        private readonly ConcurrentQueue<WorkItemId> _enqueued = new();
        private readonly ConcurrentQueue<WorkItemId> _dequeued = new();
        private readonly TaskCompletionSource _firstDequeue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dequeueCalls;

        public bool DropDefaultEnqueues { get; set; }
        public EnqueueFailureMode FailureMode { get; set; } = EnqueueFailureMode.None;

        public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        {
            _enqueued.Enqueue(id);
            if (DropDefaultEnqueues && id == default)
                return ValueTask.CompletedTask;

            return FailureMode switch
            {
                EnqueueFailureMode.ThrowSynchronously =>
                    throw new InvalidOperationException("synthetic synchronous enqueue failure"),
                EnqueueFailureMode.FaultAsynchronously =>
                    new ValueTask(Task.FromException(new InvalidOperationException("synthetic asynchronous enqueue failure"))),
                _ => _inner.EnqueueAsync(id, ct),
            };
        }

        public int Count => _inner.Count;
        public int TotalEnqueueCount => _enqueued.Count;
        public int CompletedDequeueCount => _dequeued.Count;
        public int DequeueCallCount => Volatile.Read(ref _dequeueCalls);

        public int EnqueueCount(WorkItemId id)
        {
            var count = 0;
            foreach (var enqueued in _enqueued)
                if (enqueued == id) count++;
            return count;
        }

        public async ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _dequeueCalls);
            _firstDequeue.TrySetResult();
            var id = await _inner.DequeueAsync(ct);
            if (id is { } actual)
                _dequeued.Enqueue(actual);
            return id;
        }

        public Task WaitForFirstDequeueAsync(TimeSpan timeout) =>
            _firstDequeue.Task.WaitAsync(timeout);

        public Task<bool> WaitForDequeueCallsAsync(int count, TimeSpan timeout) =>
            WaitUntilAsync(() => DequeueCallCount >= count, timeout);

        public Task<bool> WaitForCompletedDequeuesAsync(int count, TimeSpan timeout) =>
            WaitUntilAsync(() => CompletedDequeueCount >= count, timeout);
    }

    public enum EnqueueFailureMode
    {
        None,
        ThrowSynchronously,
        FaultAsynchronously,
    }

    private sealed class ReleaseControlledPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _released = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _done = new();

        public ReleaseControlledPipeline(IWorkItemStore store) => _store = store;

        public bool HasEntered(WorkItemId id) => _entered.ContainsKey(id);

        public void Release(WorkItemId id) =>
            _released.GetOrAdd(id, static _ => NewSignal()).TrySetResult();

        public Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_entered.GetOrAdd(id, static _ => NewSignal()), timeout);

        public Task<bool> WaitForDoneAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_done.GetOrAdd(id, static _ => NewSignal()), timeout);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entered.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
            await _released.GetOrAdd(item.Id, static _ => NewSignal()).Task.WaitAsync(ct);
            await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
            _done.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task<bool> WaitForSignalAsync(TaskCompletionSource signal, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout));
            return completed == signal.Task;
        }
    }
}

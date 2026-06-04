using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkerPoolSlotReleaseWakeTests : IDisposable
{
    private static readonly TimeSpan DispatchWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoDispatchQuietPeriod = TimeSpan.FromMilliseconds(500);

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
    public async Task SlotReleaseWake_RefillsAllOpenSlotsFromIndependentReadyBacklogWithoutExternalKick()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = new[]
        {
            MakeItem(createdAt: now.AddMilliseconds(1)),
            MakeItem(createdAt: now.AddMilliseconds(2)),
            MakeItem(createdAt: now.AddMilliseconds(3)),
        };

        await _store.CreateAsync(running);
        foreach (var item in readyBacklog)
            await _store.CreateAsync(item);

        await queue.EnqueueAsync(running.Id);
        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));

        foreach (var item in readyBacklog)
            Assert.False(pipeline.HasEntered(item.Id));

        pipeline.Release(running.Id);

        foreach (var item in readyBacklog)
        {
            Assert.True(
                await pipeline.WaitForEnteredAsync(item.Id, DispatchWaitTimeout),
                "One slot-release wake should keep refilling while free slots and ready backlog remain.");
            Assert.Equal(0, queue.EnqueueCount(item.Id));
        }

        foreach (var item in readyBacklog)
            pipeline.Release(item.Id);
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

        Assert.True(
            svc.IsDeferredForTest(deferred.Id),
            "A generic slot-release wake must not clear deferred items as retry-now signals.");
        Assert.False(
            await pipeline.WaitForEnteredAsync(deferred.Id, NoDispatchQuietPeriod),
            "The deferred item should remain quiet long enough to prove the generic wake was not treated as retry-now.");

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SlotReleaseWake_DoesNotCollapseCompletedItemDeferral()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var capRetryDelay = TimeSpan.FromMilliseconds(750);
        var concurrency = new AgentConcurrencyOptions
        {
            Members = { ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        };
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: concurrency,
            quotaRouterOptions: new QuotaRouterOptions { CapRetryRecheckInterval = capRetryDelay });

        Assert.True(svc.TryReserveAgentSlotForTest(AgentKind.Codex));

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var item = MakeItem(DateTimeOffset.UtcNow) with { Agent = AgentKind.Codex };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        Assert.True(
            await WaitUntilAsync(() => svc.IsDeferredForTest(item.Id), DispatchWaitTimeout),
            "The item itself should enter the cap deferral path.");
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The slot-release wake should be consumed as a generic rescan after the deferring worker exits.");

        Assert.Equal(1, queue.EnqueueCount(item.Id));
        Assert.False(pipeline.HasEntered(item.Id));

        Assert.False(
            await WaitUntilAsync(() => queue.EnqueueCount(item.Id) > 1, TimeSpan.FromMilliseconds(300)),
            "The generic slot-release wake must not clear the completed item's deferral or enqueue an immediate retry.");
        Assert.True(svc.IsDeferredForTest(item.Id));

        Assert.True(
            await WaitUntilAsync(() => queue.EnqueueCount(item.Id) > 1, TimeSpan.FromSeconds(2)),
            "The item-specific retry should occur only when the configured cap deferral interval fires.");

        svc.ReleaseAgentSlotForTest(AgentKind.Codex);
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

        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "The paused queue should hold the slot-release wake without dispatching during the quiet period.");
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await controller.ResumeAsync();
        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "The paused branch should preserve the slot-release wake so resume picks up the ready backlog.");

        pipeline.Release(readyBacklog.Id);
        Assert.True(await pipeline.WaitForDoneAsync(readyBacklog.Id, DispatchWaitTimeout));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await svc.StopAsync(stopCts.Token);
    }

    [Fact]
    public async Task QueuePauseSuppressesBufferedKickWaitingForReleasedSlot()
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

        await queue.EnqueueAsync(readyBacklog.Id);
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The ready item kick should be consumed while the dispatcher is blocked on the full pool.");

        await controller.PauseAsync("pause while buffered kick waits for a worker slot");
        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));

        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "A queued pause must suppress a buffered kick that unblocks after a worker slot is released.");
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await controller.ResumeAsync();
        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "The suppressed buffered kick should be preserved so resume can pick up the ready backlog.");

        pipeline.Release(readyBacklog.Id);
        Assert.True(await pipeline.WaitForDoneAsync(readyBacklog.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueuePauseAfterPickupDuringSpawnPacing_UnreservesAndPreservesWake()
    {
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        WorkItemId? pauseOnReserve = null;
        var pauseApplied = false;
        var reservedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = TimeSpan.FromSeconds(2),
                OnWorkerReservedForTest = id =>
                {
                    if (id != pauseOnReserve || pauseApplied)
                        return Task.CompletedTask;

                    pauseApplied = true;
                    reservedSecond.TrySetResult();
                    return controller.PauseAsync("pause after pickup during spawn pacing");
                },
            },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var first = MakeItem(createdAt: now);
        var second = MakeItem(createdAt: now.AddMilliseconds(1));
        pauseOnReserve = second.Id;
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await queue.EnqueueAsync(first.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(first.Id, DispatchWaitTimeout));
        pipeline.Release(first.Id);
        Assert.True(await pipeline.WaitForDoneAsync(first.Id, DispatchWaitTimeout));

        await reservedSecond.Task.WaitAsync(DispatchWaitTimeout);

        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(second.Id), DispatchWaitTimeout),
            "The queue-pause branch after spawn pacing must unreserve the item and release the gate.");
        Assert.False(pipeline.HasEntered(second.Id));

        await controller.ResumeAsync();
        Assert.True(
            await pipeline.WaitForEnteredAsync(second.Id, DispatchWaitTimeout),
            "The post-pickup queue-pause branch should preserve a wake so resume dispatches the item.");

        pipeline.Release(second.Id);
        Assert.True(await pipeline.WaitForDoneAsync(second.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ShutdownPauseAfterPickupDuringSpawnPacing_UnreservesAndStopsDispatch()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        WorkItemId? pauseOnReserve = null;
        var pauseApplied = false;
        var reservedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        OrchestratorService? svcRef = null;
        using var svc = svcRef = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = TimeSpan.FromSeconds(2),
                OnWorkerReservedForTest = id =>
                {
                    if (id == pauseOnReserve && !pauseApplied)
                    {
                        pauseApplied = true;
                        reservedSecond.TrySetResult();
                        svcRef!.PauseDispatch();
                    }
                    return Task.CompletedTask;
                },
            },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var first = MakeItem(createdAt: now);
        var second = MakeItem(createdAt: now.AddMilliseconds(1));
        pauseOnReserve = second.Id;
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await queue.EnqueueAsync(first.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(first.Id, DispatchWaitTimeout));
        pipeline.Release(first.Id);
        Assert.True(await pipeline.WaitForDoneAsync(first.Id, DispatchWaitTimeout));

        await reservedSecond.Task.WaitAsync(DispatchWaitTimeout);

        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(second.Id), TimeSpan.FromSeconds(4)),
            "The shutdown-pause branch after spawn pacing must unreserve the item and release the gate.");
        Assert.False(
            await pipeline.WaitForEnteredAsync(second.Id, NoDispatchQuietPeriod),
            "Shutdown dispatch pause must suppress the reserved item after it is unreserved.");

        var stored = await _store.GetAsync(second.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await svc.StopAsync(CancellationToken.None);
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

        queue.DropDispatchWakeEnqueueCount = 1;
        svc.PauseDispatch();
        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await WaitUntilAsync(() => queue.TotalEnqueueCount >= 3, DispatchWaitTimeout),
            "The slot-release wake should be enqueued even after shutdown dispatch is paused.");
        Assert.True(
            await queue.WaitForCompletedDequeuesAsync(2, DispatchWaitTimeout),
            "The slot-release wake should be delivered to the loop and suppressed by IsDispatchPaused.");

        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "The shutdown dispatch gate should suppress the delivered slot-release wake during the quiet period.");
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await svc.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(EnqueueFailureMode.ThrowSynchronously)]
    [InlineData(EnqueueFailureMode.FaultAsynchronously)]
    public async Task SlotReleaseWake_EnqueueFailure_RetriesUntilWakeIsDelivered(
        EnqueueFailureMode failureMode)
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

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(now);
        var readyBacklog = MakeItem(now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));

        queue.FailureMode = failureMode;
        pipeline.Release(running.Id);

        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        Assert.True(
            await WaitUntilAsync(
                () => logger.Entries.Any(e =>
                    e.Level == LogLevel.Error
                    && e.Exception is InvalidOperationException
                    && e.Message.Contains("required slot-release wake-up kick failed", StringComparison.Ordinal)),
                DispatchWaitTimeout),
            "A slot-release enqueue failure should be logged as an invariant failure before retrying.");
        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "The ready backlog should remain parked while the wake enqueue keeps failing.");

        queue.FailureMode = EnqueueFailureMode.None;
        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "The retry loop should deliver the slot-release wake once the queue accepts writes again.");

        pipeline.Release(readyBacklog.Id);
        Assert.True(await pipeline.WaitForDoneAsync(readyBacklog.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecoveredSlotRelease_CanSuppressWakeBeforeRecoveryStateTransition()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var workerRegistry = new SqliteWorkerRegistry(_dbPath, NullLogger<SqliteWorkerRegistry>.Instance);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: workerRegistry,
            deadWorkerOpts: new DeadWorkerOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));
        var worker = await WaitForWorkerRegistrationAsync(workerRegistry, running.Id, DispatchWaitTimeout);
        Assert.NotNull(worker);
        await _store.UpdateAsync(running with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        Assert.True(await svc.TryReleaseRecoveredWorkerSlotAsync(
            worker!.WorkerId,
            running.Id,
            "test recovery release while durable row is still worker-owned"));

        Assert.Equal(0, queue.GenericWakeEnqueueCount);
        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "Recovery release must not emit a generic wake before the recovery path updates or parks the item.");

        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RecoveredSlotRelease_WakesDispatcherAfterRecoveryStateTransition()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var workerRegistry = new SqliteWorkerRegistry(_dbPath, NullLogger<SqliteWorkerRegistry>.Instance);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: workerRegistry,
            deadWorkerOpts: new DeadWorkerOptions());

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var running = MakeItem(createdAt: now);
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, DispatchWaitTimeout));
        var worker = await WaitForWorkerRegistrationAsync(workerRegistry, running.Id, DispatchWaitTimeout);
        Assert.NotNull(worker);

        var failed = running.With(WorkItemState.Failed, "test recovery transition");
        await _store.UpdateAsync(failed);

        Assert.True(await svc.TryReleaseRecoveredWorkerSlotAsync(
            worker!.WorkerId,
            running.Id,
            "test recovery release after durable transition"));

        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, DispatchWaitTimeout),
            "A recovered slot release with a safe durable state should wake the dispatcher for unrelated ready work.");

        pipeline.Release(running.Id);
        pipeline.Release(readyBacklog.Id);
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

    private static async Task<WorkerRegistration?> WaitForWorkerRegistrationAsync(
        IWorkerRegistry registry,
        WorkItemId workItemId,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var worker = (await registry.ListAsync())
                .FirstOrDefault(w => w.CurrentWorkItemId == workItemId.ToString());
            if (worker is not null)
                return worker;

            await Task.Delay(25);
        }

        return (await registry.ListAsync())
            .FirstOrDefault(w => w.CurrentWorkItemId == workItemId.ToString());
    }

    private sealed class ObservedTaskQueue : ITaskQueue
    {
        private readonly Channel<ObservedDispatch> _channel = Channel.CreateUnbounded<ObservedDispatch>();
        private readonly ConcurrentQueue<ObservedDispatch> _enqueued = new();
        private readonly ConcurrentQueue<ObservedDispatch> _dequeued = new();
        private readonly TaskCompletionSource _firstDequeue =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _dequeueCalls;
        private int _dropDispatchWakeEnqueueCount;

        public int DropDispatchWakeEnqueueCount
        {
            get => Volatile.Read(ref _dropDispatchWakeEnqueueCount);
            set => Volatile.Write(ref _dropDispatchWakeEnqueueCount, value);
        }
        public EnqueueFailureMode FailureMode { get; set; } = EnqueueFailureMode.None;

        public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        {
            var dispatch = ObservedDispatch.ForWorkItem(id);
            _enqueued.Enqueue(dispatch);

            return FailureMode switch
            {
                EnqueueFailureMode.ThrowSynchronously =>
                    throw new InvalidOperationException("synthetic synchronous enqueue failure"),
                EnqueueFailureMode.FaultAsynchronously =>
                    new ValueTask(Task.FromException(new InvalidOperationException("synthetic asynchronous enqueue failure"))),
                _ => _channel.Writer.WriteAsync(dispatch, ct),
            };
        }

        public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default)
        {
            var dispatch = ObservedDispatch.GenericWake;
            _enqueued.Enqueue(dispatch);
            while (true)
            {
                var remaining = Volatile.Read(ref _dropDispatchWakeEnqueueCount);
                if (remaining <= 0) break;
                if (Interlocked.CompareExchange(ref _dropDispatchWakeEnqueueCount, remaining - 1, remaining) == remaining)
                    return ValueTask.CompletedTask;
            }

            return FailureMode switch
            {
                EnqueueFailureMode.ThrowSynchronously =>
                    throw new InvalidOperationException("synthetic synchronous enqueue failure"),
                EnqueueFailureMode.FaultAsynchronously =>
                    new ValueTask(Task.FromException(new InvalidOperationException("synthetic asynchronous enqueue failure"))),
                _ => _channel.Writer.WriteAsync(dispatch, ct),
            };
        }

        public int Count => _channel.Reader.Count;
        public int TotalEnqueueCount => _enqueued.Count;
        public int CompletedDequeueCount => _dequeued.Count;
        public int DequeueCallCount => Volatile.Read(ref _dequeueCalls);
        public int GenericWakeEnqueueCount => _enqueued.Count(static d => d.IsGenericWake);

        public int EnqueueCount(WorkItemId id)
        {
            var count = 0;
            foreach (var enqueued in _enqueued)
                if (enqueued.WorkItemId == id) count++;
            return count;
        }

        public async ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
        {
            try
            {
                var dispatch = await ReadObservedDispatchAsync(ct);
                return dispatch.WorkItemId;
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public async ValueTask<bool> DequeueDispatchSignalAsync(CancellationToken ct = default)
        {
            try
            {
                await ReadObservedDispatchAsync(ct);
                return true;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        public Task WaitForFirstDequeueAsync(TimeSpan timeout) =>
            _firstDequeue.Task.WaitAsync(timeout);

        public Task<bool> WaitForDequeueCallsAsync(int count, TimeSpan timeout) =>
            WaitUntilAsync(() => DequeueCallCount >= count, timeout);

        public Task<bool> WaitForCompletedDequeuesAsync(int count, TimeSpan timeout) =>
            WaitUntilAsync(() => CompletedDequeueCount >= count, timeout);

        private async ValueTask<ObservedDispatch> ReadObservedDispatchAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _dequeueCalls);
            _firstDequeue.TrySetResult();
            var dispatch = await _channel.Reader.ReadAsync(ct);
            _dequeued.Enqueue(dispatch);
            return dispatch;
        }

        private readonly record struct ObservedDispatch(WorkItemId? WorkItemId, bool IsGenericWake)
        {
            public static ObservedDispatch ForWorkItem(WorkItemId id) => new(id, false);
            public static ObservedDispatch GenericWake { get; } = new(null, true);
        }
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

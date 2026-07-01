using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

// Framework FakeTimeProvider (CreateTimer fires on Advance); aliased to avoid
// the namespace-local FakeTimeProvider in AgentClassRouterScoreTests.cs whose
// CreateTimer would stay on the system clock. See SandboxSuspendResumeTests.
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;

namespace CodeyBox.Tests;

[Collection("Background service timing")]
public sealed class WorkerPoolSlotReleaseWakeTests : IDisposable
{
    private static readonly TimeSpan DispatchWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoDispatchQuietPeriod = TimeSpan.FromMilliseconds(500);
    // Backstop for event-driven positive waits (TaskCompletionSource-backed
    // WaitForEnteredAsync/WaitForDoneAsync) that must survive severe CPU
    // starvation under the 6-core capped full suite on a co-resident host —
    // never the mechanism that makes the assertion pass, only headroom so a
    // correct-but-slow dispatch is not misread as a failure.
    private static readonly TimeSpan StarvationBackstopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SpawnPacingBranchInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SpawnPacingEarlyExitTimeout = TimeSpan.FromSeconds(4);

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

        // Fill the whole 4-slot pool so the "backlog is held out" assertion is a
        // DETERMINISTIC consequence of zero free slots, not a race against the
        // refill loop. The dispatcher's contract is to refill EVERY open slot
        // from one wake (see DispatchWake_RefillsAllOpenSlotsFromReadyBacklog...),
        // so a single occupied slot in a 4-slot pool leaves three free slots that
        // the very first wake legitimately fills — checking HasEntered==false at
        // that instant only ever observed "not yet" by luck on a loaded host and
        // was guaranteed to fail when the dispatcher won the race. Occupying all
        // four slots makes the hold-out real: backlog cannot enter until a slot
        // is released.
        var now = DateTimeOffset.UtcNow;
        var occupants = new[]
        {
            MakeItem(createdAt: now),
            MakeItem(createdAt: now.AddMilliseconds(1)),
            MakeItem(createdAt: now.AddMilliseconds(2)),
            MakeItem(createdAt: now.AddMilliseconds(3)),
        };
        var readyBacklog = new[]
        {
            MakeItem(createdAt: now.AddMilliseconds(4)),
            MakeItem(createdAt: now.AddMilliseconds(5)),
            MakeItem(createdAt: now.AddMilliseconds(6)),
        };

        foreach (var item in occupants)
            await _store.CreateAsync(item);
        foreach (var item in readyBacklog)
            await _store.CreateAsync(item);

        // One kick is enough: the refill loop fills all four slots from the
        // store-backed pickup query without a per-item enqueue.
        await queue.EnqueueAsync(occupants[0].Id);
        foreach (var item in occupants)
            Assert.True(
                await pipeline.WaitForEnteredAsync(item.Id, DispatchWaitTimeout),
                "All four slots should fill from a single kick via the store-backed refill loop.");

        // Pool is now full (four entered occupants == four slots), so the ready
        // backlog is genuinely blocked. This is a real invariant, not a timing
        // snapshot.
        foreach (var item in readyBacklog)
            Assert.False(pipeline.HasEntered(item.Id));

        // Release the occupants one at a time. Each completion's slot-release
        // wake must refill exactly one open slot from the independent ready
        // backlog WITHOUT any per-item kick (EnqueueCount stays 0 for each
        // backlog id), proving the slot-release wake keeps refilling while free
        // slots and ready backlog remain.
        for (var i = 0; i < readyBacklog.Length; i++)
        {
            pipeline.Release(occupants[i].Id);
            Assert.True(await pipeline.WaitForDoneAsync(occupants[i].Id, DispatchWaitTimeout));

            Assert.True(
                await pipeline.WaitForEnteredAsync(readyBacklog[i].Id, DispatchWaitTimeout),
                "A slot-release wake should refill the freed slot from independent ready backlog without an external kick.");
            Assert.Equal(0, queue.EnqueueCount(readyBacklog[i].Id));
        }

        pipeline.Release(occupants[^1].Id);
        foreach (var item in readyBacklog)
            pipeline.Release(item.Id);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DispatchWake_RefillsAllOpenSlotsFromReadyBacklogWithoutPerItemSignals()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 3 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var readyBacklog = new[]
        {
            MakeItem(createdAt: now),
            MakeItem(createdAt: now.AddMilliseconds(1)),
            MakeItem(createdAt: now.AddMilliseconds(2)),
        };

        foreach (var item in readyBacklog)
            await _store.CreateAsync(item);

        await queue.EnqueueDispatchWakeAsync();

        foreach (var item in readyBacklog)
        {
            Assert.True(
                await pipeline.WaitForEnteredAsync(item.Id, DispatchWaitTimeout),
                "One dispatch wake should keep refilling while free slots and ready backlog remain.");
            Assert.Equal(0, queue.EnqueueCount(item.Id));
        }

        Assert.Equal(1, queue.GenericWakeEnqueueCount);
        Assert.Equal(1, queue.TotalEnqueueCount);

        foreach (var item in readyBacklog)
            pipeline.Release(item.Id);
        foreach (var item in readyBacklog)
            Assert.True(await pipeline.WaitForDoneAsync(item.Id, DispatchWaitTimeout));

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
        // The cap-retry deferral timer now runs on the injected clock, so the
        // quiet-window assertion below is deterministic: the timer cannot fire
        // until the test advances the fake clock. The previous revision relied
        // on a 5s real-wall-clock window to keep the timer from firing during
        // the assertion (and documented a race observed at ~806ms under load) —
        // routing ScheduleDeferredRequeue's Task.Delay through _time removes
        // that wall-clock dependency entirely.
        var capRetryDelay = TimeSpan.FromSeconds(5);
        var fakeTime = new ControllableTimeProvider();
        var concurrency = new AgentConcurrencyOptions
        {
            Members = { ["codex"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
        };
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            agentConcurrency: concurrency,
            quotaRouterOptions: new QuotaRouterOptions { CapRetryRecheckInterval = capRetryDelay },
            timeProvider: fakeTime);

        Assert.True(svc.TryReserveAgentSlotForTest(AgentKind.Codex));

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var item = MakeItem(fakeTime.GetUtcNow()) with { Agent = AgentKind.Codex };
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

        // Without advancing the clock the deferral timer cannot fire, so the
        // generic slot-release wake must not produce a retry. This is now a
        // deterministic invariant rather than a real-time quiet-window race.
        Assert.False(
            await WaitUntilAsync(() => queue.EnqueueCount(item.Id) > 1, TimeSpan.FromMilliseconds(300)),
            "The generic slot-release wake must not clear the completed item's deferral or enqueue an immediate retry.");
        Assert.True(svc.IsDeferredForTest(item.Id));

        // Drive the configured cap deferral interval on the injected clock: the
        // item-specific retry must occur only when that timer fires.
        Assert.True(
            await AdvanceUntilAsync(
                fakeTime,
                capRetryDelay,
                () => queue.EnqueueCount(item.Id) > 1),
            "The item-specific retry should occur only when the configured cap deferral interval fires.");

        svc.ReleaseAgentSlotForTest(AgentKind.Codex);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeferredRequeue_DuplicateScheduleKeepsSingleRetryOwner()
    {
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var fakeTime = new ControllableTimeProvider();
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            timeProvider: fakeTime);

        var id = WorkItemId.New();
        var delay = TimeSpan.FromMilliseconds(75);

        svc.ScheduleInfrastructureDeferredRequeue(id, delay);
        svc.ScheduleInfrastructureDeferredRequeue(id, delay);

        Assert.True(svc.IsDeferredForTest(id));
        // Both deferral timers run on the injected clock, so the single-owner
        // contract is exercised deterministically: advancing past the delay
        // fires exactly the owning timer's retry wake.
        Assert.True(
            await AdvanceUntilAsync(fakeTime, delay, () => queue.EnqueueCount(id) == 1),
            "The first deferral owner should emit the retry wake.");
        Assert.False(svc.IsDeferredForTest(id));

        // The duplicate schedule was rejected at registration (TryAdd failed),
        // so it never armed a second timer. Advancing the clock far past the
        // delay must still produce no second retry wake — deterministic now
        // that the timers no longer depend on the wall clock.
        fakeTime.Advance(delay + delay);
        await Task.Delay(50);
        Assert.False(
            await WaitUntilAsync(() => queue.EnqueueCount(id) > 1, TimeSpan.FromMilliseconds(250)),
            "A duplicate deferral schedule must not create a second retry wake for the same item.");
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
        // The scenario under test is the pause of a second, already-consumed
        // wake while it waits for the first worker slot. Make the first item
        // unambiguously first in the store-backed dispatcher ordering so the
        // assertion does not depend on timestamp precision under load.
        var running = MakeItem(createdAt: now) with { Priority = 1 };
        var readyBacklog = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(running);
        await _store.CreateAsync(readyBacklog);
        await queue.EnqueueAsync(running.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(running.Id, StarvationBackstopTimeout));

        await controller.PauseAsync("slot release wake suppression test");
        pipeline.Release(running.Id);
        Assert.True(await pipeline.WaitForDoneAsync(running.Id, StarvationBackstopTimeout));
        Assert.True(
            await WaitUntilAsync(() => queue.TotalEnqueueCount >= 2, DispatchWaitTimeout),
            "The slot-release wake should be enqueued even when the queue is paused.");

        // Negative suppression check. This is safe against a starved dispatch
        // loop, not a wall-clock gamble: the pause is fully committed
        // (PauseAsync awaited) BEFORE the slot-release wake can even exist
        // (Release → worker completes → wake enqueued), and the dispatch loop
        // re-reads IsQueuePaused after every dequeue and again after acquiring
        // the concurrency gate, before any PickNextEligibleAsync (see
        // OrchestratorService dispatch loop). So the ready backlog cannot be
        // dispatched while paused regardless of scheduling; the quiet period only
        // needs to give an ERRONEOUS dispatch time to surface. We additionally
        // assert the durable row is still Queued, so suppression is proven by
        // observable state, not solely by the timed window.
        Assert.False(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, NoDispatchQuietPeriod),
            "The paused queue should hold the slot-release wake without dispatching during the quiet period.");
        var stored = await _store.GetAsync(readyBacklog.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await controller.ResumeAsync();
        // WaitForEnteredAsync / WaitForDoneAsync are event-driven (TaskCompletion
        // Source set the instant the pipeline enters/finishes the item), so these
        // are deterministic signals — the timeout is only a backstop. Under the
        // 6-core capped full suite the host can be pushed far past the cap (load
        // has been observed in the 50-90 range from the co-resident orchestrator +
        // VMs), stretching the resume→dispatch→enter→done latency well beyond the
        // 10s DispatchWaitTimeout even though the wake fires promptly. Use a
        // generous backstop so a correct-but-starved resume is not misread as a
        // failure; a genuine "resume does not dispatch" regression still fails
        // because the event never fires at all.
        Assert.True(
            await pipeline.WaitForEnteredAsync(readyBacklog.Id, StarvationBackstopTimeout),
            "The paused branch should preserve the slot-release wake so resume picks up the ready backlog.");

        pipeline.Release(readyBacklog.Id);
        Assert.True(await pipeline.WaitForDoneAsync(readyBacklog.Id, StarvationBackstopTimeout));

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
        // Force a pacing window and pause from the reservation hook so the
        // second item exercises the post-pacing queue-pause branch
        // deterministically. Reset the timestamp before resume so the
        // carried-over first spawn does not block the post-resume dispatch.
        WorkItemId? pauseOnReserve = null;
        var pauseApplied = false;
        var reservedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = SpawnPacingBranchInterval,
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
        svc.SetLastSpawnAtForTest(DateTimeOffset.UtcNow);
        pipeline.Release(first.Id);
        Assert.True(await pipeline.WaitForDoneAsync(first.Id, DispatchWaitTimeout));

        await reservedSecond.Task.WaitAsync(DispatchWaitTimeout);

        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(second.Id), SpawnPacingEarlyExitTimeout),
            "The queue-pause branch after spawn pacing must unreserve the item and release the gate.");
        Assert.False(pipeline.HasEntered(second.Id));

        svc.SetLastSpawnAtForTest(DateTimeOffset.UtcNow - SpawnPacingBranchInterval);
        await controller.ResumeAsync();
        Assert.True(
            await pipeline.WaitForEnteredAsync(second.Id, DispatchWaitTimeout + SpawnPacingBranchInterval),
            "The post-pickup queue-pause branch should preserve a wake so resume dispatches the item.");

        pipeline.Release(second.Id);
        Assert.True(await pipeline.WaitForDoneAsync(second.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task QueuePauseBetweenSpawnReservationAndPipelineStart_SkipsPipelineAndPreservesItem()
    {
        // Covers the IsQueuePaused branch inside the worker's Task.Run body:
        // when the queue pauses after spawn pacing completed but before the
        // pipeline starts, the worker must log+return without running the
        // item, leaving the work item Queued so a later resume can dispatch
        // it.
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);

        // OnWorkerSpawned runs synchronously between the spawn timestamp
        // write and Task.Run. Pausing the queue once on the first spawn
        // guarantees the Task.Run body sees IsQueuePaused == true while
        // leaving later spawns (post-resume) untouched.
        SqliteQueueController? capturedController = null;
        var pausesIssued = 0;
        var pauseIssued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseOnSpawn = new Action(() =>
        {
            if (Interlocked.Increment(ref pausesIssued) != 1) return;
            capturedController?.PauseAsync("test: pause between spawn and pipeline").GetAwaiter().GetResult();
            pauseIssued.TrySetResult();
        });
        capturedController = controller;
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                OnWorkerSpawned = pauseOnSpawn,
            },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var item = MakeItem(createdAt: DateTimeOffset.UtcNow);
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await pauseIssued.Task.WaitAsync(DispatchWaitTimeout);
        Assert.Equal(QueueState.Paused, controller.State);
        Assert.False(
            await pipeline.WaitForEnteredAsync(item.Id, NoDispatchQuietPeriod),
            "The worker must not enter the pipeline when the queue paused between spawn and pipeline start.");
        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(item.Id), DispatchWaitTimeout),
            "The skipped worker's finally block must release the slot and reservation.");

        var stored = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);

        await controller.ResumeAsync();
        Assert.True(
            await pipeline.WaitForEnteredAsync(item.Id, DispatchWaitTimeout),
            "After resume the item must dispatch normally.");
        pipeline.Release(item.Id);
        Assert.True(await pipeline.WaitForDoneAsync(item.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SpawnPacingDelay_BreaksPromptlyOnQueuePauseDuringWait()
    {
        // Asserts the pause-detection latency of WaitForSpawnPacingDelayAsync:
        // when the queue pauses while the worker is mid-wait, the wait must
        // exit well before the configured MinSpawnInterval would otherwise
        // elapse. A regression that lost the IsQueuePaused check inside the
        // polling loop would block for the full MinSpawnInterval.
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);
        var queue = new ObservedTaskQueue();
        var pipeline = new ReleaseControlledPipeline(_store);
        using var registry = new CancellationRegistry(CancellationToken.None);
        var minSpawnInterval = TimeSpan.FromSeconds(5);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 1,
                MinSpawnInterval = minSpawnInterval,
            },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        await svc.StartAsync(CancellationToken.None);
        await queue.WaitForFirstDequeueAsync(DispatchWaitTimeout);

        var now = DateTimeOffset.UtcNow;
        var first = MakeItem(createdAt: now);
        var second = MakeItem(createdAt: now.AddMilliseconds(1));
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);
        await queue.EnqueueAsync(first.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(first.Id, DispatchWaitTimeout));
        pipeline.Release(first.Id);
        Assert.True(await pipeline.WaitForDoneAsync(first.Id, DispatchWaitTimeout));

        Assert.True(
            await WaitUntilAsync(() => svc.IsActiveForTest(second.Id), DispatchWaitTimeout),
            "The second item should be reserved before the spawn-pacing delay completes.");

        // The pacing wait should be at least a few seconds at this point
        // (MinSpawnInterval=5s less first-item processing). Pausing the
        // queue must break the wait far faster than that residual interval,
        // proving the polling loop observes IsQueuePaused mid-wait. A
        // regression that lost the check would block until the full pacing
        // window elapsed.
        var pauseStart = DateTimeOffset.UtcNow;
        await controller.PauseAsync("pause during spawn pacing wait");
        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(second.Id), TimeSpan.FromSeconds(2)),
            "The polling loop in WaitForSpawnPacingDelayAsync must observe IsQueuePaused and exit promptly.");
        var detectionLatency = DateTimeOffset.UtcNow - pauseStart;
        Assert.True(
            detectionLatency < TimeSpan.FromMilliseconds(1500),
            $"Pause detection in the spawn-pacing wait took {detectionLatency} which is far longer than the polling interval; the wait did not observe the pause.");
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
                MinSpawnInterval = SpawnPacingBranchInterval,
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
        svc.SetLastSpawnAtForTest(DateTimeOffset.UtcNow);
        pipeline.Release(first.Id);
        Assert.True(await pipeline.WaitForDoneAsync(first.Id, DispatchWaitTimeout));

        await reservedSecond.Task.WaitAsync(DispatchWaitTimeout);

        Assert.True(
            await WaitUntilAsync(() => !svc.IsActiveForTest(second.Id), SpawnPacingEarlyExitTimeout),
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

    /// <summary>
    /// Advances the injected fake clock by <paramref name="step"/> in a loop,
    /// yielding between advances so the deferral-timer continuation and the
    /// subsequent SQLite enqueue can run, until <paramref name="predicate"/>
    /// trips. The fake clock — not the wall clock — is what fires the deferral
    /// timer; there is an unavoidable scheduling gap between the moment
    /// ScheduleDeferredRequeue registers its timer and the moment we advance,
    /// so the loop re-advances (each Advance fires any already-registered timer)
    /// and yields. The 30s wall-clock backstop only guards against a genuine
    /// non-firing regression and is never the mechanism that fires the timer,
    /// so it does not reintroduce wall-clock flakiness.
    /// </summary>
    private static async Task<bool> AdvanceUntilAsync(
        ControllableTimeProvider fakeTime,
        TimeSpan step,
        Func<bool> predicate)
    {
        var backstop = DateTime.UtcNow.AddSeconds(30);
        while (!predicate())
        {
            fakeTime.Advance(step);
            await Task.Yield();
            if (predicate())
                return true;

            await Task.Delay(TimeSpan.FromMilliseconds(2));
            if (DateTime.UtcNow > backstop)
                return predicate();
        }
        return true;
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

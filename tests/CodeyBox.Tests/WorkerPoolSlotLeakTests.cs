using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the 2026-09-07 worker-slot leak: two worker slots were
/// never released, the pool reported at capacity for hours with no running work
/// item and no sandbox, and no watchdog fired because every health signal
/// assumed an at-capacity pool meant busy workers.
/// </summary>
[Collection("Background service timing")]
public sealed class WorkerPoolSlotLeakTests : IDisposable
{
    private static readonly TimeSpan DispatchWaitTimeout = TimeSpan.FromSeconds(20);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-slotleak-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly InMemoryTaskQueue _queue;

    public WorkerPoolSlotLeakTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _queue = new InMemoryTaskQueue();
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(WorkItemState state = WorkItemState.Queued) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
    };

    private OrchestratorService BuildService(
        IPipelineRunner pipeline,
        IWorkItemStore? store = null,
        OrchestratorOptions? opts = null,
        ILogger<OrchestratorService>? log = null,
        TimeProvider? time = null,
        Func<long>? sandboxes = null) =>
        new(
            _queue,
            store ?? _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            opts ?? new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            log ?? NullLogger<OrchestratorService>.Instance,
            timeProvider: time,
            activeSandboxCountProvider: sandboxes);

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    private static async Task<WorkItem?> WaitForStateAsync(
        IWorkItemStore store,
        WorkItemId id,
        Func<WorkItemState, bool> match,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var item = await store.GetAsync(id);
            if (item is not null && match(item.State))
                return item;
            await Task.Delay(25);
        }
        return await store.GetAsync(id);
    }

    /// <summary>
    /// A worker whose pipeline throws before any phase begins (no store write,
    /// no state change) still releases its slot: with Max=1 a second spawn is
    /// only possible after the first worker's slot is freed.
    /// </summary>
    [Fact]
    public async Task WorkerThrowingBeforePhaseBegins_ReleasesSlotAndRedispatches()
    {
        var invocations = 0;
        var pipeline = new ThrowingPipeline(() => Interlocked.Increment(ref invocations));
        var svc = BuildService(pipeline);
        var item = MakeItem();
        await _store.CreateAsync(item);
        await _queue.EnqueueAsync(item.Id);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            Assert.True(
                await WaitUntilAsync(() => Volatile.Read(ref invocations) >= 2, DispatchWaitTimeout),
                "With MaxConcurrentWorkers=1 a second pickup proves the first worker released its slot.");

            var after = await _store.GetAsync(item.Id);
            Assert.NotNull(after);
            Assert.Equal(WorkItemState.Queued, after.State);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
            svc.Dispose();
        }

        Assert.Equal(0, svc.GlobalConcurrencyGateInFlightForTest);
        Assert.Equal(0, svc.CurrentlyRunningTotal);
        Assert.Empty(svc.GetWorkerSlotOccupancy());
    }

    /// <summary>
    /// A store outage striking pickup after the gate was acquired releases the
    /// slot and leaves the dispatch loop alive for the next signal — it must
    /// neither leak the permit nor fault ExecuteAsync.
    /// </summary>
    [Fact]
    public async Task PickupStoreFailureAfterSlotAcquisition_ReleasesSlotAndDispatchSurvives()
    {
        var calls = 0;
        var faultConsumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedWorkItemStore(_store)
        {
            ListDispatchEligibleHook = _ =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    faultConsumed.TrySetResult();
                    return Task.FromException(new InvalidOperationException("simulated pickup outage"));
                }
                return Task.CompletedTask;
            },
        };
        var svc = BuildService(new CompletingPipeline(_store), store: gated);
        var item = MakeItem();
        await _store.CreateAsync(item);
        await _queue.EnqueueAsync(item.Id);

        try
        {
            await svc.StartAsync(CancellationToken.None);

            await faultConsumed.Task.WaitAsync(DispatchWaitTimeout);
            Assert.True(
                await WaitUntilAsync(() => svc.GlobalConcurrencyGateInFlightForTest == 0, DispatchWaitTimeout),
                "The faulted pickup must release the acquired slot.");

            await _queue.EnqueueAsync(item.Id);
            var done = await WaitForStateAsync(_store, item.Id, s => s == WorkItemState.Done, DispatchWaitTimeout);
            Assert.NotNull(done);
            Assert.Equal(WorkItemState.Done, done.State);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
            svc.Dispose();
        }

        Assert.Equal(0, svc.GlobalConcurrencyGateInFlightForTest);
        Assert.Equal(0, svc.CurrentlyRunningTotal);
        Assert.Empty(svc.GetWorkerSlotOccupancy());
    }

    /// <summary>
    /// Host shutdown racing a blocked pickup (gate already acquired) releases
    /// the slot and ends the dispatch loop instead of leaking the permit.
    /// </summary>
    [Fact]
    public async Task CancelledMidAcquisition_ReleasesSlotAndEndsDispatchCleanly()
    {
        var enteredPickup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePickup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new GatedWorkItemStore(_store)
        {
            ListDispatchEligibleHook = async ct =>
            {
                enteredPickup.TrySetResult();
                await releasePickup.Task.WaitAsync(ct);
            },
        };
        var svc = BuildService(new CompletingPipeline(_store), store: gated);
        var item = MakeItem();
        await _store.CreateAsync(item);
        await _queue.EnqueueAsync(item.Id);

        using var hostCts = new CancellationTokenSource();
        await svc.StartAsync(hostCts.Token);

        await enteredPickup.Task.WaitAsync(DispatchWaitTimeout);
        Assert.Equal(1, svc.GlobalConcurrencyGateInFlightForTest);

        hostCts.Cancel();

        Assert.True(
            await WaitUntilAsync(() => svc.GlobalConcurrencyGateInFlightForTest == 0, DispatchWaitTimeout),
            "Cancelling mid-acquisition must release the acquired slot.");
        Assert.True(
            await WaitUntilAsync(() => svc.CurrentlyRunningTotal == 0, DispatchWaitTimeout),
            "No worker was spawned, so the running counter must stay at zero.");

        await svc.StopAsync(CancellationToken.None).WaitAsync(DispatchWaitTimeout);
        svc.Dispose();
    }

    /// <summary>
    /// The incident shape: a slot whose worker already exited without releasing,
    /// pool at capacity, no running item, no sandbox. Reconciliation reclaims
    /// it at Warning and the item becomes dispatchable again.
    /// </summary>
    [Fact]
    public async Task OrphanedExitedWorkerSlot_ReclaimedWithWarningAndItemRedispatches()
    {
        var log = new CapturingLogger<OrchestratorService>();
        var svc = BuildService(
            new CompletingPipeline(_store),
            log: log,
            sandboxes: () => 0);
        var item = MakeItem();
        await _store.CreateAsync(item);

        svc.SimulateOrphanedSlotForTest(item.Id, completedTask: true);
        Assert.Equal(1, svc.CurrentlyRunningTotal);
        Assert.Single(svc.GetWorkerSlotOccupancy());

        var result = await svc.TryReclaimOrphanedWorkerSlotsAsync(
            TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.True(result.PoolAtCapacity);
        var reclaimed = Assert.Single(result.ReclaimedSlots);
        Assert.Equal(item.Id.ToString(), reclaimed.WorkItemId);
        Assert.Equal(0, svc.GlobalConcurrencyGateInFlightForTest);
        Assert.Equal(0, svc.CurrentlyRunningTotal);
        Assert.Empty(svc.GetWorkerSlotOccupancy());
        Assert.False(svc.IsActiveForTest(item.Id));

        var warning = Assert.Single(
            log.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("reclaimed"));
        Assert.Contains(item.Id.ToString(), warning.Message);

        try
        {
            await svc.StartAsync(CancellationToken.None);
            await _queue.EnqueueAsync(item.Id);
            var done = await WaitForStateAsync(_store, item.Id, s => s == WorkItemState.Done, DispatchWaitTimeout);
            Assert.NotNull(done);
            Assert.Equal(WorkItemState.Done, done.State);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
            svc.Dispose();
        }
    }

    /// <summary>
    /// Reconciliation stays hands-off while any slot is provably live: a
    /// running item or a live sandbox blocks every reclaim, and a young worker
    /// whose task is still running is left alone.
    /// </summary>
    [Fact]
    public async Task OrphanedSlot_NotReclaimedWhileAnySlotIsProvablyLive()
    {
        var time = new ManualTimeProvider();
        var sandboxCount = 0L;
        var svc = BuildService(
            new CompletingPipeline(_store),
            time: time,
            sandboxes: () => Volatile.Read(ref sandboxCount));

        var orphan = MakeItem();
        var running = MakeItem(WorkItemState.Working);
        var hung = MakeItem();
        await _store.CreateAsync(orphan);
        await _store.CreateAsync(running);
        await _store.CreateAsync(hung);

        svc.SimulateOrphanedSlotForTest(orphan.Id, completedTask: true);

        var withRunning = await svc.TryReclaimOrphanedWorkerSlotsAsync(
            TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.True(withRunning.PoolAtCapacity);
        Assert.Empty(withRunning.ReclaimedSlots);
        Assert.Equal(1, withRunning.RunningItemCount);

        await _store.UpdateAsync(running.With(WorkItemState.Queued));
        Interlocked.Exchange(ref sandboxCount, 1);
        var withSandbox = await svc.TryReclaimOrphanedWorkerSlotsAsync(
            TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Empty(withSandbox.ReclaimedSlots);
        Assert.Equal(1, withSandbox.ActiveSandboxCount);

        Interlocked.Exchange(ref sandboxCount, 0);
        var orphanReclaimed = await svc.TryReclaimOrphanedWorkerSlotsAsync(
            TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Single(orphanReclaimed.ReclaimedSlots);

        svc.SimulateOrphanedSlotForTest(hung.Id, completedTask: false);
        var youngHungSkipped = await svc.TryReclaimOrphanedWorkerSlotsAsync(
            TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Empty(youngHungSkipped.ReclaimedSlots);
        Assert.Single(svc.GetWorkerSlotOccupancy());

        svc.Dispose();
    }

    /// <summary>
    /// A hung worker (task still running) past the max slot age is reclaimed:
    /// it is invisible to every item-state watchdog yet holds a full slot.
    /// </summary>
    [Fact]
    public async Task HungWorkerSlot_PastMaxAge_IsReclaimed()
    {
        var time = new ManualTimeProvider();
        var svc = BuildService(
            new CompletingPipeline(_store),
            time: time,
            sandboxes: () => 0);
        var item = MakeItem();
        await _store.CreateAsync(item);

        svc.SimulateOrphanedSlotForTest(item.Id, completedTask: false);
        var young = await svc.TryReclaimOrphanedWorkerSlotsAsync(
            TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.Empty(young.ReclaimedSlots);

        time.Advance(TimeSpan.FromMinutes(31));
        var aged = await svc.TryReclaimOrphanedWorkerSlotsAsync(
            TimeSpan.FromMinutes(30), CancellationToken.None);
        var reclaimed = Assert.Single(aged.ReclaimedSlots);
        Assert.Equal(item.Id.ToString(), reclaimed.WorkItemId);
        Assert.Equal(0, svc.GlobalConcurrencyGateInFlightForTest);
        Assert.Equal(0, svc.CurrentlyRunningTotal);

        svc.Dispose();
    }

    /// <summary>
    /// End to end through the production watchdog path: an at-capacity pool
    /// with an orphaned slot is reclaimed with a Warning log and a
    /// worker_pool.slot_reclaimed webhook.
    /// </summary>
    [Fact]
    public async Task Watchdog_AtCapacityWithOrphan_ReclaimsAndEmitsWarning()
    {
        var svcLog = new CapturingLogger<OrchestratorService>();
        var svc = BuildService(
            new CompletingPipeline(_store),
            log: svcLog,
            sandboxes: () => 0);
        var item = MakeItem();
        await _store.CreateAsync(item);
        svc.SimulateOrphanedSlotForTest(item.Id, completedTask: true);

        var webhooks = new CapturingWebhookDispatcher();
        var watchdogLog = new CapturingLogger<WorkerPoolHealthWatchdog>();
        var coordinator = new WorkerPoolHealthCoordinator(
            svc,
            _store,
            _queue,
            NullLogger<WorkerPoolHealthCoordinator>.Instance);
        var watchdog = new WorkerPoolHealthWatchdog(
            coordinator,
            () => new WorkerPoolHealthWatchdogOptions
            {
                StallTimeout = TimeSpan.FromMinutes(1),
                CheckInterval = TimeSpan.FromSeconds(1),
                MaxRecoveryAttempts = 1,
                RecoveryVerificationDelay = TimeSpan.Zero,
                OrphanedSlotMaxAge = TimeSpan.FromMinutes(30),
            },
            watchdogLog,
            webhooks: webhooks);

        try
        {
            await watchdog.RunOnceAsync(CancellationToken.None);
        }
        finally
        {
            svc.Dispose();
        }

        Assert.Equal(0, svc.GlobalConcurrencyGateInFlightForTest);
        Assert.Equal(0, svc.CurrentlyRunningTotal);
        Assert.Contains(
            watchdogLog.Entries,
            e => e.Level == LogLevel.Warning && e.Message.Contains("reclaimed"));
        var reclaimed = Assert.Single(webhooks.Events, e => e.Event == "worker_pool.slot_reclaimed");
        Assert.Null(reclaimed.WorkItem);
    }

    /// <summary>
    /// After mixed success/failure dispatch cycles — including workers that
    /// throw mid-run — every permit returns: available slots equal the ceiling
    /// and the pool keeps dispatching.
    /// </summary>
    [Fact]
    public async Task ManyAcquireReleaseCycles_WithFailures_ConserveSlots()
    {
        const int maxConcurrent = 2;
        const int totalItems = 6;
        var attempts = new System.Collections.Concurrent.ConcurrentDictionary<WorkItemId, int>();
        var pipeline = new FlakyPipeline(_store, attempts);
        var svc = BuildService(
            pipeline,
            opts: new OrchestratorOptions { MaxConcurrentWorkers = maxConcurrent });

        var ids = new List<WorkItemId>();
        for (var i = 0; i < totalItems; i++)
        {
            var item = MakeItem();
            ids.Add(item.Id);
            await _store.CreateAsync(item);
            await _queue.EnqueueAsync(item.Id);
        }

        try
        {
            await svc.StartAsync(CancellationToken.None);

            var deadline = DateTimeOffset.UtcNow + DispatchWaitTimeout;
            var allDone = false;
            while (DateTimeOffset.UtcNow < deadline)
            {
                allDone = true;
                foreach (var id in ids)
                {
                    var current = await _store.GetAsync(id);
                    if (current?.State != WorkItemState.Done)
                    {
                        allDone = false;
                        break;
                    }
                }
                if (allDone)
                    break;
                await Task.Delay(50);
            }
            Assert.True(allDone, "All items (including ones whose first attempt threw) must reach Done.");
            Assert.All(ids, id => Assert.True(attempts.TryGetValue(id, out var n) && n >= 2));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
            svc.Dispose();
        }

        Assert.Equal(0, svc.GlobalConcurrencyGateInFlightForTest);
        Assert.Equal(0, svc.CurrentlyRunningTotal);
        Assert.Empty(svc.GetWorkerSlotOccupancy());
    }

    private sealed class ThrowingPipeline(Action onEnter) : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            onEnter();
            throw new InvalidOperationException("simulated pre-phase failure");
        }
    }

    private sealed class CompletingPipeline(IWorkItemStore store) : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
            => store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }

    private sealed class FlakyPipeline(
        IWorkItemStore store,
        System.Collections.Concurrent.ConcurrentDictionary<WorkItemId, int> attempts) : IPipelineRunner
    {
        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            var attempt = attempts.AddOrUpdate(item.Id, 1, static (_, n) => n + 1);
            if (attempt == 1)
                throw new InvalidOperationException("simulated first-attempt failure");
            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }

    private sealed class GatedWorkItemStore(IWorkItemStore inner) : IWorkItemStore
    {
        public Func<CancellationToken, Task>? ListDispatchEligibleHook { get; set; }

        public async IAsyncEnumerable<WorkItem> ListDispatchEligibleByPriorityAsync(
            IReadOnlySet<WorkItemId> skipIds,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (ListDispatchEligibleHook is { } hook)
                await hook(ct);
            await foreach (var item in inner.ListDispatchEligibleByPriorityAsync(skipIds, ct).ConfigureAwait(false))
                yield return item;
        }

        public Task CreateAsync(WorkItem item, CancellationToken ct = default) => inner.CreateAsync(item, ct);
        public Task UpdateAsync(WorkItem item, CancellationToken ct = default) => inner.UpdateAsync(item, ct);
        public Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default) =>
            inner.TryUpdateIfStateAsync(item, onlyIfState, ct);
        public Task<PriorityUpdateResult> UpdatePriorityAsync(WorkItemId id, int priority, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdatePriorityAsync(id, priority, updatedAt, ct);
        public Task<DependsOnUpdateResult> UpdateDependsOnAsync(WorkItemId id, IReadOnlyList<WorkItemId> dependsOn, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdateDependsOnAsync(id, dependsOn, updatedAt, ct);
        public Task<AuditBudgetUpdateResult> UpdateAuditBudgetAsync(WorkItemId id, int? auditMaxIterations, string? auditComplexity, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.UpdateAuditBudgetAsync(id, auditMaxIterations, auditComplexity, updatedAt, ct);
        public Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default) => inner.GetAsync(id, ct);
        public IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default) =>
            inner.ListByStateAsync(state, ct);
        public Task<int> CountByStateAsync(WorkItemState state, CancellationToken ct = default) =>
            inner.CountByStateAsync(state, ct);
        public Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default) =>
            inner.ReorderAsync(orderedIds, ct);
        public Task<int> CountStartedInWindowAsync(ProjectId projectId, DateTimeOffset since, CancellationToken ct = default) =>
            inner.CountStartedInWindowAsync(projectId, since, ct);
        public Task<int> CountInFlightAsync(ProjectId projectId, CancellationToken ct = default) =>
            inner.CountInFlightAsync(projectId, ct);
        public Task<WorkItem?> GetByExternalIdAsync(ProjectId projectId, string externalId, CancellationToken ct = default) =>
            inner.GetByExternalIdAsync(projectId, externalId, ct);
        public Task<WorkItem?> GetByNamespacedExternalIdAsync(ProjectId projectId, string @namespace, string externalId, CancellationToken ct = default) =>
            inner.GetByNamespacedExternalIdAsync(projectId, @namespace, externalId, ct);
        public Task<WorkItem?> ReplaceExternalIdsAsync(WorkItemId id, IReadOnlyDictionary<string, string> externalIds, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.ReplaceExternalIdsAsync(id, externalIds, updatedAt, ct);
        public Task<IReadOnlyList<(string ProjectId, int State, int Count, string MaxUpdatedAt)>> GetFleetStateCountsAsync(CancellationToken ct = default) =>
            inner.GetFleetStateCountsAsync(ct);
        public Task<IReadOnlyList<(string ProjectId, int State)>> GetFleetRecentOutcomesAsync(int perProject = 5, CancellationToken ct = default) =>
            inner.GetFleetRecentOutcomesAsync(perProject, ct);
        public Task<IReadOnlyDictionary<string, bool>> GetFleetPauseStatesAsync(CancellationToken ct = default) =>
            inner.GetFleetPauseStatesAsync(ct);
        public IAsyncEnumerable<WorkItem> ListByReplaySourceAsync(WorkItemId sourceId, CancellationToken ct = default) =>
            inner.ListByReplaySourceAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListSuspendedAsync(CancellationToken ct = default) => inner.ListSuspendedAsync(ct);
        public Task<IReadOnlySet<string>> GetActiveBaselineImageRefsAsync(CancellationToken ct = default) =>
            inner.GetActiveBaselineImageRefsAsync(ct);
        public Task<IReadOnlyList<(WorkItemId Id, string Title, WorkItemState State)>> ListWorkItemsForBaselineAsync(string baselineImageRef, CancellationToken ct = default) =>
            inner.ListWorkItemsForBaselineAsync(baselineImageRef, ct);
        public Task OrphanReplaysAsync(WorkItemId sourceId, CancellationToken ct = default) => inner.OrphanReplaysAsync(sourceId, ct);
        public IAsyncEnumerable<WorkItem> ListByReleaseAsync(ReleaseId releaseId, CancellationToken ct = default) =>
            inner.ListByReleaseAsync(releaseId, ct);
        public Task<PromptReplaceResult> TryReplacePromptAsync(WorkItemId id, string newPrompt, DateTimeOffset updatedAt, CancellationToken ct = default) =>
            inner.TryReplacePromptAsync(id, newPrompt, updatedAt, ct);
        public Task RecordIterationDispatchAsync(WorkItemId workItemId, int iteration, int promptRevisionAtDispatch, DateTimeOffset dispatchedAt, CancellationToken ct = default) =>
            inner.RecordIterationDispatchAsync(workItemId, iteration, promptRevisionAtDispatch, dispatchedAt, ct);
        public Task<IReadOnlyList<WorkItemIteration>> GetIterationsAsync(WorkItemId workItemId, CancellationToken ct = default) =>
            inner.GetIterationsAsync(workItemId, ct);
    }
}

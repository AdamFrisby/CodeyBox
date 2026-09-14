using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verification for the worker-death infrastructure-recovery contract: losing
/// the worker without a preempt checkpoint is an infrastructure event, not a
/// work-item failure. The item must return to a runnable state (never Failed)
/// and the restart must not consume the recovery budget that guards genuinely
/// wedged items (never AbandonedAfterRecoveryAttempts). Recovery must still
/// leave genuine terminal failures alone.
/// </summary>
[Collection("Background service timing")]
public sealed class WorkerDeathInfrastructureRecoveryTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-infradeath-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly DeadWorkerOptions _opts;
    private readonly DeadWorkerReaper _reaper;

    public WorkerDeathInfrastructureRecoveryTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _webhooks = new CapturingWebhookDispatcher();
        _opts = new DeadWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            DeadWorkerThreshold = TimeSpan.FromSeconds(15),
            CheckInterval = TimeSpan.FromMinutes(60),
            MaxRecoveryAttempts = 2,
        };
        _reaper = new DeadWorkerReaper(
            _registry, _store, _queue, _opts,
            NullLogger<DeadWorkerReaper>.Instance,
            _webhooks);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(WorkItemState state, int recoveryAttempts = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
        RecoveryAttempts = recoveryAttempts,
        StartedAt = state == WorkItemState.Queued ? null : DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    private async Task PlantDeadWorkerAsync(string workItemId)
    {
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "crashed-host",
            ProcessId = 9999,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CurrentWorkItemId = workItemId,
        });
    }

    [Fact]
    public async Task RestartWithoutCheckpoint_RequeuesRunnablePreservingBranchAndBudget()
    {
        // The exact incident scenario: the orchestrator died mid-work-phase
        // without a preempt checkpoint (no worker row survives the restart).
        // Startup recovery must return the item to a runnable state and must
        // not erode the recovery budget — even when prior genuine recoveries
        // already consumed it up to the cap.
        const string workBranch = "codeybox/auto/work-restart";
        var item = MakeItem(WorkItemState.Working, recoveryAttempts: _opts.MaxRecoveryAttempts)
            with { WorkBranch = workBranch };
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(workBranch, after.WorkBranch);
        Assert.True(after.PreserveWorkBranchOnQueuedPickup);
        Assert.Null(after.StartedAt);
        Assert.Equal(_opts.MaxRecoveryAttempts, after.RecoveryAttempts);
        Assert.Contains("without a preempt checkpoint", after.LastError);
        Assert.Equal(1, _queue.Count);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
    }

    [Fact]
    public async Task RestartWithoutCheckpoint_ParksAtNeedsOperatorInputPastInfraCap()
    {
        // Poison-input bound: an item whose content deterministically kills
        // every worker must not requeue forever. Past the consecutive-
        // infrastructure cap the item parks for triage instead of requeueing —
        // still without consuming the genuine-failure budget, and without
        // re-entering the dispatch queue.
        var cappedOpts = new DeadWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            DeadWorkerThreshold = TimeSpan.FromSeconds(15),
            CheckInterval = TimeSpan.FromMinutes(60),
            MaxRecoveryAttempts = 2,
            MaxConsecutiveInfrastructureRecoveries = 1,
        };
        var cappedReaper = new DeadWorkerReaper(
            _registry, _store, _queue, cappedOpts,
            NullLogger<DeadWorkerReaper>.Instance,
            _webhooks);
        var item = MakeItem(WorkItemState.Working, recoveryAttempts: 1) with
        {
            ConsecutiveInfrastructureRecoveries = 1,
            WorkBranch = "codeybox/auto/work-poison",
        };
        await _store.CreateAsync(item);

        await cappedReaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.NeedsOperatorInput, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Equal(2, after.ConsecutiveInfrastructureRecoveries);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task GenuineWorkPhaseFailure_IsNotResurrectedByRecovery()
    {
        // Regression guard: recovery must not swallow real failures. An item
        // the pipeline already recorded as Failed (agent exit, build break,
        // verdict) stays Failed when its worker row goes stale — it is not
        // requeued and its error is untouched.
        var item = MakeItem(WorkItemState.Failed) with
        {
            LastError = "agent exited 1: build broke",
        };
        await _store.CreateAsync(item);
        await PlantDeadWorkerAsync(item.Id.ToString());

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Failed, after.State);
        Assert.Equal("agent exited 1: build broke", after.LastError);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Drain_WaitsForInFlightItemWhilePauseReturnsImmediately()
    {
        // Drain path: with a worker running, pausing returns at once (new
        // pickup stops, the running item is unaffected) while the drain wait
        // stays pending until the in-flight item reaches its safe boundary.
        using var controller = new SqliteQueueController(_dbPath, NullLogger<SqliteQueueController>.Instance);

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new BlockingPipelineRunner(
            _store,
            onStart: () => entered.TrySetResult(),
            proceedGate: release.Task,
            onComplete: () => completed.TrySetResult());

        var reg = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            _queue, _store, pipeline, reg,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            queueController: controller);

        var item = MakeItem(WorkItemState.Queued);
        await _store.CreateAsync(item);
        await _queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Plain pause returns immediately while the worker is blocked and
            // does not disturb it.
            await controller.PauseAsync("drain test");
            Assert.Equal(QueueState.Paused, controller.State);
            Assert.False(completed.Task.IsCompleted);
            Assert.Equal(1, (await svc.GetStatusAsync()).CurrentlyRunning);

            // The drain wait stays pending until the running item finishes.
            var drainTask = QueueDrain.WaitForQuiescenceAsync(
                async ct => (await svc.GetStatusAsync(ct)).CurrentlyRunning,
                TimeSpan.FromSeconds(30),
                CancellationToken.None,
                TimeSpan.FromMilliseconds(25));
            await Task.Delay(300);
            Assert.False(drainTask.IsCompleted);

            release.TrySetResult();
            Assert.True(await drainTask.WaitAsync(TimeSpan.FromSeconds(30)));
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(WorkItemState.Done, (await _store.GetAsync(item.Id))!.State);
        }
        finally
        {
            release.TrySetResult();
            await svc.StopAsync(CancellationToken.None);
        }
    }
}

using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.PipelineAndWorkerLifecycle;

/// <summary>
/// UAT coverage for shutdown recovery and worker-pool/dead-worker lifecycle behavior.
/// Plan anchors:
/// docs/uat/00-plan.md#shutdown-cancellation-and-preemption---preserves-or-recovers-in-flight-work-during-host-shutdown
/// docs/uat/00-plan.md#worker-pool-dispatch-and-dead-worker-recovery---controls-concurrency-pacing-registration-and-orphaned-workers
/// </summary>
public sealed class WorkerRecoveryTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-workers-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Theory]
    [InlineData(WorkItemState.Auditing, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Reworking, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Merging, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Merged)]
    [InlineData(WorkItemState.WorkComplete, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.AuditPassed, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merged, WorkItemState.Merged)]
    public void RestartRecovery_MapsInterruptedAndDurablePipelineStatesToResumeStates(
        WorkItemState entryState,
        WorkItemState expectedState)
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var service = PipelineLifecycleUatHelpers.BuildReplayService(store, queue);
        var item = PipelineLifecycleUatHelpers.WorkerItem(entryState);

        var recovered = service.TryBuildRecoveredStateForTest(item);

        Assert.NotNull(recovered);
        Assert.Equal(expectedState, recovered!.State);
        Assert.Equal(item.RecoveryAttempts + 1, recovered.RecoveryAttempts);
    }

    [Fact]
    public void RestartRecovery_WorkingWithoutCheckpointFailsButCheckpointedWorkIsRequeued()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var service = PipelineLifecycleUatHelpers.BuildReplayService(store, queue);
        var plainWorking = PipelineLifecycleUatHelpers.WorkerItem(WorkItemState.Working);
        var checkpointed = plainWorking with
        {
            Id = WorkItemId.New(),
            PreemptCheckpoint = "refs/codeybox/preempt/test",
        };

        var failed = service.TryBuildRecoveredStateForTest(plainWorking);
        var resumable = service.TryBuildRecoveredStateForTest(checkpointed);

        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Contains("without a preempt checkpoint", failed.LastError);
        Assert.Equal(WorkItemState.Working, resumable!.State);
        Assert.Equal(1, resumable.RecoveryAttempts);
        Assert.Null(resumable.StartedAt);
        Assert.Equal(checkpointed.PreemptCheckpoint, resumable.PreemptCheckpoint);
    }

    [Fact]
    public void RestartRecovery_AbandonsInterruptedItemAfterRecoveryCap()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var service = PipelineLifecycleUatHelpers.BuildReplayService(store, queue, maxRecoveryAttempts: 1);
        var item = PipelineLifecycleUatHelpers.WorkerItem(WorkItemState.Merging, recoveryAttempts: 1);

        var recovered = service.TryBuildRecoveredStateForTest(item);

        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, recovered!.State);
        Assert.Contains("abandoned after 1 recovery attempts", recovered.LastError);
    }

    [Fact]
    public async Task StartupReplay_ReconstructsRunnableQueueAndLeavesDependencyGatedItemsUnqueued()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var service = PipelineLifecycleUatHelpers.BuildReplayService(store, queue);
        var runnable = PipelineLifecycleUatHelpers.WorkerItem(WorkItemState.Merging);
        var dependency = PipelineLifecycleUatHelpers.WorkerItem(WorkItemState.Working);
        var gated = PipelineLifecycleUatHelpers.WorkerItem(WorkItemState.Queued) with
        {
            DependsOn = [dependency.Id],
        };
        await store.CreateAsync(runnable);
        await store.CreateAsync(dependency);
        await store.CreateAsync(gated);

        await service.ReplayPendingForTestAsync(CancellationToken.None);

        Assert.Equal(1, queue.Count);
        Assert.Equal(WorkItemState.AuditPassed, (await store.GetAsync(runnable.Id))!.State);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(dependency.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(gated.Id))!.State);
    }

    [Fact]
    public async Task WorkerRegistry_RegistersHeartbeatsAndDeregistersActiveWorker()
    {
        using var registry = NewRegistry();
        var worker = new WorkerRegistration
        {
            WorkerId = "worker-1",
            HostName = "host",
            ProcessId = 123,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CurrentWorkItemId = "item-a",
        };

        await registry.RegisterAsync(worker);
        await registry.HeartbeatAsync(worker.WorkerId, "item-b");
        var active = Assert.Single(await registry.ListAsync());
        Assert.Equal("item-b", active.CurrentWorkItemId);
        Assert.True(active.LastHeartbeatAt >= worker.LastHeartbeatAt);

        await registry.DeregisterAsync(worker.WorkerId);

        Assert.Empty(await registry.ListAsync());
    }

    [Fact]
    public async Task DeadWorkerReaper_ClaimsExpiredHeartbeatRecoversItemAndEnqueuesOnce()
    {
        using var store = NewStore();
        using var registry = NewRegistry();
        var queue = new InMemoryTaskQueue();
        var item = PipelineLifecycleUatHelpers.WorkerItem(WorkItemState.Merging);
        await store.CreateAsync(item);
        await registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "dead-worker",
            HostName = "host",
            ProcessId = 123,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-2),
            CurrentWorkItemId = item.Id.ToString(),
        });
        var reaper = new DeadWorkerReaper(
            registry,
            store,
            queue,
            new DeadWorkerOptions
            {
                DeadWorkerThreshold = TimeSpan.FromMinutes(30),
                MaxRecoveryAttempts = 2,
            },
            NullLogger<DeadWorkerReaper>.Instance);

        await reaper.RunOnceAsync(CancellationToken.None);
        await reaper.RunOnceAsync(CancellationToken.None);

        Assert.Empty(await registry.ListAsync());
        var recovered = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditPassed, recovered!.State);
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Equal(1, queue.Count);
    }

    private SqliteWorkItemStore NewStore()
        => new(Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db"));

    private SqliteWorkerRegistry NewRegistry()
        => new(
            Path.Combine(_workspace, "workers-" + Guid.NewGuid().ToString("N")[..8] + ".db"),
            NullLogger<SqliteWorkerRegistry>.Instance);
}

using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests.Uat.PersistenceAndRecovery;

/// <summary>
/// UAT coverage for restart queue replay and recovery caps from the Persistence And Recovery section.
/// Plan anchor:
/// docs/uat/00-plan.md#restart-resumption-and-recovery-caps---reconstructs-runnable-queue-after-process-restart
/// </summary>
public sealed class RestartRecoveryUatTests : IDisposable
{
    private readonly PersistenceAndRecoveryWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Theory]
    [InlineData(WorkItemState.Auditing, WorkItemState.WorkComplete, 1)]
    [InlineData(WorkItemState.Reworking, WorkItemState.WorkComplete, 1)]
    [InlineData(WorkItemState.Merging, WorkItemState.AuditPassed, 1)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Merged, 1)]
    [InlineData(WorkItemState.WorkComplete, WorkItemState.WorkComplete, 1)]
    [InlineData(WorkItemState.AuditPassed, WorkItemState.AuditPassed, 1)]
    [InlineData(WorkItemState.Merged, WorkItemState.Merged, 1)]
    public void RecoveryMapping_ResetsInterruptedPhasesAndCountsDurableBoundaryRedispatches(
        WorkItemState entryState,
        WorkItemState expectedState,
        int expectedAttemptIncrement)
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var queue = new InMemoryTaskQueue();
        var service = PersistenceAndRecoveryHelpers.BuildReplayService(store, queue);
        var item = PersistenceAndRecoveryHelpers.Item(entryState, recoveryAttempts: 2);

        var recovered = service.TryBuildRecoveredStateForTest(item);

        Assert.NotNull(recovered);
        Assert.Equal(expectedState, recovered!.State);
        Assert.Equal(2 + expectedAttemptIncrement, recovered.RecoveryAttempts);
    }

    [Fact]
    public void WorkingCrashWithoutCheckpointFailsButPreemptCheckpointCanResume()
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var queue = new InMemoryTaskQueue();
        var service = PersistenceAndRecoveryHelpers.BuildReplayService(store, queue);
        var crashedWork = PersistenceAndRecoveryHelpers.Item(WorkItemState.Working);
        var preemptedWork = crashedWork with
        {
            Id = WorkItemId.New(),
            PreemptedAt = DateTimeOffset.Parse("2026-05-14T03:00:00Z"),
            PreemptCheckpoint = "refs/heads/codeybox/preempt/uat",
        };

        var failed = service.TryBuildRecoveredStateForTest(crashedWork);
        var resumable = service.TryBuildRecoveredStateForTest(preemptedWork);

        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal(1, failed.RecoveryAttempts);
        Assert.Null(failed.StartedAt);
        Assert.Null(failed.PreemptCheckpoint);
        Assert.Contains("without a preempt checkpoint", failed.LastError);
        Assert.Equal(WorkItemState.Working, resumable!.State);
        Assert.Equal(1, resumable.RecoveryAttempts);
        Assert.Null(resumable.StartedAt);
        Assert.Equal(preemptedWork.PreemptCheckpoint, resumable.PreemptCheckpoint);
    }

    [Fact]
    public void RecoveryCap_AbandonsInterruptedAndDurableBoundaryRecoveryBeyondLimit()
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var queue = new InMemoryTaskQueue();
        var service = PersistenceAndRecoveryHelpers.BuildReplayService(store, queue, maxRecoveryAttempts: 2);
        var interrupted = PersistenceAndRecoveryHelpers.Item(WorkItemState.Merging, recoveryAttempts: 2);
        var durableBoundary = PersistenceAndRecoveryHelpers.Item(WorkItemState.Merged, recoveryAttempts: 2);

        var abandoned = service.TryBuildRecoveredStateForTest(interrupted);
        var boundaryAbandoned = service.TryBuildRecoveredStateForTest(durableBoundary);

        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, abandoned!.State);
        Assert.Contains("abandoned after 2 recovery attempts", abandoned.LastError);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, boundaryAbandoned!.State);
        Assert.Contains("abandoned after 2 recovery attempts", boundaryAbandoned.LastError);
    }

    [Fact]
    public async Task StartupReplay_ReconstructsRunnableQueueAndLeavesDependencyGatedItemsParked()
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var queue = new InMemoryTaskQueue();
        var service = PersistenceAndRecoveryHelpers.BuildReplayService(store, queue);
        var completedParent = PersistenceAndRecoveryHelpers.Item(WorkItemState.Done);
        var inFlightParent = PersistenceAndRecoveryHelpers.Item(WorkItemState.Working);
        var runnableQueued = PersistenceAndRecoveryHelpers.Item() with
        {
            DependsOn = [completedParent.Id],
        };
        var blockedQueued = PersistenceAndRecoveryHelpers.Item() with
        {
            DependsOn = [inFlightParent.Id],
        };
        var interruptedAudit = PersistenceAndRecoveryHelpers.Item(WorkItemState.Auditing);
        var durableMerged = PersistenceAndRecoveryHelpers.Item(WorkItemState.Merged);
        var terminal = PersistenceAndRecoveryHelpers.Item(WorkItemState.Done);
        foreach (var item in new[] { completedParent, inFlightParent, runnableQueued, blockedQueued, interruptedAudit, durableMerged, terminal })
            await store.CreateAsync(item);

        await service.ReplayPendingForTestAsync(CancellationToken.None);

        Assert.Equal(3, queue.Count);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(inFlightParent.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(blockedQueued.Id))!.State);
        Assert.Equal(WorkItemState.WorkComplete, (await store.GetAsync(interruptedAudit.Id))!.State);
        Assert.Equal(1, (await store.GetAsync(interruptedAudit.Id))!.RecoveryAttempts);
        Assert.Equal(WorkItemState.Merged, (await store.GetAsync(durableMerged.Id))!.State);
        Assert.Equal(1, (await store.GetAsync(durableMerged.Id))!.RecoveryAttempts);
        Assert.Equal(WorkItemState.Done, (await store.GetAsync(terminal.Id))!.State);
    }

    [Fact]
    public async Task StartupReplay_AbandonsAfterRecoveryCapAndDoesNotEnqueueTerminalItems()
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var queue = new InMemoryTaskQueue();
        var service = PersistenceAndRecoveryHelpers.BuildReplayService(store, queue, maxRecoveryAttempts: 1);
        var overCap = PersistenceAndRecoveryHelpers.Item(WorkItemState.Auditing, recoveryAttempts: 1);
        var terminalFailed = PersistenceAndRecoveryHelpers.Item(WorkItemState.Failed);
        await store.CreateAsync(overCap);
        await store.CreateAsync(terminalFailed);

        await service.ReplayPendingForTestAsync(CancellationToken.None);

        var recovered = await store.GetAsync(overCap.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, recovered!.State);
        Assert.Contains("abandoned after 1 recovery attempts", recovered.LastError);
        Assert.Equal(0, queue.Count);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(terminalFailed.Id))!.State);
    }

    [Fact]
    public async Task StartupReplay_DoesNotAutoRecoverOperatorOrLegacyCancelledItems()
    {
        using var store = new SqliteWorkItemStore(_workspace.NewDatabasePath());
        var queue = new InMemoryTaskQueue();
        var service = PersistenceAndRecoveryHelpers.BuildReplayService(store, queue);
        var operatorCancelled = PersistenceAndRecoveryHelpers.Item(WorkItemState.Cancelled) with
        {
            CancellationReason = WorkItemCancellationReason.OperatorRequested,
            LastError = "cancelled by operator",
        };
        var legacyAmbiguousCancelled = PersistenceAndRecoveryHelpers.Item(WorkItemState.Cancelled) with
        {
            LastError = "cancelled",
        };
        await store.CreateAsync(operatorCancelled);
        await store.CreateAsync(legacyAmbiguousCancelled);

        await service.ReplayPendingForTestAsync(CancellationToken.None);

        Assert.Equal(0, queue.Count);
        Assert.Equal(WorkItemState.Cancelled, (await store.GetAsync(operatorCancelled.Id))!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, (await store.GetAsync(operatorCancelled.Id))!.CancellationReason);
        Assert.Equal(WorkItemState.Cancelled, (await store.GetAsync(legacyAmbiguousCancelled.Id))!.State);
        Assert.Null((await store.GetAsync(legacyAmbiguousCancelled.Id))!.CancellationReason);
    }
}

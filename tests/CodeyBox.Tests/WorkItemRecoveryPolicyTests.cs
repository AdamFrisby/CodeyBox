using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkItemRecoveryPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkingItem_WithEmptyOrWhitespaceCheckpoint_StillRequiresPipelinePreemptBeforeLifecycleTeardown(
        string preemptCheckpoint)
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            PreemptCheckpoint = preemptCheckpoint,
        };

        Assert.True(WorkItemRecoveryPolicy.RequiresPipelinePreemptCheckpointBeforeLifecycleTeardown(item));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CheckAndActWorkingItem_WithEmptyOrWhitespaceCheckpoint_IsRerunnableWithoutPreempt(
        string preemptCheckpoint)
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            JobType = JobType.CheckAndAct,
            PreemptCheckpoint = preemptCheckpoint,
        };

        Assert.True(WorkItemRecoveryPolicy.IsRerunnableCheckAndActWithoutPreempt(item));
    }

    [Theory]
    [InlineData(WorkItemState.Working, WorkItemState.Queued, true)]
    [InlineData(WorkItemState.Reworking, WorkItemState.WorkComplete, false)]
    [InlineData(WorkItemState.Auditing, WorkItemState.WorkComplete, false)]
    [InlineData(WorkItemState.ReworkingForConflict, WorkItemState.AuditPassed, false)]
    [InlineData(WorkItemState.Merging, WorkItemState.AuditPassed, false)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Merged, false)]
    [InlineData(WorkItemState.WorkComplete, WorkItemState.WorkComplete, false)]
    [InlineData(WorkItemState.AuditPassed, WorkItemState.AuditPassed, false)]
    [InlineData(WorkItemState.Merged, WorkItemState.Merged, false)]
    public void GracefulShutdownRecovery_MapsRecoverableStates(
        WorkItemState from,
        WorkItemState to,
        bool clearsStartedAt)
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            MakeItem(from) with { StartedAt = startedAt },
            DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(to, recovered!.State);
        Assert.Equal(clearsStartedAt ? null : startedAt, recovered.StartedAt);
    }

    [Fact]
    public void GracefulShutdownRecovery_WorkingWithPreemptCheckpoint_PreservesResumeState()
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PreemptCheckpoint = "refs/heads/codeybox/preempt/test",
        };

        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            item,
            DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.Working, recovered!.State);
        Assert.Null(recovered.StartedAt);
        Assert.Equal(item.PreemptCheckpoint, recovered.PreemptCheckpoint);
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Reworking)]
    public void InfrastructureDeferral_WithPreemptCheckpoint_PreservesCheckpointResumeState(
        WorkItemState state)
    {
        var item = MakeItem(state) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PreemptedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            PreemptCheckpoint = "refs/heads/codeybox/preempt/test",
            LastError = "prior error",
            FailureKind = "other",
        };

        var recovered = WorkItemRecoveryPolicy.BuildInfrastructureDeferredResumeState(
            item,
            DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(state, recovered!.State);
        Assert.Null(recovered.StartedAt);
        Assert.Equal(item.PreemptedAt, recovered.PreemptedAt);
        Assert.Equal(item.PreemptCheckpoint, recovered.PreemptCheckpoint);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.FailureKind);
    }

    [Fact]
    public void InfrastructureDeferral_NormalReworking_ResumesFromWorkComplete()
    {
        var item = MakeItem(WorkItemState.Reworking) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            WorkBranch = "codeybox/work",
            LastError = "prior error",
            FailureKind = "other",
        };

        var recovered = WorkItemRecoveryPolicy.BuildInfrastructureDeferredResumeState(
            item,
            DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.WorkComplete, recovered!.State);
        Assert.Equal(item.WorkBranch, recovered.WorkBranch);
        Assert.Null(recovered.PreemptCheckpoint);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.FailureKind);
    }

    [Fact]
    public void GracefulShutdownRecovery_SuspendedItem_IsLeftAlone()
    {
        var item = MakeItem(WorkItemState.Working) with { SuspendedVmName = "vm-1" };

        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            item,
            DateTimeOffset.UtcNow);

        Assert.Null(recovered);
    }

    private static WorkItem MakeItem(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
    };
}

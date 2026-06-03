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

    private static WorkItem MakeItem(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
    };
}

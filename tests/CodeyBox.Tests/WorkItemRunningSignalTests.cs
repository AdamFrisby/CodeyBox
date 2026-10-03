using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// The bound-worker signal: an item is running only while a worker holds
/// its row (<see cref="WorkItem.StartedAt"/> set, not terminal, not
/// parked). A durable agent-turn checkpoint retried into Working reports
/// not-running with a pending resume until a worker picks it up.
/// </summary>
public sealed class WorkItemRunningSignalTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WaitingResume_WorkingCheckpointWithoutStartedAt_IsNotRunningWithPendingResume()
    {
        var item = CheckpointItem(WorkItemState.Working, startedAt: null);

        Assert.False(WorkItemInFlight.IsRunning(item));
        Assert.True(WorkItemInFlight.HasPendingResume(item));
    }

    [Fact]
    public void PickedUpResume_WorkingCheckpointWithStartedAt_IsRunningWithoutPendingResume()
    {
        var item = CheckpointItem(WorkItemState.Working, startedAt: Now);

        Assert.True(WorkItemInFlight.IsRunning(item));
        Assert.False(WorkItemInFlight.HasPendingResume(item));
    }

    [Fact]
    public void WaitingResume_ReworkingCheckpointWithoutStartedAt_IsNotRunningWithPendingResume()
    {
        var item = CheckpointItem(WorkItemState.Reworking, startedAt: null);

        Assert.False(WorkItemInFlight.IsRunning(item));
        Assert.True(WorkItemInFlight.HasPendingResume(item));
    }

    [Fact]
    public void FreshWorking_WithoutCheckpointOrStartedAt_IsNotRunningWithoutPendingResume()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Working,
        };

        Assert.False(WorkItemInFlight.IsRunning(item));
        Assert.False(WorkItemInFlight.HasPendingResume(item));
    }

    [Fact]
    public void QueuedItem_IsNotRunningAndNotPendingResume()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };

        Assert.False(WorkItemInFlight.IsRunning(item));
        Assert.False(WorkItemInFlight.HasPendingResume(item));
    }

    [Fact]
    public void RetriedToMerged_WithoutCheckpoint_IsNotRunningWithoutPendingResume()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Merged,
        };

        Assert.False(WorkItemInFlight.IsRunning(item));
        Assert.False(WorkItemInFlight.HasPendingResume(item));
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    [InlineData(WorkItemState.NoActionRequired)]
    [InlineData(WorkItemState.NeedsOperatorInput)]
    [InlineData(WorkItemState.WaitingForQuotaReset)]
    [InlineData(WorkItemState.WaitingForAgentResume)]
    [InlineData(WorkItemState.WaitingForTransientRetry)]
    public void TerminalOrParked_WithStaleStartedAt_IsNotRunning(WorkItemState state)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = state,
            StartedAt = Now,
        };

        Assert.False(WorkItemInFlight.IsRunning(item));
        Assert.False(WorkItemInFlight.HasPendingResume(item));
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Auditing)]
    [InlineData(WorkItemState.Reworking)]
    [InlineData(WorkItemState.Merging)]
    [InlineData(WorkItemState.UpstreamPushing)]
    [InlineData(WorkItemState.Planning)]
    [InlineData(WorkItemState.Delegating)]
    public void HeldWorkerState_WithStartedAt_IsRunning(WorkItemState state)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = state,
            StartedAt = Now,
        };

        Assert.True(WorkItemInFlight.IsRunning(item));
        Assert.False(WorkItemInFlight.HasPendingResume(item));
    }

    private static WorkItem CheckpointItem(WorkItemState state, DateTimeOffset? startedAt)
    {
        var id = WorkItemId.New();
        const int promptRevision = 5;
        var phase = state == WorkItemState.Reworking
            ? AgentTurnResumePhase.Rework
            : AgentTurnResumePhase.Work;
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            PromptRevision = promptRevision,
            BaseBranch = "main",
            WorkBranch = "codeybox/test",
            State = state,
            Agent = AgentKind.Claude,
            AgentInstanceId = "claude/acct-a",
            StartedAt = startedAt,
            PreemptedAt = Now,
            PreemptCheckpoint = "refs/heads/codeybox/preempt-test",
            AgentTurnResumeCheckpoint = new AgentTurnResumeCheckpoint(
                AgentKind.Claude,
                "claude/acct-a",
                "claude-opus-4-7",
                "high",
                null,
                state,
                phase,
                phase == AgentTurnResumePhase.Rework ? 3 : null,
                promptRevision,
                Now,
                0),
        };
    }
}

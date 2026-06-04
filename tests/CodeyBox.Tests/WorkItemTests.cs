using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class WorkItemTests
{
    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        Agent = AgentKind.Claude,
    };

    [Fact]
    public void With_UpdatesStateAndTimestamp()
    {
        var item = Sample();
        var before = item.UpdatedAt;
        Thread.Sleep(2);
        var next = item.With(WorkItemState.Working);
        Assert.Equal(WorkItemState.Working, next.State);
        Assert.True(next.UpdatedAt >= before);
        Assert.Equal(item.Id, next.Id);
    }

    [Fact]
    public void With_RecordsLastError()
    {
        var item = Sample();
        var failed = item.With(WorkItemState.Failed, "boom");
        Assert.Equal("boom", failed.LastError);
    }

    [Fact]
    public void With_QueuedToQueuedPreservesResumeBranchAndFlag()
    {
        var item = Sample() with
        {
            State = WorkItemState.Queued,
            WorkBranch = "feature/operator-resume",
            PreserveWorkBranchOnQueuedPickup = true,
            StartedAt = DateTimeOffset.UtcNow,
        };

        var requeued = item.With(WorkItemState.Queued);

        Assert.Equal(WorkItemState.Queued, requeued.State);
        Assert.Equal("feature/operator-resume", requeued.WorkBranch);
        Assert.True(requeued.PreserveWorkBranchOnQueuedPickup);
        Assert.Null(requeued.StartedAt);
    }

    [Fact]
    public void With_RequeueClearsPreserveFlagWhenWorkBranchIsCleared()
    {
        var item = Sample() with
        {
            State = WorkItemState.Working,
            WorkBranch = "feature/operator-resume",
            PreserveWorkBranchOnQueuedPickup = true,
            StartedAt = DateTimeOffset.UtcNow,
        };

        var requeued = item.With(WorkItemState.Queued);

        Assert.Null(requeued.WorkBranch);
        Assert.False(requeued.PreserveWorkBranchOnQueuedPickup);
    }
}

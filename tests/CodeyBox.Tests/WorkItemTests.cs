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
}

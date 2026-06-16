using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

public sealed class WorkItemTerminalTransitionTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-terminal-transition-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task TransitionFailedAsync_WhenCurrentStateNoLongerMatchesExpectedStates_DoesNotFailItem()
    {
        using var store = new SqliteWorkItemStore(Path.Combine(_workspace, "state.db"));
        var webhooks = new CapturingWebhookDispatcher();
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("terminal-transition"),
            DisplayName = "Terminal transition",
            RepositoryUrl = "file:///tmp/terminal-transition",
            DefaultAgent = AgentKind.Claude,
        });
        var transition = new WorkItemTerminalTransition(
            store,
            webhooks,
            projects,
            NullLogger<WorkItemTerminalTransition>.Instance);
        var stale = NewTransientItem();
        await store.CreateAsync(stale);

        var retried = stale.With(WorkItemState.Queued, "retry resumed", failureKind: null) with
        {
            NextTransientRetryAt = null,
        };
        await store.UpdateAsync(retried);

        var result = await transition.TransitionFailedAsync(
            stale,
            "transient network auto-retry exhausted",
            new WorkItemTerminalFailureTransitionOptions
            {
                FailureKind = "transient-exhausted",
                ExpectedStates =
                [
                    WorkItemState.Failed,
                    WorkItemState.WaitingForTransientRetry,
                ],
            },
            CancellationToken.None);

        var stored = await store.GetAsync(stale.Id);
        Assert.False(result.Updated);
        Assert.Equal(WorkItemState.Queued, result.CurrentWorkItem?.State);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Null(stored.FailureKind);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "work_item.failed");
    }

    private static WorkItem NewTransientItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("terminal-transition"),
        Title = "Transient retry",
        Prompt = "retry after transient transport failure",
        State = WorkItemState.WaitingForTransientRetry,
        LastError = "Agent claude reported transient transport failure",
        FailureKind = "transient",
        PushUpstream = false,
    };
}

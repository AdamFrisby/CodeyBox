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
            new WorkItemTerminalFailureTransitionCommand
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

    [Fact]
    public async Task TransitionFailedAsync_WhenWebhookPublishHangs_ObservesCallerCancellation()
    {
        using var store = new SqliteWorkItemStore(Path.Combine(_workspace, "publish-timeout.db"));
        var webhooks = new BlockingWebhookDispatcher();
        var transition = new WorkItemTerminalTransition(
            store,
            webhooks,
            projects: null,
            NullLogger<WorkItemTerminalTransition>.Instance);
        var item = NewTransientItem();
        await store.CreateAsync(item);

        using var cts = new CancellationTokenSource();
        var transitionTask = transition.TransitionFailedAsync(
            item,
            "post-agent transition timeout",
            new WorkItemTerminalFailureTransitionCommand
            {
                FailureKind = "transient-exhausted",
            },
            cts.Token);

        await webhooks.WaitForPublishAsync();
        await cts.CancelAsync();

        var completed = await Task.WhenAny(transitionTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(transitionTask, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transitionTask);

        var stored = await store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
    }

    [Fact]
    public async Task TransitionFailedAsync_WhenExpectedUpdatedAtMismatches_DoesNotFailNewerRow()
    {
        using var store = new SqliteWorkItemStore(Path.Combine(_workspace, "updated-at-mismatch.db"));
        var transition = new WorkItemTerminalTransition(
            store,
            new CapturingWebhookDispatcher(),
            projects: null,
            NullLogger<WorkItemTerminalTransition>.Instance);
        var stale = NewTransientItem() with
        {
            NextTransientRetryAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            TransientRetryAttempts = 5,
            TransientRetryFirstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
        };
        await store.CreateAsync(stale);

        var newer = stale with
        {
            LastError = "newer retry state",
            UpdatedAt = stale.UpdatedAt.AddSeconds(1),
        };
        await store.UpdateAsync(newer);

        var result = await transition.TransitionFailedAsync(
            stale,
            "transient network auto-retry exhausted",
            new WorkItemTerminalFailureTransitionCommand
            {
                FailureKind = "transient-exhausted",
                ExpectedStates =
                [
                    WorkItemState.Failed,
                    WorkItemState.WaitingForTransientRetry,
                ],
                ExpectedUpdatedAt = stale.UpdatedAt,
                TransientRetryExhaustion = new WorkItemTransientRetryExhaustion(
                    "attempts=5; max=5",
                    stale.TransientRetryFirstFailedAt),
            },
            CancellationToken.None);

        var stored = await store.GetAsync(stale.Id);
        Assert.False(result.Updated);
        Assert.Equal(newer.UpdatedAt, result.CurrentWorkItem?.UpdatedAt);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.WaitingForTransientRetry, stored!.State);
        Assert.Equal("transient", stored.FailureKind);
        Assert.Equal("newer retry state", stored.LastError);
    }

    [Fact]
    public async Task TransitionFailedAsync_WhenProjectLookupHangs_ObservesCallerCancellation()
    {
        using var store = new SqliteWorkItemStore(Path.Combine(_workspace, "project-timeout.db"));
        var projects = new BlockingProjectRepository();
        var transition = new WorkItemTerminalTransition(
            store,
            new CapturingWebhookDispatcher(),
            projects,
            NullLogger<WorkItemTerminalTransition>.Instance);
        var item = NewTransientItem();
        await store.CreateAsync(item);

        using var cts = new CancellationTokenSource();
        var transitionTask = transition.TransitionFailedAsync(
            item,
            "post-agent transition timeout",
            new WorkItemTerminalFailureTransitionCommand
            {
                FailureKind = "transient-exhausted",
            },
            cts.Token);

        await projects.WaitForLookupAsync();
        await cts.CancelAsync();

        var completed = await Task.WhenAny(transitionTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(transitionTask, completed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transitionTask);

        var stored = await store.GetAsync(item.Id);
        Assert.NotNull(stored);
        Assert.Equal(WorkItemState.Failed, stored!.State);
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

    private sealed class BlockingWebhookDispatcher : IWebhookDispatcher
    {
        private readonly TaskCompletionSource _publishStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForPublishAsync() => _publishStarted.Task;

        public async Task PublishAsync(WebhookEvent evt, CancellationToken ct)
        {
            _ = evt;
            _publishStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
    }

    private sealed class BlockingProjectRepository : IProjectRepository
    {
        private readonly TaskCompletionSource _lookupStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForLookupAsync() => _lookupStarted.Task;

        public async Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default)
        {
            _ = id;
            _lookupStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        }

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult<IReadOnlyList<Project>>([]);
        }
    }
}

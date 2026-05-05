using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that when all work items linked to a Closed release reach terminal
/// state, OnWorkItemTerminalAsync automatically transitions the release to InReview.
/// </summary>
public sealed class AutoTransitionToInReviewTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"cb-atr-{Guid.NewGuid():N}.db");
    private readonly SqliteReleaseStore _releaseStore;
    private readonly SqliteWorkItemStore _workItemStore;
    private readonly CapturingWebhookDispatcher _webhooks = new();
    private readonly ReleaseService _svc;

    public AutoTransitionToInReviewTests()
    {
        _releaseStore = new SqliteReleaseStore(_dbPath);
        _workItemStore = new SqliteWorkItemStore(_dbPath);
        var projects = new InMemoryProjectRepository(ReleaseTestHelper.EnabledProject());
        _svc = ReleaseTestHelper.BuildService(_releaseStore, _workItemStore, projects, _webhooks);
    }

    public void Dispose()
    {
        _workItemStore.Dispose();
        _releaseStore.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task OnWorkItemTerminal_WhenAllItemsDone_BeginsReviewPhase()
    {
        var (rel, item) = await SeedClosedReleaseWithOneItemAsync(WorkItemState.Done);

        await _svc.OnWorkItemTerminalAsync(rel.Id, default);

        // Allow brief async propagation for the fire-and-forget deep audit task.
        await Task.Delay(200);

        var refreshed = await _releaseStore.GetAsync(rel.Id);
        // Review was started — state is InReview, Released, or Failed (never Closed or Abandoned).
        Assert.NotEqual(ReleaseState.Closed, refreshed!.State);
        Assert.NotNull(refreshed.ReviewStartedAt);
    }

    [Fact]
    public async Task OnWorkItemTerminal_WhenItemStillPending_DoesNotTransition()
    {
        var (rel, _) = await SeedClosedReleaseWithOneItemAsync(WorkItemState.Queued);

        await _svc.OnWorkItemTerminalAsync(rel.Id, default);

        var refreshed = await _releaseStore.GetAsync(rel.Id);
        Assert.Equal(ReleaseState.Closed, refreshed!.State);
    }

    [Fact]
    public async Task OnWorkItemTerminal_WhenReleaseIsOpen_DoesNotTransition()
    {
        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Open);
        await _releaseStore.CreateAsync(rel);
        var item = SeedWorkItem(rel.Id, WorkItemState.Done);
        await _workItemStore.CreateAsync(item);

        await _svc.OnWorkItemTerminalAsync(rel.Id, default);

        var refreshed = await _releaseStore.GetAsync(rel.Id);
        Assert.Equal(ReleaseState.Open, refreshed!.State);
    }

    [Fact]
    public async Task OnWorkItemTerminal_WhenAllItemsFailed_StillBeginsReview()
    {
        // Failed items are terminal — the deep-audit phase handles failed output.
        var (rel, _) = await SeedClosedReleaseWithOneItemAsync(WorkItemState.Failed);

        await _svc.OnWorkItemTerminalAsync(rel.Id, default);

        await Task.Delay(200);

        var refreshed = await _releaseStore.GetAsync(rel.Id);
        // Review was triggered — state advanced past Closed.
        Assert.NotEqual(ReleaseState.Closed, refreshed!.State);
        Assert.NotNull(refreshed.ReviewStartedAt);
    }

    private async Task<(Release, WorkItem)> SeedClosedReleaseWithOneItemAsync(WorkItemState itemState)
    {
        var rel = ReleaseTestHelper.SeedRelease(ReleaseState.Closed);
        await _releaseStore.CreateAsync(rel);
        var item = SeedWorkItem(rel.Id, itemState);
        await _workItemStore.CreateAsync(item);
        if (itemState != WorkItemState.Queued)
            await _workItemStore.UpdateAsync(item.With(itemState));
        return (rel, item);
    }

    private static WorkItem SeedWorkItem(ReleaseId releaseId, WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        Agent = AgentKind.Claude,
        ReleaseId = releaseId,
    };
}

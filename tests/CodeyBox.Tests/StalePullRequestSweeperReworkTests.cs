using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the stale-base remediation added to <see cref="StalePullRequestSweeper"/>:
/// a detected stale-base PR now drives the owning work item into the existing
/// conflict-rework state machine instead of only firing a notify signal. Wired
/// through the real <see cref="StaleBaseConflictReworkRouter"/> +
/// <see cref="WorkItemRetrier"/> + <see cref="SqliteWorkItemStore"/> +
/// <see cref="InMemoryTaskQueue"/> so the state transition and re-dispatch are
/// exercised for real (not asserted against mocks).
/// </summary>
public sealed class StalePullRequestSweeperReworkTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-stale-sweep-rework-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private static Project GitHubProject(string id = "sweep-rework") => new()
    {
        Id = new ProjectId(id),
        DisplayName = id,
        RepositoryUrl = "https://github.com/example/repo",
        Upstream = new ProjectUpstream
        {
            Kind = "github",
            GitHubOwner = "example",
            GitHubRepository = "repo",
            TokenEnvVar = "FAKE_TOKEN",
        },
    };

    private static UpstreamPullRequest DirtyPr(int number) => new()
    {
        Number = number,
        Url = $"https://github.com/example/repo/pull/{number}",
        HeadBranch = "codeybox/shipped",
        HeadSha = "tip",
        BaseBranch = "main",
        HasMergeConflict = true,
    };

    private static WorkItem ShippedItem(ProjectId projectId, int prNumber, int conflictAttempts = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = projectId,
        Title = "Shipped item",
        Prompt = "do work",
        BaseBranch = "main",
        WorkBranch = "codeybox/shipped",
        State = WorkItemState.Done,
        MergedPrNumber = prNumber,
        MergedPrUrl = $"https://github.com/example/repo/pull/{prNumber}",
        ConflictReworkAttempts = conflictAttempts,
    };

    [Fact]
    public async Task Sweep_StaleBaseWithConflict_RoutesOwningItemIntoConflictRework()
    {
        var project = GitHubProject();
        var pr = DirtyPr(112);
        using var store = NewStore();
        var item = ShippedItem(project.Id, pr.Number);
        await store.CreateAsync(item);

        var webhooks = new CapturingWebhookDispatcher();
        var (sweeper, queue) = BuildSweeper(project, pr, store, webhooks,
            options: Enabled(maxAttempts: 2));

        await sweeper.RunSweepAsync(CancellationToken.None);

        // Routed into rework — NOT left Done (re-audit loop) or merely notified.
        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.ReworkingForConflict, persisted!.State);
        Assert.Equal(1, persisted.ConflictReworkAttempts);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
        // The notify signal still fires (the sweeper keeps its detector role).
        Assert.Contains(webhooks.Events, e => e.Event == "upstream.pr_stale_base");
    }

    [Fact]
    public async Task Sweep_AttemptCapExhausted_ParksItemAtMergeConflictResolutionFailed()
    {
        var project = GitHubProject();
        var pr = DirtyPr(200);
        using var store = NewStore();
        // Already at the cap of 2 — the sweeper must park, not re-dispatch.
        var item = ShippedItem(project.Id, pr.Number, conflictAttempts: 2);
        await store.CreateAsync(item);

        var webhooks = new CapturingWebhookDispatcher();
        var (sweeper, queue) = BuildSweeper(project, pr, store, webhooks,
            options: Enabled(maxAttempts: 2));

        await sweeper.RunSweepAsync(CancellationToken.None);

        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, persisted!.State);
        // Not re-dispatched.
        Assert.Equal(2, persisted.ConflictReworkAttempts);
        Assert.Equal(0, queue.Count);
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.merge_conflict_resolution_failed");
    }

    [Fact]
    public async Task Sweep_RouteDisabled_PreservesNotifyOnlyBehaviour()
    {
        var project = GitHubProject();
        var pr = DirtyPr(9);
        using var store = NewStore();
        var item = ShippedItem(project.Id, pr.Number);
        await store.CreateAsync(item);

        var webhooks = new CapturingWebhookDispatcher();
        var (sweeper, queue) = BuildSweeper(project, pr, store, webhooks,
            options: new StalePullRequestSweeperOptions
            {
                Enabled = true,
                CheckInterval = TimeSpan.FromSeconds(30),
                BranchPrefix = "codeybox/",
                RouteToConflictRework = false,
            });

        await sweeper.RunSweepAsync(CancellationToken.None);

        // Notify-only: the item is untouched and nothing is re-dispatched.
        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, persisted!.State);
        Assert.Equal(0, persisted.ConflictReworkAttempts);
        Assert.Equal(0, queue.Count);
        var evt = Assert.Single(webhooks.Events);
        Assert.Equal("upstream.pr_stale_base", evt.Event);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static StalePullRequestSweeperOptions Enabled(int maxAttempts) => new()
    {
        Enabled = true,
        CheckInterval = TimeSpan.FromSeconds(30),
        BranchPrefix = "codeybox/",
        RouteToConflictRework = true,
        MaxReworkAttempts = maxAttempts,
    };

    private SqliteWorkItemStore NewStore()
        => new(Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db"));

    private static (StalePullRequestSweeper Sweeper, InMemoryTaskQueue Queue) BuildSweeper(
        Project project,
        UpstreamPullRequest pr,
        IWorkItemStore store,
        IWebhookDispatcher webhooks,
        StalePullRequestSweeperOptions options)
    {
        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(
            store, queue, new ReworkFakeGitHost(), NullLogger<WorkItemRetrier>.Instance);
        var router = new StaleBaseConflictReworkRouter(
            store, retrier, () => options, NullLogger<StaleBaseConflictReworkRouter>.Instance);
        var sweeper = new StalePullRequestSweeper(
            new InMemoryProjectRepository(project),
            new ScriptedUpstreamFactory(new ScriptedUpstreamRemote(new[] { pr })),
            webhooks,
            options,
            NullLogger<StalePullRequestSweeper>.Instance,
            time: null,
            store: store,
            reworkRouter: router);
        return (sweeper, queue);
    }
}

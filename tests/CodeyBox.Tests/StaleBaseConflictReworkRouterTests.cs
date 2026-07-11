using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="StaleBaseConflictReworkRouter"/>, wired through the real
/// <see cref="WorkItemRetrier"/>, a real <see cref="SqliteWorkItemStore"/>, and a
/// real <see cref="InMemoryTaskQueue"/> so the state transition + re-dispatch is
/// exercised end-to-end (no mocks of the code under test). A single-statement
/// regression in the router — dropping the reserve bump, inverting the cap
/// comparison, or ignoring the enable flag — flips one of these red.
/// </summary>
public sealed class StaleBaseConflictReworkRouterTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("stale-base-rework");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-stale-rework-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task TryRoute_UnderCap_TransitionsToReworkingForConflict_BumpsAttempts_AndEnqueues()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var router = NewRouter(store, queue, options: Enabled(maxAttempts: 2));
        var item = ShippedItem() with { ConflictReworkAttempts = 0 };
        await store.CreateAsync(item);

        var outcome = await router.TryRouteAsync(item, "test", CancellationToken.None);

        Assert.Equal(StaleBaseReworkOutcome.Routed, outcome);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.ReworkingForConflict, persisted!.State);
        // The attempt is reserved up-front so the counter advances across
        // successive re-dispatches (the merge-phase resume path would otherwise
        // decline to double-count it).
        Assert.Equal(1, persisted.ConflictReworkAttempts);
        // Re-dispatched: the dispatcher kick landed on the queue.
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TryRoute_AtCap_ReturnsCapExhausted_WithoutTouchingItem()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var router = NewRouter(store, queue, options: Enabled(maxAttempts: 2));
        var item = ShippedItem() with { ConflictReworkAttempts = 2 };
        await store.CreateAsync(item);

        var outcome = await router.TryRouteAsync(item, "test", CancellationToken.None);

        Assert.Equal(StaleBaseReworkOutcome.CapExhausted, outcome);
        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, persisted!.State);
        Assert.Equal(2, persisted.ConflictReworkAttempts);
    }

    [Fact]
    public async Task TryRoute_MaxAttemptsBelowOne_IsFlooredToOne()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        // A misconfigured cap of 0 must not disable rework entirely nor allow an
        // unbounded loop: it floors to 1, so an item with 1 attempt is exhausted.
        var router = NewRouter(store, queue, options: Enabled(maxAttempts: 0));
        Assert.Equal(1, router.MaxReworkAttempts);
        var item = ShippedItem() with { ConflictReworkAttempts = 1 };
        await store.CreateAsync(item);

        var outcome = await router.TryRouteAsync(item, "test", CancellationToken.None);

        Assert.Equal(StaleBaseReworkOutcome.CapExhausted, outcome);
    }

    [Fact]
    public async Task TryRoute_Disabled_ReturnsNotEnabled_AndLeavesItemUntouched()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var router = NewRouter(store, queue, options: new StalePullRequestSweeperOptions
        {
            RouteToConflictRework = false,
            MaxReworkAttempts = 2,
        });
        var item = ShippedItem();
        await store.CreateAsync(item);

        var outcome = await router.TryRouteAsync(item, "test", CancellationToken.None);

        Assert.Equal(StaleBaseReworkOutcome.NotEnabled, outcome);
        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, persisted!.State);
        Assert.Equal(0, persisted.ConflictReworkAttempts);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static StalePullRequestSweeperOptions Enabled(int maxAttempts) => new()
    {
        RouteToConflictRework = true,
        MaxReworkAttempts = maxAttempts,
    };

    private SqliteWorkItemStore NewStore()
        => new(Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db"));

    private static StaleBaseConflictReworkRouter NewRouter(
        IWorkItemStore store,
        ITaskQueue queue,
        StalePullRequestSweeperOptions options)
    {
        var retrier = new WorkItemRetrier(
            store,
            queue,
            new ReworkFakeGitHost(),
            NullLogger<WorkItemRetrier>.Instance);
        return new StaleBaseConflictReworkRouter(
            store,
            retrier,
            () => options,
            NullLogger<StaleBaseConflictReworkRouter>.Instance);
    }

    private static WorkItem ShippedItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "Shipped item with open PR",
        Prompt = "do the work",
        BaseBranch = "main",
        WorkBranch = "codeybox/shipped",
        State = WorkItemState.Done,
        MergedPrNumber = 314,
        MergedPrUrl = "https://github.com/example/repo/pull/314",
    };
}

/// <summary>
/// Minimal <see cref="IGitHost"/> for the retrier's post-work resume checks:
/// the conflict-rework resume path only probes repository + work-branch
/// existence, both of which report present here so no fallback to from=work
/// occurs. Mirrors the proven fake shape used by the retrier auto-pick tests.
/// </summary>
internal sealed class ReworkFakeGitHost : IGitHost
{
    public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
        => Task.FromResult(id.ToString());

    public Task<string> EnsureRepositoryAsync(
        WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
        => Task.FromResult(id.ToString());

    public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
        => throw new NotSupportedException();

    public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
        => Task.FromResult("main");

    public Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult((string.Empty, string.Empty));
}

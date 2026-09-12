using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Push-path coverage for stale-base remediation in
/// <see cref="PipelineRunner"/>. When the upstream push fails with a
/// non-fast-forward reconcile conflict (the PR base moved after the item's
/// merge), the pipeline must route the item into the existing conflict-rework
/// state machine instead of parking it as a generic infrastructure failure.
///
/// <para>Unlike <see cref="StaleBaseConflictReworkRouterTests"/> (router core)
/// and <see cref="StalePullRequestSweeperReworkTests"/> (out-of-band sweeper),
/// these tests drive the full <c>RunAsync</c> pipeline — work phase, merge
/// phase, then a scripted <see cref="IUpstreamRemote"/> whose
/// <c>CompleteAsync</c> throws <see cref="UpstreamPushReconcileConflictException"/> —
/// through real wiring (real <see cref="SqliteWorkItemStore"/>, real
/// <see cref="StaleBaseConflictReworkRouter"/> + <see cref="WorkItemRetrier"/>,
/// real local git host). Deleting the push-path call sites in
/// <c>RunUpstreamPushPhaseAsync</c> or inverting the <c>Routed</c> check flips
/// the routing test red.</para>
/// </summary>
[Collection("Pipeline integration")]
public sealed class StaleBasePushPathReworkTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-stale-push-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private static WorkItem NewItem(string workBranch, int conflictAttempts = 0) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "push-path stale base",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = true,
        ConflictReworkAttempts = conflictAttempts,
    };

    private static StalePullRequestSweeperOptions Enabled(int maxAttempts) => new()
    {
        RouteToConflictRework = true,
        MaxReworkAttempts = maxAttempts,
    };

    [Fact]
    public async Task PushReconcileConflict_RoutesItemIntoConflictRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var factory = new PushConflictUpstreamFactory();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "test-upstream", MergeMethod = "rebase" },
            upstreamFactory: factory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 3,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            staleBaseReworkOptions: Enabled(maxAttempts: 2));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("push-conflict.txt", "work content\n"));

        var item = NewItem("feature/push-stale-base");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Routed into rework — NOT parked as an infrastructure failure and NOT
        // left to livelock through re-audit. The reconcile conflict is not
        // retried against the moved base.
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.ReworkingForConflict, final!.State);
        Assert.Equal(1, final.ConflictReworkAttempts);
        Assert.Equal(1, factory.Remote.CompleteCalls);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PushReconcileConflict_AttemptCapExhausted_ParksAtMergeConflictResolutionFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var factory = new PushConflictUpstreamFactory();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "test-upstream", MergeMethod = "rebase" },
            upstreamFactory: factory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 3,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            staleBaseReworkOptions: Enabled(maxAttempts: 2));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("push-conflict.txt", "work content\n"));

        // Already at the cap: the push conflict must park at the merge-conflict
        // terminal (via MergeConflictResolutionFailedException) rather than
        // re-dispatching into another unbounded rework loop.
        var item = NewItem("feature/push-stale-base-capped", conflictAttempts: 2);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.MergeConflictResolutionFailed, final!.State);
        Assert.Equal(2, final.ConflictReworkAttempts);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task PushReconcileConflict_RouteDisabled_PreservesHistoricalPark()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var factory = new PushConflictUpstreamFactory();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "test-upstream", MergeMethod = "rebase" },
            upstreamFactory: factory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 3,
                UpstreamPushBackoff = TimeSpan.Zero,
            },
            staleBaseReworkOptions: new StalePullRequestSweeperOptions
            {
                RouteToConflictRework = false,
                MaxReworkAttempts = 2,
            });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("push-conflict.txt", "work content\n"));

        var item = NewItem("feature/push-stale-base-disabled");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // Notify-only era behaviour: generic infrastructure park, untouched
        // attempt counter, nothing re-dispatched.
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("upstream rebase conflict on main; manual resolution required", final.LastError);
        Assert.Equal(0, final.ConflictReworkAttempts);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task PushReconcileConflict_NullRouter_PreservesHistoricalPark()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var factory = new PushConflictUpstreamFactory();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            upstream: new ProjectUpstream { Kind = "test-upstream", MergeMethod = "rebase" },
            upstreamFactory: factory,
            pipelineOptions: new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
                UpstreamPushMaxAttempts = 3,
                UpstreamPushBackoff = TimeSpan.Zero,
            });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("push-conflict.txt", "work content\n"));

        var item = NewItem("feature/push-stale-base-no-router");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("upstream rebase conflict on main; manual resolution required", final.LastError);
        Assert.Equal(0, final.ConflictReworkAttempts);
        var queue = Assert.IsType<InMemoryTaskQueue>(tp.Queue);
        Assert.Equal(0, queue.Count);
    }
}

/// <summary>
/// Scripted upstream whose <c>CompleteAsync</c> always fails with a
/// non-fast-forward reconcile conflict — the push-time signature of a PR whose
/// base moved after the item's merge. Mirrors the proven fake shape used by
/// the existing reconcile-conflict pipeline test.
/// </summary>
internal sealed class PushConflictUpstreamFactory : IUpstreamRemoteFactory
{
    public PushConflictUpstreamRemote Remote { get; } = new();

    public IUpstreamRemote Create(Project project) => Remote;
}

internal sealed class PushConflictUpstreamRemote : IUpstreamRemote
{
    public int CompleteCalls { get; private set; }
    public string Name => "test-upstream";

    public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(false, "not used"));

    public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        CompleteCalls++;
        throw new UpstreamPushReconcileConflictException(request.BaseBranch, "rebase");
    }

    public Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch, string sourceBranch, CancellationToken ct = default)
        => Task.FromResult(true);
}

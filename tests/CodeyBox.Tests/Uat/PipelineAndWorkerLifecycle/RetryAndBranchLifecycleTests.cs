using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests.Uat.PipelineAndWorkerLifecycle;

/// <summary>
/// UAT coverage for retry entry points and work-branch lifecycle safety in the Pipeline And Worker Lifecycle section.
/// Plan anchors:
/// docs/uat/00-plan.md#retry-and-rework-entry-points---requeues-failed-work-from-selected-pipeline-phases
/// docs/uat/00-plan.md#work-branch-lifecycle-and-merge-safety---manages-branch-creation-rebase-on-base-merge-verification-and-conflict-scope
/// </summary>
[Collection("Pipeline integration")]
public sealed class RetryAndBranchLifecycleTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-uat-retry-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Theory]
    [InlineData("work", WorkItemState.Queued)]
    [InlineData("audit", WorkItemState.WorkComplete)]
    [InlineData("merge", WorkItemState.AuditPassed)]
    [InlineData("upstream", WorkItemState.Merged)]
    public async Task Retrier_MapsSupportedFromValuesToResumeStateAndEnqueues(string from, WorkItemState expected)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = PipelineLifecycleUatHelpers.NewItem("feature/retry-" + from) with
        {
            State = WorkItemState.Failed,
            LastError = "previous failure",
            RecoveryAttempts = 2,
        };
        await store.CreateAsync(item);
        await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, from);

        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.ResumeState);
        var stored = await store.GetAsync(item.Id);
        Assert.Equal(expected, stored!.State);
        Assert.Null(stored.LastError);
        Assert.Equal(0, stored.RecoveryAttempts);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryFromLaterPhaseWithoutBareRepository_IsRejectedAndNotEnqueued()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = PipelineLifecycleUatHelpers.NewItem("feature/missing-repo") with
        {
            State = WorkItemState.Failed,
        };
        await store.CreateAsync(item);
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, "merge");

        Assert.False(result.Success);
        Assert.Contains("bare repo", result.Error);
        Assert.Equal(0, queue.Count);
        Assert.Equal(WorkItemState.Failed, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task RetryConcurrentStateChange_IsRejectedWithoutClobberingNewerState()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var staleView = PipelineLifecycleUatHelpers.NewItem("feature/race") with
        {
            State = WorkItemState.Failed,
        };
        await store.CreateAsync(staleView);
        await gitHost.EnsureRepositoryAsync(staleView.Id, seed, staleView.BaseBranch);
        await store.UpdateAsync(staleView.With(WorkItemState.Done));
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(staleView, "audit");

        Assert.False(result.Success);
        Assert.Contains("state changed concurrently", result.Error);
        Assert.Equal(WorkItemState.Done, (await store.GetAsync(staleView.Id))!.State);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task QueuedOwnedWorkBranch_StartsFromBaseAndDoesNotReuseStaleAttemptCommits()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var context = PipelineLifecycleUatHelpers.BuildPipeline(_workspace, seed);
        var item = PipelineLifecycleUatHelpers.NewItem("placeholder") with
        {
            WorkBranch = null,
        };
        var ownedWorkBranch = "codeybox/" + item.Id.ToString()[..8];
        var repoId = await context.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = context.GitHost.GetRepoPath(repoId);
        await PipelineLifecycleUatHelpers.CommitToBareBranchAsync(
            _workspace,
            barePath,
            ownedWorkBranch,
            "stale.txt",
            "stale failed attempt\n",
            "stale attempt");
        var staleTip = await PipelineLifecycleUatHelpers.RevParseAsync(barePath, ownedWorkBranch);
        context.Agent.WorkPlan.Enqueue(new FileWrite("fresh.txt", "fresh retry\n"));
        await context.Store.CreateAsync(item);

        await context.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(ownedWorkBranch, final.WorkBranch);
        Assert.NotEqual(staleTip, await PipelineLifecycleUatHelpers.RevParseAsync(barePath, ownedWorkBranch));
        Assert.NotEqual(0, (await TestSupport.RunGitNoThrow(barePath, "show", $"{ownedWorkBranch}:stale.txt")).code);
        var staleOnMain = await TestSupport.RunGitNoThrow(barePath, "show", "main:stale.txt");
        Assert.NotEqual(0, staleOnMain.code);
        var (_, freshOnMain, _) = await TestSupport.RunGit(barePath, "show", "main:fresh.txt");
        Assert.Equal("fresh retry\n", freshOnMain);
    }

    [Fact]
    public async Task WorkBranchEqualToBaseBranch_FailsBeforeSandboxWorkRuns()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var context = PipelineLifecycleUatHelpers.BuildPipeline(_workspace, seed);
        context.Agent.WorkPlan.Enqueue(new FileWrite("should-not-run.txt", "not used\n"));
        var item = PipelineLifecycleUatHelpers.NewItem("main");
        await context.Store.CreateAsync(item);

        await context.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("must differ from baseBranch", final.LastError);
        Assert.Single(context.Agent.WorkPlan);
    }

    private SqliteWorkItemStore NewStore()
        => new(Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db"));

    private LocalGitHost NewGitHost()
        => new(
            new LocalGitHostOptions
            {
                RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]),
            },
            NullLogger<LocalGitHost>.Instance);
}

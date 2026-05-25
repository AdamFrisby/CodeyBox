using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

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
        var workBranch = "feature/retry-" + from;
        var item = PipelineLifecycleUatHelpers.NewItem(workBranch) with
        {
            State = WorkItemState.Failed,
            LastError = "previous failure",
            RecoveryAttempts = 2,
        };
        await store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        if (from != "work")
        {
            // Post-work resumes require the work branch to exist in the bare repo;
            // missing branches now auto-fall-back to from='work'.
            await PipelineLifecycleUatHelpers.CommitToBareBranchAsync(
                _workspace,
                gitHost.GetRepoPath(repoId),
                workBranch,
                "artifact.txt",
                "previous attempt artifact\n",
                "previous attempt");
        }
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, from);

        Assert.True(result.Success, result.Error);
        Assert.Equal(expected, result.ResumeState);
        Assert.Equal(from, result.ActualFrom);
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
    public async Task RetryWithoutFrom_WhenWorkBranchHasPriorCommits_AutoPicksAudit()
    {
        // Acceptance criterion for the "/retry without from=" smart-default:
        // a Failed item whose work branch carries prior-iteration commits
        // must resume from the audit phase, not from work — otherwise the
        // agent re-runs on a tree that already looks done, exits with zero
        // diff, and gets misclassified as "no changes to commit".
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        const string workBranch = "codeybox/priorcommits";
        var item = PipelineLifecycleUatHelpers.NewItem(workBranch) with
        {
            State = WorkItemState.Failed,
            LastError = "previous iteration failure",
        };
        await store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await AddCommitsToBareBranchAsync(_workspace, barePath, workBranch, "main", count: 3);
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.WorkComplete, result.ResumeState);
        Assert.Equal("audit", result.ActualFrom);
        var stored = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, stored!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryWithoutFrom_WhenWorkBranchHasNoCommits_AutoPicksWork()
    {
        // The companion case: a Failed item whose work branch never got a
        // commit (work phase died before the agent pushed anything) must
        // resume from work — there's nothing on the branch to audit, so a
        // from=audit pick would crash the pipeline with a "branch missing"
        // error or audit an empty diff.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = PipelineLifecycleUatHelpers.NewItem("codeybox/nocommits") with
        {
            State = WorkItemState.Failed,
            LastError = "agent killed before first commit",
        };
        await store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await CreateBareBranchAtAsync(gitHost.GetRepoPath(repoId), item.WorkBranch!, item.BaseBranch!);
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        var stored = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryWithoutFrom_WhenItemBaseBranchIsNull_UsesProjectDefaultBaseBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await TestSupport.RunGit(seed, "checkout", "-b", "develop");
        await File.WriteAllTextAsync(Path.Combine(seed, "develop.txt"), "project default base\n");
        await TestSupport.RunGit(seed, "add", "develop.txt");
        await TestSupport.RunGit(seed, "commit", "-m", $"develop base\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(seed, "checkout", "main");

        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        const string workBranch = "codeybox/project-default-base";
        var item = PipelineLifecycleUatHelpers.NewItem(workBranch) with
        {
            BaseBranch = null,
            State = WorkItemState.Failed,
            LastError = "agent killed before first commit",
        };
        await store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, "develop");
        await CreateBareBranchAtAsync(gitHost.GetRepoPath(repoId), workBranch, "develop");
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = item.ProjectId,
            DisplayName = "Retry UAT",
            RepositoryUrl = seed,
            DefaultBaseBranch = "develop",
        });
        var retrier = new WorkItemRetrier(
            store,
            queue,
            gitHost,
            NullLogger<WorkItemRetrier>.Instance,
            projects: projects);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task RetryWithoutFrom_WhenBranchProbeFails_DoesNotMutateOrEnqueue()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var item = PipelineLifecycleUatHelpers.NewItem("codeybox/probe-failure") with
        {
            State = WorkItemState.Failed,
            LastError = "previous failure",
        };
        await store.CreateAsync(item);
        var retrier = new WorkItemRetrier(
            store,
            queue,
            new ThrowingBranchProbeGitHost(),
            NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.False(result.Success);
        Assert.Contains("cannot auto-pick retry phase", result.Error);
        var stored = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, stored!.State);
        Assert.Equal("previous failure", stored.LastError);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RetryEndpointWithoutBody_WhenWorkBranchHasPriorCommits_AutoPicksAudit()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = new Project
        {
            Id = PipelineLifecycleUatHelpers.TestProjectId,
            DisplayName = "Retry endpoint UAT",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
        };
        using var factory = new WorkItemApiFactory(null, project);
        using var client = factory.CreateClient();
        var gitHost = factory.Services.GetRequiredService<IGitHost>();
        const string workBranch = "codeybox/http-priorcommits";
        var item = PipelineLifecycleUatHelpers.NewItem(workBranch) with
        {
            State = WorkItemState.Failed,
            LastError = "previous iteration failure",
        };
        await factory.Store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await AddCommitsToBareBranchAsync(_workspace, gitHost.GetRepoPath(repoId), workBranch, "main", count: 3);

        var response = await client.PostAsync($"/workitems/{item.Id}/retry", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("auto", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("audit", body.RootElement.GetProperty("actualFrom").GetString());
        Assert.Equal("WorkComplete", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(WorkItemState.WorkComplete, (await factory.Store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task RetryEndpointWithExplicitFromWork_OverridesAutoPickWhenWorkBranchHasPriorCommits()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = new Project
        {
            Id = PipelineLifecycleUatHelpers.TestProjectId,
            DisplayName = "Retry endpoint UAT",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
        };
        using var factory = new WorkItemApiFactory(null, project);
        using var client = factory.CreateClient();
        var gitHost = factory.Services.GetRequiredService<IGitHost>();
        const string workBranch = "codeybox/http-explicit-work";
        var item = PipelineLifecycleUatHelpers.NewItem(workBranch) with
        {
            State = WorkItemState.Failed,
            LastError = "previous iteration failure",
        };
        await factory.Store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await AddCommitsToBareBranchAsync(_workspace, gitHost.GetRepoPath(repoId), workBranch, "main", count: 3);

        var response = await client.PostAsJsonAsync($"/workitems/{item.Id}/retry", new { from = "work" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("work", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("work", body.RootElement.GetProperty("actualFrom").GetString());
        Assert.Equal("Queued", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(WorkItemState.Queued, (await factory.Store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task RetryWithoutFrom_WhenAutoPicksAudit_CanRunPipelineToDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var context = PipelineLifecycleUatHelpers.BuildPipeline(
            _workspace,
            seed,
            auditors: [new PassingAuditor()]);
        const string workBranch = "codeybox/autopick-audit-done";
        var item = PipelineLifecycleUatHelpers.NewItem(workBranch) with
        {
            State = WorkItemState.Failed,
            LastError = "previous iteration failure",
        };
        await context.Store.CreateAsync(item);
        var repoId = await context.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        await AddCommitsToBareBranchAsync(_workspace, context.GitHost.GetRepoPath(repoId), workBranch, "main", count: 3);
        var retrier = new WorkItemRetrier(
            context.Store,
            new InMemoryTaskQueue(),
            context.GitHost,
            NullLogger<WorkItemRetrier>.Instance);

        var retry = await retrier.RetryAsync(item, from: null);
        await context.Pipeline.RunAsync((await context.Store.GetAsync(item.Id))!, CancellationToken.None);

        Assert.True(retry.Success, retry.Error);
        Assert.Equal("audit", retry.ActualFrom);
        var final = await context.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var (_, artifact, _) = await TestSupport.RunGit(
            context.GitHost.GetRepoPath(repoId),
            "show",
            "main:artifact-2.txt");
        Assert.Equal("prior iteration commit 2\n", artifact);
    }

    [Fact]
    public async Task RetryEndpointWithoutBody_WhenWorkBranchExistsAtProjectBase_AutoPicksWork()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        await TestSupport.RunGit(seed, "checkout", "-b", "develop");
        await File.WriteAllTextAsync(Path.Combine(seed, "develop.txt"), "project default base\n");
        await TestSupport.RunGit(seed, "add", "develop.txt");
        await TestSupport.RunGit(seed, "commit", "-m", $"develop base\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(seed, "checkout", "main");
        var project = new Project
        {
            Id = PipelineLifecycleUatHelpers.TestProjectId,
            DisplayName = "Retry endpoint UAT",
            RepositoryUrl = seed,
            DefaultBaseBranch = "develop",
        };
        using var factory = new WorkItemApiFactory(null, project);
        using var client = factory.CreateClient();
        var gitHost = factory.Services.GetRequiredService<IGitHost>();
        const string workBranch = "codeybox/http-nocommits";
        var item = PipelineLifecycleUatHelpers.NewItem(workBranch) with
        {
            BaseBranch = null,
            State = WorkItemState.Failed,
            LastError = "agent killed before first commit",
        };
        await factory.Store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, "develop");
        await CreateBareBranchAtAsync(gitHost.GetRepoPath(repoId), workBranch, "develop");

        var response = await client.PostAsync($"/workitems/{item.Id}/retry", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("auto", body.RootElement.GetProperty("from").GetString());
        Assert.Equal("work", body.RootElement.GetProperty("actualFrom").GetString());
        Assert.Equal("Queued", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(WorkItemState.Queued, (await factory.Store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task RetryFromAuditWithMissingWorkBranch_FallsBackToWorkAndEnqueues()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        // Item has WorkBranch set (the work phase recorded it before failing),
        // but the bare repo never received a commit on that branch.
        var item = PipelineLifecycleUatHelpers.NewItem("codeybox/missingbr") with
        {
            State = WorkItemState.Failed,
            LastError = "agent killed before first commit",
        };
        await store.CreateAsync(item);
        await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, "audit");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        var stored = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, stored!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryFromAuditWithExistingWorkBranch_BehavesAsToday()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        const string workBranch = "codeybox/withcommit";
        var item = PipelineLifecycleUatHelpers.NewItem(workBranch) with
        {
            State = WorkItemState.AuditFailed,
        };
        await store.CreateAsync(item);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var barePath = gitHost.GetRepoPath(repoId);
        await PipelineLifecycleUatHelpers.CommitToBareBranchAsync(
            _workspace,
            barePath,
            workBranch,
            "work.txt",
            "previous attempt artifact\n",
            "previous attempt");
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, "audit");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.WorkComplete, result.ResumeState);
        Assert.Equal("audit", result.ActualFrom);
        var stored = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, stored!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryFromWorkIsUnaffectedByBranchExistenceCheck()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = PipelineLifecycleUatHelpers.NewItem("codeybox/noworkbr") with
        {
            State = WorkItemState.Failed,
        };
        await store.CreateAsync(item);
        await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var retrier = new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);

        var result = await retrier.RetryAsync(item, "work");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
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

    private static async Task CreateBareBranchAtAsync(string barePath, string branch, string baseBranch)
    {
        await TestSupport.RunGit(barePath, "update-ref", $"refs/heads/{branch}", $"refs/heads/{baseBranch}");
    }

    private static async Task AddCommitsToBareBranchAsync(
        string workspace,
        string barePath,
        string branch,
        string baseBranch,
        int count)
    {
        var clone = Path.Combine(workspace, "stack-edit-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch, $"origin/{baseBranch}");
        for (var i = 0; i < count; i++)
        {
            var fileName = $"artifact-{i}.txt";
            await File.WriteAllTextAsync(Path.Combine(clone, fileName), $"prior iteration commit {i}\n");
            await TestSupport.RunGit(clone, "add", fileName);
            await TestSupport.RunGit(clone, "commit", "-m", $"prior commit {i}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        }
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
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

    private sealed class ThrowingBranchProbeGitHost : IGitHost
    {
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => Task.FromResult(id.ToString());

        public Task<string> EnsureRepositoryAsync(
            WorkItemId id,
            string? seedFromUrl,
            string? baseBranch,
            CancellationToken ct = default)
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
            => throw new NotSupportedException();

        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> BranchHasCommitsAheadAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => throw new InvalidOperationException("branch probe failed");

        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => Task.FromResult((string.Empty, string.Empty));
    }
}

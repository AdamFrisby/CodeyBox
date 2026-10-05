using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Lease-guard behavior at the pipeline and operator layers: a lease
/// mismatch parks the item exactly once (no retry loop, no conflict-rework
/// routing, non-retryable failure kind), the re-drive scan surfaces stranded
/// items, and an authorized re-drive records the observed tip as the lease
/// base before resuming the upstream phase.
/// </summary>
public sealed class UpstreamLeasePipelineTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-lease-pipe-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task LeaseMismatch_PipelineParksWithoutRetryOrRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var remote = new LeaseMismatchUpstreamRemote { SeedRepoPath = seed };
        var factory = new LeaseMismatchRemoteFactory(remote);

        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            upstream: new ProjectUpstream
            {
                Kind = "lease-mismatch-upstream",
                AutoMerge = true,
                MergeMethod = "squash",
            },
            upstreamFactory: factory,
            mergeStrategy: [MergeStrategy.RealMerge]);
        remote.BareRepoRoot = tp.GitRoot;
        tp.Agent.WorkPlan.Enqueue(new FileWrite("lease-fix.txt", "lease fix\n"));

        var item = NewItem("codeybox/lease-park-test");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = (await tp.Store.GetAsync(item.Id))!;
        Assert.Equal(WorkItemState.Failed, final.State);
        Assert.Equal(WorkItemFailureKinds.UpstreamBlocked, final.FailureKind);
        Assert.Equal(1, remote.CompleteCalls);
        Assert.Equal(1, final.UpstreamPushAttempts);
        Assert.Equal(0, final.ConflictReworkAttempts);
        Assert.Contains("refusing to overwrite", final.LastError);
        Assert.Single(remote.Requests);
        // The first attempt carries no lease base: nothing was ever pushed.
        Assert.Null(remote.Requests[0].ExpectedRemoteHeadSha);
    }

    [Fact]
    public async Task RedriveScan_SurfacesLeaseBlockedItemWithOpenPr()
    {
        using var fixture = CreateRedriveFixture();
        var project = fixture.Project;
        var item = NewItem("codeybox/stranded-item", project.Id);
        item = item.With(WorkItemState.Failed, "refusing to overwrite work branch", failureKind: WorkItemFailureKinds.UpstreamBlocked)
            with { MergedPrNumber = 612, MergedPrUrl = "https://github.com/o/r/pull/612" };
        await fixture.Store.CreateAsync(item);
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/stranded-item",
            HeadSha = new string('b', 40),
            BaseBranch = "main",
            HasMergeConflict = false,
        });

        var candidates = await fixture.Redrive.ListCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(item.Id.ToString(), candidate.WorkItemId);
        Assert.Equal(612, candidate.PullRequestNumber);
        Assert.Contains("lease", candidate.Reason);
    }

    [Fact]
    public async Task RedriveScan_ExcludesDoneItemsAndForeignBranches()
    {
        using var fixture = CreateRedriveFixture();
        var done = NewItem("codeybox/done-item", fixture.Project.Id);
        done = done.With(WorkItemState.Done) with { MergedPrNumber = 612 };
        await fixture.Store.CreateAsync(done, CancellationToken.None);
        var foreign = NewItem("codeybox/other-item", fixture.Project.Id);
        foreign = foreign.With(WorkItemState.Failed, "boom", failureKind: WorkItemFailureKinds.UpstreamBlocked)
            with { MergedPrNumber = 613 };
        await fixture.Store.CreateAsync(foreign, CancellationToken.None);
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/done-item",
            HeadSha = new string('b', 40),
            BaseBranch = "main",
            HasMergeConflict = false,
        });
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 613,
            Url = "https://github.com/o/r/pull/613",
            HeadBranch = "codeybox/third-party-branch",
            HeadSha = new string('c', 40),
            BaseBranch = "main",
            HasMergeConflict = false,
        });

        var candidates = await fixture.Redrive.ListCandidatesAsync(CancellationToken.None);

        Assert.Empty(candidates);
    }

    [Fact]
    public async Task RedriveAsync_WithoutOpenPr_Refuses()
    {
        using var fixture = CreateRedriveFixture();
        var item = NewItem("codeybox/stranded-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "refusing to overwrite", failureKind: WorkItemFailureKinds.UpstreamBlocked)
            with { MergedPrNumber = 612 };
        await fixture.Store.CreateAsync(item);

        var result = await fixture.Redrive.RedriveAsync(item.Id, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("no open PR #612", result.Error);
    }

    [Fact]
    public async Task RedriveAsync_WithOpenPr_RecordsLeaseBaseBeforeResume()
    {
        using var fixture = CreateRedriveFixture();
        var item = NewItem("codeybox/stranded-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "refusing to overwrite", failureKind: WorkItemFailureKinds.UpstreamBlocked)
            with { MergedPrNumber = 612 };
        await fixture.Store.CreateAsync(item);
        var headSha = new string('d', 40);
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/stranded-item",
            HeadSha = headSha,
            BaseBranch = "main",
            HasMergeConflict = false,
        });

        // The bare repo is gone in this fixture, so the resume itself is
        // refused — but the operator authorization (recording the observed
        // tip as the lease base) must already be persisted.
        var result = await fixture.Redrive.RedriveAsync(item.Id, CancellationToken.None);

        Assert.False(result.Success);
        var stored = (await fixture.Store.GetAsync(item.Id))!;
        Assert.Equal(headSha, stored.LastPushedWorkBranchSha);
    }

    private static WorkItem NewItem(string branch, ProjectId? projectId = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = projectId ?? new ProjectId("test-project"),
        Title = "lease test item",
        Prompt = "do the thing",
        BaseBranch = "main",
        WorkBranch = branch,
        PushUpstream = true,
    };

    private RedriveFixture CreateRedriveFixture()
    {
        var dbPath = Path.Combine(_workspace, "redrive-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var store = new SqliteWorkItemStore(dbPath);
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "test",
            RepositoryUrl = "https://example.invalid/o/r.git",
        };
        var projects = new StubProjectRepository(project);
        var remote = new StubListingUpstreamRemote();
        var retrier = new WorkItemRetrier(
            store,
            new StubTaskQueue(),
            new LocalGitHost(
                new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "git-" + Guid.NewGuid().ToString("N")[..8]) },
                NullLogger<LocalGitHost>.Instance),
            NullLogger<WorkItemRetrier>.Instance);
        var service = new UpstreamRedriveService(
            store, projects, new StubRemoteFactory(remote), retrier,
            NullLogger<UpstreamRedriveService>.Instance);
        return new RedriveFixture(store, project, remote, service);
    }

    private sealed record RedriveFixture(
        SqliteWorkItemStore Store,
        Project Project,
        StubListingUpstreamRemote Remote,
        UpstreamRedriveService Redrive) : IDisposable
    {
        public void Dispose() => Store.Dispose();
    }

    private sealed class StubProjectRepository(Project project) : IProjectRepository
    {
        public Task<Project?> GetAsync(ProjectId id, CancellationToken ct = default) =>
            Task.FromResult<Project?>(id == project.Id ? project : null);

        public Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Project>>([project]);
    }

    private sealed class StubRemoteFactory(IUpstreamRemote remote) : IUpstreamRemoteFactory
    {
        public IUpstreamRemote Create(Project project) => remote;
    }

    private sealed class StubListingUpstreamRemote : IUpstreamRemote
    {
        public List<UpstreamPullRequest> OpenPullRequests { get; } = new();
        public string Name => "stub-listing-upstream";

        public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(new UpstreamPushResult(true, null));

        public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(new UpstreamCompletionOutcome { BranchPushed = true });

        public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(string branchPrefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UpstreamPullRequest>>(
                OpenPullRequests.Where(p => p.HeadBranch.StartsWith(branchPrefix, StringComparison.Ordinal)).ToList());
    }

    private sealed class StubTaskQueue : ITaskQueue
    {
        public int Count => 0;
        public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default) => ValueTask.FromResult<WorkItemId?>(null);
    }

    private sealed class LeaseMismatchUpstreamRemote : IUpstreamRemote
    {
        public required string SeedRepoPath { get; init; }
        public string? BareRepoRoot { get; set; }
        public List<UpstreamCompletionRequest> Requests { get; } = new();
        public int CompleteCalls { get; private set; }
        public string Name => "lease-mismatch-upstream";

        public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(new UpstreamPushResult(true, null));

        public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            CompleteCalls++;
            throw new UpstreamLeaseMismatchException(
                request.WorkBranch,
                request.ExpectedRemoteHeadSha ?? "(none)",
                new string('e', 40));
        }

        public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
            => Task.FromResult(true);

        public async Task<string?> FetchBaseBranchAsync(string repositoryId, string baseBranch, CancellationToken ct = default)
        {
            var barePath = Path.Combine(
                BareRepoRoot ?? throw new InvalidOperationException("BareRepoRoot not wired"),
                repositoryId + ".git");
            await TestSupport.RunGit(
                barePath, "fetch", "--no-tags",
                SeedRepoPath, $"+refs/heads/{baseBranch}:refs/heads/{baseBranch}");
            var (_, sha, _) = await TestSupport.RunGit(
                barePath, "rev-parse", "--verify", $"refs/heads/{baseBranch}^{{commit}}");
            return sha.Trim();
        }
    }

    private sealed class LeaseMismatchRemoteFactory(LeaseMismatchUpstreamRemote remote) : IUpstreamRemoteFactory
    {
        public IUpstreamRemote Create(Project project) => remote;
    }
}

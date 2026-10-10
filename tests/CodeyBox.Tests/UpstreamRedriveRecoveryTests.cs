using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Upstream re-drive recovery for legacy items: PR discovery by head branch
/// when no number was recorded, the push-alone path when no PR exists, the
/// commit-trailer ownership proof for unrecorded pushes (refused unless the
/// operator passes <c>confirmOwnership</c>), and candidate listing for items
/// the recorded-number join would miss.
/// </summary>
public sealed class UpstreamRedriveRecoveryTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-redrive-recovery-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    private const string TrailerMessage =
        "previous attempt head\n\n" +
        "CodeyBox-WorkItem: 6f2c9a1e3b4d5f6a7b8c9d0e1f2a3b4c\n" +
        "Co-Authored-By: CodeyBox <noreply@codeybox.invalid>";

    [Fact]
    public async Task RedriveAsync_DiscoversPrByBranch_RecordsNumberAndLeaseBase()
    {
        using var fixture = CreateFixture();
        var item = NewItem("codeybox/legacy-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "upstream push diverged", failureKind: WorkItemFailureKinds.UpstreamBlocked);
        await fixture.Store.CreateAsync(item, CancellationToken.None);
        await EnsureHostBranchAsync(fixture, item);
        var headSha = new string('e', 40);
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/legacy-item",
            HeadSha = headSha,
            BaseBranch = "main",
            HasMergeConflict = true,
        });
        fixture.Remote.BranchCommitMessages = [TrailerMessage];

        var result = await fixture.Redrive.RedriveAsync(item.Id, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(612, result.PullRequestNumber);
        Assert.Equal(headSha, result.LeaseBaseSha);
        var stored = (await fixture.Store.GetAsync(item.Id, CancellationToken.None))!;
        Assert.Equal(612, stored.MergedPrNumber);
        Assert.Equal("https://github.com/o/r/pull/612", stored.MergedPrUrl);
        Assert.Equal(headSha, stored.LastPushedWorkBranchSha);
    }

    [Fact]
    public async Task RedriveAsync_ForeignTip_RefusedUnlessConfirmOwnership()
    {
        using var fixture = CreateFixture();
        var item = NewItem("codeybox/legacy-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "upstream push diverged", failureKind: WorkItemFailureKinds.UpstreamBlocked);
        await fixture.Store.CreateAsync(item, CancellationToken.None);
        var headSha = new string('f', 40);
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/legacy-item",
            HeadSha = headSha,
            BaseBranch = "main",
            HasMergeConflict = true,
        });
        fixture.Remote.BranchCommitMessages = ["third party change"];

        var refused = await fixture.Redrive.RedriveAsync(item.Id, CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Contains("confirmOwnership", refused.Error);
        var unarmed = (await fixture.Store.GetAsync(item.Id, CancellationToken.None))!;
        Assert.Null(unarmed.MergedPrNumber);
        Assert.Null(unarmed.LastPushedWorkBranchSha);

        var confirmed = await fixture.Redrive.RedriveAsync(item.Id, confirmOwnership: true, CancellationToken.None);

        Assert.DoesNotContain("confirmOwnership", confirmed.Error ?? string.Empty);
        var stored = (await fixture.Store.GetAsync(item.Id, CancellationToken.None))!;
        Assert.Equal(612, stored.MergedPrNumber);
        Assert.Equal(headSha, stored.LastPushedWorkBranchSha);
    }

    [Fact]
    public async Task RedriveAsync_MergeFromMainPlusTraileredWork_AcceptedWithoutConfirmation()
    {
        // Regression: branches that merged main and carry trailered work in
        // every writer's own subset were refused because the proof demanded
        // every configured key on every commit.
        using var fixture = CreateFixture();
        var item = NewItem("codeybox/legacy-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "upstream push diverged", failureKind: WorkItemFailureKinds.UpstreamBlocked);
        await fixture.Store.CreateAsync(item, CancellationToken.None);
        await EnsureHostBranchAsync(fixture, item);
        var baseTip = new string('1', 40);
        var headSha = new string('e', 40);
        fixture.Remote.BranchTips["main"] = baseTip;
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/legacy-item",
            HeadSha = headSha,
            BaseBranch = "main",
            HasMergeConflict = true,
        });
        var mergeSha = new string('2', 40);
        var agentSha = new string('3', 40);
        var mechanicalSha = new string('4', 40);
        fixture.Remote.BranchCommits =
        [
            // Orchestrator merge commit: WorkItem + Agent + Co-Authored-By.
            new OwnedBranchCommit(
                mergeSha,
                "codeybox: merge codeybox/legacy-item\n\n" +
                "CodeyBox-WorkItem: 6f2c9a1e3b4d5f6a7b8c9d0e1f2a3b4c\n" +
                "CodeyBox-Agent: test-agent\n" +
                "Co-Authored-By: CodeyBox <noreply@codeybox.invalid>"),
            // Agent work commit: Prompt-Revision + Co-Authored-By only (no WorkItem).
            new OwnedBranchCommit(
                agentSha,
                "agent work\n\nDo the thing.\n\n" +
                "CodeyBox-Prompt-Revision: 7\n" +
                "Co-Authored-By: CodeyBox <noreply@codeybox.invalid>"),
            // Mechanical fixer commit: Mechanical-Fixer only.
            new OwnedBranchCommit(
                mechanicalSha,
                "normalize whitespace\n\nCodeyBox-Mechanical-Fixer: whitespace"),
        ];

        var result = await fixture.Redrive.RedriveAsync(item.Id, CancellationToken.None);

        Assert.True(result.Success);
        // The range was pinned to the current base tip, not the branch name.
        Assert.Equal(baseTip, fixture.Remote.LastBaseRevision);
    }

    [Fact]
    public async Task RedriveAsync_UntraileredCommitOffBase_RefusedNamingSha()
    {
        using var fixture = CreateFixture();
        var item = NewItem("codeybox/legacy-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "upstream push diverged", failureKind: WorkItemFailureKinds.UpstreamBlocked);
        await fixture.Store.CreateAsync(item, CancellationToken.None);
        var baseTip = new string('1', 40);
        var headSha = new string('f', 40);
        fixture.Remote.BranchTips["main"] = baseTip;
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/legacy-item",
            HeadSha = headSha,
            BaseBranch = "main",
            HasMergeConflict = true,
        });
        var ownedSha = new string('a', 40);
        var foreignSha = new string('b', 40);
        fixture.Remote.BranchCommits =
        [
            new OwnedBranchCommit(
                ownedSha,
                "agent work\n\nCodeyBox-Prompt-Revision: 7\nCo-Authored-By: CodeyBox <noreply@codeybox.invalid>"),
            new OwnedBranchCommit(foreignSha, "operator hotfix without trailers"),
        ];

        var refused = await fixture.Redrive.RedriveAsync(item.Id, CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Contains(foreignSha, refused.Error);
        Assert.DoesNotContain(ownedSha, refused.Error);
        Assert.Contains("confirmOwnership", refused.Error);
    }

    [Fact]
    public async Task RedriveAsync_TrailersDisabledByConfig_RequiresConfirmOwnership()
    {
        using var fixture = CreateFixture(new CommitAttribution(
            includeCoAuthoredBy: false, includeCodeyBoxTrailers: false, includePullRequestFooter: true));
        var item = NewItem("codeybox/legacy-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "upstream push diverged", failureKind: WorkItemFailureKinds.UpstreamBlocked);
        await fixture.Store.CreateAsync(item, CancellationToken.None);
        await EnsureHostBranchAsync(fixture, item);
        var baseTip = new string('1', 40);
        var headSha = new string('e', 40);
        fixture.Remote.BranchTips["main"] = baseTip;
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/legacy-item",
            HeadSha = headSha,
            BaseBranch = "main",
            HasMergeConflict = true,
        });
        fixture.Remote.BranchCommits =
        [
            new OwnedBranchCommit(
                new string('a', 40),
                "agent work\n\n" +
                "CodeyBox-WorkItem: 6f2c9a1e3b4d5f6a7b8c9d0e1f2a3b4c\n" +
                "Co-Authored-By: CodeyBox <noreply@codeybox.invalid>"),
        ];

        var refused = await fixture.Redrive.RedriveAsync(item.Id, CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Contains("confirmOwnership", refused.Error);

        var confirmed = await fixture.Redrive.RedriveAsync(item.Id, confirmOwnership: true, CancellationToken.None);

        Assert.True(confirmed.Success);
    }

    [Fact]
    public async Task RedriveAsync_NoPrAnywhere_PushesAloneWithoutRecordingPr()
    {
        using var fixture = CreateFixture();
        var item = NewItem("codeybox/legacy-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "upstream push diverged", failureKind: WorkItemFailureKinds.UpstreamBlocked);
        await fixture.Store.CreateAsync(item, CancellationToken.None);
        await EnsureHostBranchAsync(fixture, item);
        var headSha = new string('a', 40);
        fixture.Remote.BranchHeadSha = headSha;
        fixture.Remote.BranchCommitMessages = [TrailerMessage];

        var result = await fixture.Redrive.RedriveAsync(item.Id, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.PullRequestNumber);
        Assert.Equal(headSha, result.LeaseBaseSha);
        var stored = (await fixture.Store.GetAsync(item.Id, CancellationToken.None))!;
        Assert.Null(stored.MergedPrNumber);
        Assert.Equal(headSha, stored.LastPushedWorkBranchSha);
    }

    [Fact]
    public async Task RedriveScan_ListsLegacyItemWithoutRecordedPr()
    {
        using var fixture = CreateFixture();
        var item = NewItem("codeybox/legacy-item", fixture.Project.Id);
        item = item.With(
            WorkItemState.MergeConflictResolutionFailed,
            "upstream complete failed: upstream merge conflict");
        await fixture.Store.CreateAsync(item, CancellationToken.None);
        fixture.Remote.OpenPullRequests.Add(new UpstreamPullRequest
        {
            Number = 612,
            Url = "https://github.com/o/r/pull/612",
            HeadBranch = "codeybox/legacy-item",
            HeadSha = new string('b', 40),
            BaseBranch = "main",
            HasMergeConflict = true,
        });

        var candidates = await fixture.Redrive.ListCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(item.Id.ToString(), candidate.WorkItemId);
        Assert.Equal(612, candidate.PullRequestNumber);
        Assert.Equal("codeybox/legacy-item", candidate.WorkBranch);
        Assert.False(string.IsNullOrWhiteSpace(candidate.Reason));
    }

    [Fact]
    public async Task RedriveScan_ListsUpstreamBlockedItemWithLiveRemoteTipButNoPr()
    {
        using var fixture = CreateFixture();
        var item = NewItem("codeybox/legacy-item", fixture.Project.Id);
        item = item.With(WorkItemState.Failed, "refusing to overwrite", failureKind: WorkItemFailureKinds.UpstreamBlocked);
        await fixture.Store.CreateAsync(item, CancellationToken.None);
        var headSha = new string('c', 40);
        fixture.Remote.BranchHeadSha = headSha;

        var candidates = await fixture.Redrive.ListCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(item.Id.ToString(), candidate.WorkItemId);
        Assert.Null(candidate.PullRequestNumber);
        Assert.Equal(headSha, candidate.PullRequestHeadSha);
        Assert.Contains("no open PR", candidate.Reason);
    }

    private static WorkItem NewItem(string branch, ProjectId? projectId = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = projectId ?? new ProjectId("test-project"),
        Title = "legacy redrive item",
        Prompt = "do the thing",
        BaseBranch = "main",
        WorkBranch = branch,
        PushUpstream = true,
    };

    private static async Task EnsureHostBranchAsync(RecoveryFixture fixture, WorkItem item)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(fixture.Workspace);
        var repoId = await fixture.Git.EnsureRepositoryAsync(item.Id, seed, ct: CancellationToken.None);
        var barePath = fixture.Git.GetRepoPath(repoId);
        var clone = Path.Combine(fixture.Workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await TestSupport.RunGit(fixture.Workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "t@t");
            await TestSupport.RunGit(clone, "config", "user.name", "T");
            await TestSupport.RunGit(clone, "checkout", "-b", item.WorkBranch!);
            await File.WriteAllTextAsync(Path.Combine(clone, "work.txt"), "work\n");
            await TestSupport.RunGit(clone, "add", "work.txt");
            await TestSupport.RunGit(clone, "commit", "-m", "work");
            await TestSupport.RunGit(clone, "push", "origin", item.WorkBranch!);
        }
        finally
        {
            if (Directory.Exists(clone))
                Directory.Delete(clone, recursive: true);
        }
    }

    private RecoveryFixture CreateFixture(CommitAttribution? attribution = null)
    {
        var workspace = Path.Combine(_workspace, "fx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workspace);
        var dbPath = Path.Combine(workspace, "redrive.db");
        var store = new SqliteWorkItemStore(dbPath);
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "test",
            RepositoryUrl = "https://example.invalid/o/r.git",
        };
        var projects = new StubProjectRepository(project);
        var remote = new StubRecoveryUpstreamRemote();
        var git = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(workspace, "git") },
            NullLogger<LocalGitHost>.Instance);
        var retrier = new WorkItemRetrier(
            store,
            new StubTaskQueue(),
            git,
            NullLogger<WorkItemRetrier>.Instance);
        CommitAttributionPolicy? policy = attribution is null
            ? null
            : new CommitAttributionPolicy(new CommitAttributionSnapshot(
                new CommitAttributionOptions
                {
                    CoAuthoredBy = attribution.IncludeCoAuthoredBy,
                    CodeyBoxTrailers = attribution.IncludeCodeyBoxTrailers,
                    PullRequestFooter = attribution.IncludePullRequestFooter,
                }));
        var service = new UpstreamRedriveService(
            store, projects, new StubRemoteFactory(remote), retrier,
            NullLogger<UpstreamRedriveService>.Instance,
            attributionPolicy: policy);
        return new RecoveryFixture(workspace, store, project, remote, git, service);
    }

    private sealed record RecoveryFixture(
        string Workspace,
        SqliteWorkItemStore Store,
        Project Project,
        StubRecoveryUpstreamRemote Remote,
        LocalGitHost Git,
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

    private sealed class StubRecoveryUpstreamRemote : IUpstreamRemote
    {
        public List<UpstreamPullRequest> OpenPullRequests { get; } = new();
        public IReadOnlyList<string>? BranchCommitMessages { get; set; }
        public IReadOnlyList<OwnedBranchCommit>? BranchCommits { get; set; }
        public string? BranchHeadSha { get; set; }
        public Dictionary<string, string> BranchTips { get; } = new(StringComparer.Ordinal);
        public string? LastBaseRevision { get; private set; }
        public string Name => "stub-recovery-upstream";

        public Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
            => Task.FromResult(new UpstreamPushResult(true, null));

        public Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
            => Task.FromResult(new UpstreamCompletionOutcome { BranchPushed = true });

        public Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(string branchPrefix, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UpstreamPullRequest>>(
                OpenPullRequests.Where(p => p.HeadBranch.StartsWith(branchPrefix, StringComparison.Ordinal)).ToList());

        public Task<string?> GetBranchHeadShaAsync(string branch, CancellationToken ct = default)
            => Task.FromResult(BranchTips.TryGetValue(branch, out var sha) ? sha : BranchHeadSha);

        public Task<IReadOnlyList<string>?> ListBranchCommitMessagesAsync(
            string baseBranch, string head, int maxCommits, CancellationToken ct = default)
            => Task.FromResult(BranchCommitMessages);

        public Task<IReadOnlyList<OwnedBranchCommit>?> ListBranchCommitsAsync(
            string baseRevision, string head, int maxCommits, CancellationToken ct = default)
        {
            LastBaseRevision = baseRevision;
            if (BranchCommits is not null)
                return Task.FromResult<IReadOnlyList<OwnedBranchCommit>?>(BranchCommits);
            return Task.FromResult<IReadOnlyList<OwnedBranchCommit>?>(BranchCommitMessages?
                .Select(m => new OwnedBranchCommit(string.Empty, m)).ToList());
        }
    }

    private sealed class StubTaskQueue : ITaskQueue
    {
        public int Count => 0;
        public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default) => ValueTask.FromResult<WorkItemId?>(null);
    }
}

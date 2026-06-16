using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class WorkItemRetrierAutoPickTests : IDisposable
{
    private static readonly ProjectId TestProjectId = new("retry-autopick");
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-retry-autopick-").FullName;
    private readonly TestSink _sink = new();

    public WorkItemRetrierAutoPickTests()
    {
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public void Dispose()
    {
        Log.CloseAndFlush();
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task ExplicitFromWork_OverridesAutoPickEvenWhenWorkBranchIsAhead()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost { Ahead = true };
        var item = NewFailedItem(baseBranch: "main");
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: "work");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        Assert.Equal(0, gitHost.BranchHasCommitsAheadCalls);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AutoPickAudit_LogsPickedPhaseAndReason()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost { Ahead = true };
        var item = NewFailedItem(baseBranch: "main");
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal("audit", result.ActualFrom);
        var evt = Assert.Single(_sink.Events, e => Scalar<string>(e, "EventName") == "work_item.retried");
        Assert.Equal(item.Id.ToString(), Scalar<string>(evt, "WorkItemId"));
        Assert.Equal(
            "audit (auto-pick: work branch has prior commits ahead of base)",
            Scalar<string>(evt, "From"));
    }

    [Fact]
    public async Task AutoPickAudit_ClearsStaleStartedAt()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost { Ahead = true };
        var item = NewFailedItem(baseBranch: "main") with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10) };
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.WorkComplete, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, persisted!.State);
        Assert.Null(persisted.StartedAt);
    }

    [Fact]
    public async Task AutoPickInterruptedAuditWithPartialFindings_ResumesAtReworkBoundary()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost { Ahead = true };
        var item = NewFailedItem(baseBranch: "main");
        await store.CreateAsync(item);
        var workAttemptStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await store.RecordIterationDispatchAsync(
            item.Id,
            AuditProgressIterationNumbers.WorkPhase,
            item.PromptRevision,
            workAttemptStartedAt);
        await store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt,
            new AuditProgressRecord(
                Iteration: 5,
                MaxIterations: 10,
                BlockingFindings: 1,
                NonBlockingFindings: 0,
                BlockingFindingIds: ["partial-id"],
                BlockingFindingsDetails:
                [
                    new AuditProgressFinding("architecture", AuditSeverity.Error, "real finding", "fix it"),
                ],
                Findings:
                [
                    new AuditProgressFinding("architecture", AuditSeverity.Error, "real finding", "fix it"),
                ],
                WorkBranchTip: "abc123",
                Status: AuditProgressStatuses.InProgress,
                ScheduledAuditors: ["security", "architecture", "quality"],
                CompletedAuditors: ["architecture"]),
            DateTimeOffset.UtcNow);
        var retrier = NewRetrier(store, queue, gitHost, auditProgress: store);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal("rework", result.ActualFrom);
        Assert.Equal(WorkItemState.WorkComplete, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, persisted!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AutoPickIncompleteAuditWithPartialFindings_ResumesAtReworkBoundary()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost { Ahead = true };
        var item = NewFailedItem(baseBranch: "main");
        await store.CreateAsync(item);
        var workAttemptStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await store.RecordIterationDispatchAsync(
            item.Id,
            AuditProgressIterationNumbers.WorkPhase,
            item.PromptRevision,
            workAttemptStartedAt);
        await store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt,
            new AuditProgressRecord(
                Iteration: 5,
                MaxIterations: 10,
                BlockingFindings: 1,
                NonBlockingFindings: 0,
                BlockingFindingIds: ["partial-id"],
                BlockingFindingsDetails:
                [
                    new AuditProgressFinding("architecture", AuditSeverity.Error, "incomplete finding", "fix it"),
                ],
                Findings:
                [
                    new AuditProgressFinding("architecture", AuditSeverity.Error, "incomplete finding", "fix it"),
                ],
                WorkBranchTip: "abc123",
                Status: AuditProgressStatuses.Incomplete,
                ScheduledAuditors: ["security", "architecture", "quality"],
                CompletedAuditors: ["architecture"]),
            DateTimeOffset.UtcNow);
        var retrier = NewRetrier(store, queue, gitHost, auditProgress: store);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal("rework", result.ActualFrom);
        Assert.Equal(WorkItemState.WorkComplete, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, persisted!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AutoPickInterruptedAudit_IgnoresOlderInterruptedRowWhenLatestAuditCompleted()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost { Ahead = true };
        var item = NewFailedItem(baseBranch: "main");
        await store.CreateAsync(item);
        var workAttemptStartedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await store.RecordIterationDispatchAsync(
            item.Id,
            AuditProgressIterationNumbers.WorkPhase,
            item.PromptRevision,
            workAttemptStartedAt);
        await store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt,
            new AuditProgressRecord(
                Iteration: 5,
                MaxIterations: 10,
                BlockingFindings: 1,
                NonBlockingFindings: 0,
                BlockingFindingIds: ["older-partial-id"],
                BlockingFindingsDetails:
                [
                    new AuditProgressFinding("architecture", AuditSeverity.Error, "older finding", "already superseded"),
                ],
                Findings:
                [
                    new AuditProgressFinding("architecture", AuditSeverity.Error, "older finding", "already superseded"),
                ],
                WorkBranchTip: "abc123",
                Status: AuditProgressStatuses.InProgress,
                ScheduledAuditors: ["architecture"],
                CompletedAuditors: ["architecture"]),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        await store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt,
            new AuditProgressRecord(
                Iteration: 6,
                MaxIterations: 10,
                BlockingFindings: 0,
                NonBlockingFindings: 0,
                BlockingFindingIds: [],
                BlockingFindingsDetails: [],
                Findings: [],
                WorkBranchTip: "def456",
                Status: AuditProgressStatuses.Complete,
                ScheduledAuditors: ["architecture"],
                CompletedAuditors: ["architecture"]),
            DateTimeOffset.UtcNow);
        var retrier = NewRetrier(store, queue, gitHost, auditProgress: store);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal("audit", result.ActualFrom);
        Assert.Equal(WorkItemState.WorkComplete, result.ResumeState);
    }

    [Theory]
    [InlineData("no-work-branch")]
    [InlineData("missing-repo")]
    [InlineData("missing-work-branch")]
    [InlineData("invalid-work-branch")]
    public async Task AutoPickAbsentWorkBranchState_DefaultsToWork(string scenario)
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost { Ahead = true };
        var item = NewFailedItem(baseBranch: "main");
        switch (scenario)
        {
            case "no-work-branch":
                item = item with { WorkBranch = null };
                break;
            case "missing-repo":
                gitHost.RepositoryExists = false;
                break;
            case "missing-work-branch":
                gitHost.BranchExists = false;
                break;
            case "invalid-work-branch":
                item = item with { WorkBranch = "bad..branch" };
                gitHost.ThrowBranchExistsArgumentException = true;
                break;
        }
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        Assert.Equal(0, gitHost.BranchHasCommitsAheadCalls);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AutoPick_DefaultsToWorkWhenBaseAdvancedAfterWorkBranchWasCut()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions
            {
                RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]),
            },
            NullLogger<LocalGitHost>.Instance);
        var item = NewFailedItem(workBranch: "codeybox/stale", baseBranch: "main");
        await store.CreateAsync(item);

        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        await TestSupport.RunGit(barePath, "update-ref", "refs/heads/codeybox/stale", "refs/heads/main");
        await File.WriteAllTextAsync(Path.Combine(seed, "base-advance.txt"), "base advance\n");
        await TestSupport.RunGit(seed, "add", "base-advance.txt");
        await TestSupport.RunGit(seed, "commit", "-m", "advance base after work branch cut");
        await gitHost.EnsureRepositoryAsync(item.Id, seed, "main");

        var retrier = NewRetrier(store, queue, gitHost);
        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        Assert.Equal(WorkItemState.Queued, (await store.GetAsync(item.Id))!.State);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AutoPick_UsesGitDefaultBranchWhenItemAndProjectBaseAreUnavailable()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost
        {
            DefaultBranch = "develop",
            AheadSelector = (_, baseBranch, _) => baseBranch != "develop",
        };
        var item = NewFailedItem(baseBranch: null);
        await store.CreateAsync(item);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Retry autopick",
            RepositoryUrl = "file:///tmp/retry-autopick",
            DefaultBaseBranch = null,
        });
        var retrier = NewRetrier(store, queue, gitHost, projects: projects);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal("work", result.ActualFrom);
        Assert.Equal("develop", gitHost.LastBaseBranch);
        Assert.Equal(1, gitHost.GetDefaultBranchCalls);
    }

    [Fact]
    public async Task AutoPick_UsesExplicitItemBaseBranchBeforeProjectDefault()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = new RecordingGitHost
        {
            AheadSelector = (_, baseBranch, _) => string.Equals(baseBranch, "main", StringComparison.Ordinal),
        };
        var item = NewFailedItem(baseBranch: "release/item-base");
        await store.CreateAsync(item);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Retry autopick",
            RepositoryUrl = "file:///tmp/retry-autopick",
            DefaultBaseBranch = "main",
        });
        var retrier = NewRetrier(store, queue, gitHost, projects: projects);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        Assert.Equal("release/item-base", gitHost.LastBaseBranch);
        Assert.Equal(0, gitHost.GetDefaultBranchCalls);
    }

    [Fact]
    public async Task AutoPick_UsesPersistedReleaseBranchBeforeProjectDefault()
    {
        using var store = NewStore();
        using var releases = new SqliteReleaseStore(Path.Combine(_workspace, "releases.db"));
        var queue = new InMemoryTaskQueue();
        var release = new Release
        {
            Id = ReleaseId.New(),
            ProjectId = TestProjectId,
            Name = "v1.0",
            State = ReleaseState.Open,
            BranchName = "release/v1.0",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await releases.CreateAsync(release);
        var gitHost = new RecordingGitHost
        {
            AheadSelector = (_, baseBranch, _) => baseBranch == "main",
        };
        var item = NewFailedItem(baseBranch: null, releaseId: release.Id);
        await store.CreateAsync(item);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = TestProjectId,
            DisplayName = "Retry autopick",
            RepositoryUrl = "file:///tmp/retry-autopick",
            DefaultBaseBranch = "main",
        });
        var retrier = NewRetrier(store, queue, gitHost, projects: projects, releases: releases);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        Assert.Equal("release/v1.0", gitHost.LastBaseBranch);
        Assert.Equal(0, gitHost.GetDefaultBranchCalls);
    }

    private SqliteWorkItemStore NewStore()
        => new(Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db"));

    private static WorkItemRetrier NewRetrier(
        IWorkItemStore store,
        ITaskQueue queue,
        IGitHost gitHost,
        IProjectRepository? projects = null,
        IReleaseStore? releases = null,
        IAuditProgressStore? auditProgress = null)
        => new(
            store,
            queue,
            gitHost,
            NullLogger<WorkItemRetrier>.Instance,
            projects: projects,
            releases: releases,
            auditProgress: auditProgress);

    private static WorkItem NewFailedItem(
        string? workBranch = "codeybox/retry-autopick",
        string? baseBranch = null,
        ReleaseId? releaseId = null)
        => new()
        {
            Id = WorkItemId.New(),
            ProjectId = TestProjectId,
            Title = "Retry autopick",
            Prompt = "retry the previous work",
            BaseBranch = baseBranch,
            WorkBranch = workBranch,
            PushUpstream = false,
            State = WorkItemState.Failed,
            LastError = "previous failure",
            ReleaseId = releaseId,
        };

    private static T? Scalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue scalar)
            return default;
        return scalar.Value is T value ? value : default;
    }

    private sealed class RecordingGitHost : IGitHost
    {
        public bool RepositoryExists { get; set; } = true;
        public bool BranchExists { get; set; } = true;
        public bool ThrowBranchExistsArgumentException { get; set; }
        public bool Ahead { get; set; }
        public string DefaultBranch { get; set; } = "main";
        public Func<string, string, string, bool>? AheadSelector { get; set; }
        public int BranchHasCommitsAheadCalls { get; private set; }
        public int GetDefaultBranchCalls { get; private set; }
        public string? LastBaseBranch { get; private set; }

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
        {
            GetDefaultBranchCalls++;
            return Task.FromResult(DefaultBranch);
        }

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
            => Task.FromResult(RepositoryExists);

        public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
        {
            if (ThrowBranchExistsArgumentException)
                throw new ArgumentException("invalid work branch name", nameof(branch));

            return Task.FromResult(BranchExists);
        }

        public Task<bool> BranchHasCommitsAheadAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
        {
            BranchHasCommitsAheadCalls++;
            LastBaseBranch = baseBranch;
            return Task.FromResult(AheadSelector?.Invoke(repositoryId, baseBranch, workBranch) ?? Ahead);
        }

        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => Task.FromResult((string.Empty, string.Empty));
    }
}

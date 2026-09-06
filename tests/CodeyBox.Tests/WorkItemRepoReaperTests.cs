using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verification tests for <see cref="WorkItemRepoReaper"/>:
/// - Terminal item's clone is removed.
/// - Non-terminal item's clone survives.
/// - Sweep continues past unreadable and unknown entries.
/// - Retention grace period is honoured from configuration rather than a constant.
/// - Sweep does not race an active worker (in-memory or registered).
/// - Enabled toggle disables reaping and sweeping.
/// </summary>
public sealed class WorkItemRepoReaperTests : IDisposable
{
    private readonly string _testDir;
    private readonly string _gitRoot;
    private readonly string _dbPath;
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly LocalGitHost _gitHost;

    public WorkItemRepoReaperTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"cb-reporeap-tests-{Guid.NewGuid():N}");
        _gitRoot = Path.Combine(_testDir, "repos");
        _dbPath = Path.Combine(_testDir, "state.db");
        Directory.CreateDirectory(_gitRoot);

        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = _gitRoot },
            NullLogger<LocalGitHost>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch
        {
            // Ignore test cleanup failures
        }
    }

    private WorkItem CreateItem(WorkItemState state, DateTimeOffset? updatedAt = null)
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-proj"),
            Title = "Test work item",
            Prompt = "Implement test feature",
            State = state,
            UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow,
        };
        return item;
    }

    private string CreateRepoClone(WorkItemId id)
    {
        var path = Path.Combine(_gitRoot, id + ".git");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "HEAD"), "ref: refs/heads/main\n");
        return path;
    }

    [Fact]
    public async Task TerminalItem_CloneIsRemoved()
    {
        var item = CreateItem(WorkItemState.Done);
        await _store.CreateAsync(item);
        var repoPath = CreateRepoClone(item.Id);
        Assert.True(Directory.Exists(repoPath));

        var options = new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero };
        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance);

        var reaped = await reaper.ReapWorkItemAsync(item.Id);

        Assert.True(reaped);
        Assert.False(Directory.Exists(repoPath));
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.MergeConflictResolutionFailed)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    public async Task AllTerminalStates_CloneIsRemoved(WorkItemState state)
    {
        var item = CreateItem(state);
        await _store.CreateAsync(item);
        var repoPath = CreateRepoClone(item.Id);
        Assert.True(Directory.Exists(repoPath));

        var options = new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero };
        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance);

        var summary = await reaper.RunSweepAsync();

        Assert.Equal(1, summary.Reaped);
        Assert.False(Directory.Exists(repoPath));
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Queued)]
    [InlineData(WorkItemState.Planning)]
    [InlineData(WorkItemState.NeedsOperatorInput)]
    [InlineData(WorkItemState.WaitingForQuotaReset)]
    [InlineData(WorkItemState.WaitingForAgentResume)]
    [InlineData(WorkItemState.WaitingForTransientRetry)]
    public async Task NonTerminalItem_CloneSurvives(WorkItemState state)
    {
        var item = CreateItem(state);
        await _store.CreateAsync(item);
        var repoPath = CreateRepoClone(item.Id);
        Assert.True(Directory.Exists(repoPath));

        var options = new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero };
        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance);

        var reaped = await reaper.ReapWorkItemAsync(item.Id);
        Assert.False(reaped);
        Assert.True(Directory.Exists(repoPath));

        var summary = await reaper.RunSweepAsync();
        Assert.Equal(0, summary.Reaped);
        Assert.Equal(1, summary.SkippedNonTerminal);
        Assert.True(Directory.Exists(repoPath));
    }

    [Fact]
    public async Task Sweep_ContinuesPastUnknownAndUnreadableEntries()
    {
        // 1. Unknown entry: directory with GUID.git that doesn't exist in DB
        var unknownGuid = Guid.NewGuid();
        var unknownPath = Path.Combine(_gitRoot, unknownGuid + ".git");
        Directory.CreateDirectory(unknownPath);

        // 2. Non-work-item directory (e.g. system folder or staging directory)
        var nonWorkItemPath = Path.Combine(_gitRoot, "staging-temp-repo.git");
        Directory.CreateDirectory(nonWorkItemPath);

        // 3. Valid terminal item
        var validItem = CreateItem(WorkItemState.Done);
        await _store.CreateAsync(validItem);
        var validRepoPath = CreateRepoClone(validItem.Id);

        var options = new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero };
        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance);

        var summary = await reaper.RunSweepAsync();

        // The sweep must tolerate unknown/non-work-item entries and complete successfully
        Assert.Equal(1, summary.Reaped);
        Assert.Equal(1, summary.SkippedUnknown);
        Assert.False(Directory.Exists(validRepoPath));
        Assert.True(Directory.Exists(unknownPath));
        Assert.True(Directory.Exists(nonWorkItemPath));
    }

    [Fact]
    public async Task RetentionGracePeriod_HonouredFromConfiguration_NotAConstant()
    {
        var t0 = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(t0);

        var item = CreateItem(WorkItemState.Done, updatedAt: t0);
        await _store.CreateAsync(item);
        var repoPath = CreateRepoClone(item.Id);

        // Configure grace period of 2 hours from options
        var configuredGrace = TimeSpan.FromHours(2);
        var options = new RepoRetentionOptions
        {
            Enabled = true,
            GracePeriod = configuredGrace,
        };

        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            () => options,
            NullLogger<WorkItemRepoReaper>.Instance,
            time: time);

        // Advance 1 hour: within grace period, clone MUST survive
        time.SetUtcNow(t0.AddHours(1));
        var summary1 = await reaper.RunSweepAsync();
        Assert.Equal(0, summary1.Reaped);
        Assert.Equal(1, summary1.SkippedWithinGrace);
        Assert.True(Directory.Exists(repoPath));

        // Advance past configured 2 hour grace period: clone MUST be reaped
        time.SetUtcNow(t0.AddHours(2).AddSeconds(1));
        var summary2 = await reaper.RunSweepAsync();
        Assert.Equal(1, summary2.Reaped);
        Assert.Equal(0, summary2.SkippedWithinGrace);
        Assert.False(Directory.Exists(repoPath));
    }

    [Fact]
    public async Task RetentionGracePeriod_HotReload_UpdatesDynamically()
    {
        var t0 = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(t0);

        var item = CreateItem(WorkItemState.Done, updatedAt: t0);
        await _store.CreateAsync(item);
        var repoPath = CreateRepoClone(item.Id);

        // Initially configured with 24-hour grace window
        var currentOptions = new RepoRetentionOptions
        {
            Enabled = true,
            GracePeriod = TimeSpan.FromHours(24),
        };

        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            () => currentOptions,
            NullLogger<WorkItemRepoReaper>.Instance,
            time: time);

        // Advance 30 minutes: within 24h grace window, clone survives
        time.SetUtcNow(t0.AddMinutes(30));
        var summary1 = await reaper.RunSweepAsync();
        Assert.Equal(0, summary1.Reaped);
        Assert.Equal(1, summary1.SkippedWithinGrace);
        Assert.True(Directory.Exists(repoPath));

        // Hot-reload configuration: change GracePeriod to 15 minutes
        currentOptions = new RepoRetentionOptions
        {
            Enabled = true,
            GracePeriod = TimeSpan.FromMinutes(15),
        };

        // Next sweep at same time (30 min old > 15 min new grace) immediately reaps the clone
        var summary2 = await reaper.RunSweepAsync();
        Assert.Equal(1, summary2.Reaped);
        Assert.False(Directory.Exists(repoPath));
    }

    [Fact]
    public async Task Sweep_DoesNotRaceActiveWorker_InMemory()
    {
        var item = CreateItem(WorkItemState.Done);
        await _store.CreateAsync(item);
        var repoPath = CreateRepoClone(item.Id);

        var options = new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero };
        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance);

        // Register in-memory active check indicating this item is currently running
        reaper.RegisterActiveItemCheck(id => id == item.Id);

        var reaped = await reaper.ReapWorkItemAsync(item.Id);
        Assert.False(reaped);
        Assert.True(Directory.Exists(repoPath));

        var summary = await reaper.RunSweepAsync();
        Assert.Equal(0, summary.Reaped);
        Assert.Equal(1, summary.SkippedActive);
        Assert.True(Directory.Exists(repoPath));
    }

    [Fact]
    public async Task Sweep_DoesNotRaceActiveWorker_InRegistry()
    {
        var item = CreateItem(WorkItemState.Done);
        await _store.CreateAsync(item);
        var repoPath = CreateRepoClone(item.Id);

        // Register active worker in SQLite worker registry
        var workerRegistration = new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "worker-host-1",
            ProcessId = 1234,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CurrentWorkItemId = item.Id.ToString(),
        };
        await _registry.RegisterAsync(workerRegistration);

        var options = new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero };
        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance,
            workerRegistry: _registry);

        var reaped = await reaper.ReapWorkItemAsync(item.Id);
        Assert.False(reaped);
        Assert.True(Directory.Exists(repoPath));

        var summary = await reaper.RunSweepAsync();
        Assert.Equal(0, summary.Reaped);
        Assert.Equal(1, summary.SkippedActive);
        Assert.True(Directory.Exists(repoPath));

        // Deregister worker from registry
        await _registry.DeregisterAsync(workerRegistration.WorkerId);

        // Now sweep should safely reap the clone
        var summaryAfterDeregister = await reaper.RunSweepAsync();
        Assert.Equal(1, summaryAfterDeregister.Reaped);
        Assert.False(Directory.Exists(repoPath));
    }

    [Fact]
    public async Task Enabled_False_SkipsReapAndSweep()
    {
        var item = CreateItem(WorkItemState.Done);
        await _store.CreateAsync(item);
        var repoPath = CreateRepoClone(item.Id);

        var options = new RepoRetentionOptions { Enabled = false, GracePeriod = TimeSpan.Zero };
        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance);

        var reaped = await reaper.ReapWorkItemAsync(item.Id);
        Assert.False(reaped);
        Assert.True(Directory.Exists(repoPath));

        var summary = await reaper.RunSweepAsync();
        Assert.Equal(0, summary.Reaped);
        Assert.Equal(0, summary.Scanned);
        Assert.True(Directory.Exists(repoPath));
    }

    [Fact]
    public async Task StartupSweep_SweepsAlreadyTerminalClonesLeftBehind()
    {
        var item1 = CreateItem(WorkItemState.Done);
        var item2 = CreateItem(WorkItemState.Failed);
        var item3 = CreateItem(WorkItemState.Working);
        await _store.CreateAsync(item1);
        await _store.CreateAsync(item2);
        await _store.CreateAsync(item3);

        var repo1 = CreateRepoClone(item1.Id);
        var repo2 = CreateRepoClone(item2.Id);
        var repo3 = CreateRepoClone(item3.Id);

        var options = new RepoRetentionOptions
        {
            Enabled = true,
            GracePeriod = TimeSpan.Zero,
            CheckInterval = TimeSpan.FromHours(1),
        };

        var reaper = new WorkItemRepoReaper(
            _gitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance);

        using var cts = new CancellationTokenSource();
        // StartAsync invokes ExecuteAsync which executes the initial startup sweep
        await reaper.StartAsync(cts.Token);

        // Give the background task a moment to run the startup sweep
        for (var i = 0; i < 20 && (Directory.Exists(repo1) || Directory.Exists(repo2)); i++)
        {
            await Task.Delay(50);
        }

        await reaper.StopAsync(CancellationToken.None);

        Assert.False(Directory.Exists(repo1));
        Assert.False(Directory.Exists(repo2));
        Assert.True(Directory.Exists(repo3));
    }

    [Fact]
    public async Task Sweep_ContinuesPastEntryError()
    {
        var errorItem = CreateItem(WorkItemState.Done);
        var validItem = CreateItem(WorkItemState.Done);
        await _store.CreateAsync(errorItem);
        await _store.CreateAsync(validItem);

        var errorRepo = CreateRepoClone(errorItem.Id);
        var validRepo = CreateRepoClone(validItem.Id);

        var faultInjectingGitHost = new FaultInjectingGitHost(_gitHost, errorItem.Id.ToString());

        var options = new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero };
        var reaper = new WorkItemRepoReaper(
            faultInjectingGitHost,
            _store,
            options,
            NullLogger<WorkItemRepoReaper>.Instance);

        var summary = await reaper.RunSweepAsync();

        // Error disposing errorItem must not abort sweep: validItem must still be reaped!
        Assert.Equal(1, summary.Errors);
        Assert.Equal(1, summary.Reaped);
        Assert.True(Directory.Exists(errorRepo));
        Assert.False(Directory.Exists(validRepo));
    }

    [Fact]
    public async Task GraceWindow_Alias_SetsAndGetsGracePeriod()
    {
        var options = new RepoRetentionOptions();
        options.GraceWindow = TimeSpan.FromMinutes(45);
        Assert.Equal(TimeSpan.FromMinutes(45), options.GracePeriod);
        Assert.Equal(TimeSpan.FromMinutes(45), options.GraceWindow);

        options.GracePeriod = TimeSpan.FromHours(2);
        Assert.Equal(TimeSpan.FromHours(2), options.GraceWindow);
    }

    private sealed class FaultInjectingGitHost : IGitHost
    {
        private readonly LocalGitHost _inner;
        private readonly string _failingRepoId;

        public FaultInjectingGitHost(LocalGitHost inner, string failingRepoId)
        {
            _inner = inner;
            _failingRepoId = failingRepoId;
        }

        public string RepositoriesRootDirectory => _inner.RepositoriesRootDirectory;

        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        {
            if (string.Equals(repositoryId, _failingRepoId, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Simulated git dispose failure for " + repositoryId);
            return _inner.DisposeRepositoryAsync(repositoryId, ct);
        }

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default) => _inner.EnsureRepositoryAsync(id, seedFromUrl, ct);
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default) => _inner.EnsureRepositoryAsync(id, seedFromUrl, baseBranch, ct);
        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) => _inner.GetSandboxAccess(repositoryId);
        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default) => _inner.GetDefaultBranchAsync(repositoryId, ct);
        public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch, IReadOnlyDictionary<string, string> upstreamEnv, UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase, CancellationToken ct = default) => _inner.PushToUpstreamAsync(repositoryId, upstreamUrl, branch, upstreamEnv, reconcileStrategy, ct);
        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default) => _inner.RepositoryExistsAsync(id, ct);
        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default) => _inner.GetDiffAsync(repositoryId, baseBranch, workBranch, ct);
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;
        public MutableTimeProvider(DateTimeOffset initial) => _utcNow = initial;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void SetUtcNow(DateTimeOffset time) => _utcNow = time;
    }
}

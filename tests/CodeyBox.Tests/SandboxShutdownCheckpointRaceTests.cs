using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the graceful-shutdown checkpoint race: in-flight items
/// are checkpointed to a resumable state before VM teardown, and workers still
/// parked inside a sandbox operation must not overwrite that checkpoint with a
/// terminal failure when teardown disposes the sandbox underneath them.
/// </summary>
public sealed class SandboxShutdownCheckpointRaceTests : IDisposable
{
    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-shutdown-race-");
    private readonly SqliteWorkItemStore _store;

    public SandboxShutdownCheckpointRaceTests()
    {
        _store = new SqliteWorkItemStore(_scratch.DbPath("race.db"));
        ShutdownCheckpointGuard.Clear();
    }

    public void Dispose()
    {
        _store.Dispose();
        TestScratchDirectory.ClearSqlitePools();
        ShutdownCheckpointGuard.Clear();
        _scratch.Dispose();
    }

    private static WorkItem AuditingItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Auditing,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    [Fact]
    public async Task GracefulShutdown_WorkerParkedInSandboxOp_ItemEndsCheckpointed_NotFailed()
    {
        // End-to-end drive of the incident: a worker is parked inside a sandbox
        // exec when graceful shutdown checkpoints its Auditing item to
        // WorkComplete and then disposes the sandbox. The worker wakes to the
        // disposal error and routes it through the same terminal sink
        // PipelineRunner's generic catch uses. The item must keep its
        // checkpointed resumable state instead of flipping to Failed.
        var item = AuditingItem();
        await _store.CreateAsync(item);

        var provider = new RaceFakeProvider();
        var sandbox = new ParkingRaceSandbox("vm-parked");
        provider.Register(item.Id, sandbox);

        var svc = new SandboxShutdownTeardownService(
            provider,
            _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Dispose);

        var parkedExec = sandbox.ExecAsync(
            new SandboxExec { Argv = ["agent", "run"] },
            CancellationToken.None);
        Assert.False(parkedExec.IsCompleted, "worker must be parked inside the sandbox op before shutdown");

        using var hostShutdown = new CancellationTokenSource();
        await svc.TeardownAllAsync(hostShutdown.Token);

        Assert.True(sandbox.DisposeCalled, "Dispose teardown must dispose the sandbox");
        var disposal = await Assert.ThrowsAsync<InvalidOperationException>(() => parkedExec);
        Assert.Contains(
            "stopping, preserved, or disposed.",
            disposal.Message,
            StringComparison.Ordinal);

        var checkpointed = await _store.GetAsync(item.Id);
        Assert.NotNull(checkpointed);
        Assert.Equal(WorkItemState.WorkComplete, checkpointed.State);
        Assert.Contains("clean restart on next boot", checkpointed.LastError, StringComparison.Ordinal);
        Assert.True(
            ShutdownCheckpointGuard.IsCheckpointed(item.Id),
            "shutdown checkpoint must be recorded as authoritative");

        // The worker's failure sink refuses the terminal write ...
        var terminal = new WorkItemTerminalTransition(
            _store,
            webhooks: null,
            projects: null,
            NullLogger<WorkItemTerminalTransition>.Instance);
        var result = await terminal.TransitionFailedAsync(
            checkpointed,
            disposal.Message,
            new WorkItemTerminalFailureTransitionCommand { FailureKind = "other" },
            CancellationToken.None);

        Assert.False(result.Updated, "terminal write over a shutdown checkpoint must be refused");
        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.WorkComplete, after.State);
        Assert.NotEqual(WorkItemState.Failed, after.State);

        // ... and the PipelineRunner generic-catch guard suppresses the same
        // failure while host shutdown is in progress, but stays out of the way
        // otherwise.
        hostShutdown.Cancel();
        Assert.True(
            ShutdownCheckpointGuard.ShouldSuppressTerminalFailure(disposal, hostShutdown.Token, item.Id));
        Assert.False(
            ShutdownCheckpointGuard.ShouldSuppressTerminalFailure(disposal, CancellationToken.None, item.Id));
        Assert.False(
            ShutdownCheckpointGuard.ShouldSuppressTerminalFailure(
                new InvalidOperationException("ordinary work failure"),
                hostShutdown.Token,
                WorkItemId.New()));
    }

    [Fact]
    public async Task RequiredBuildVerifier_VmStartSigtermedByShutdown_DoesNotReportInfrastructureFailure()
    {
        // The mid-VM-start case from the same incident: a sandbox-create that
        // dies with exit 143 (SIGTERM from shutdown) must not be reported as
        // "could not verify required build" (an infrastructure failure).
        var workspaceRoot = Path.Combine(_scratch.DirectoryPath, "ws-143");
        Directory.CreateDirectory(workspaceRoot);
        var seed = await TestSupport.CreateSeedRepoAsync(workspaceRoot);
        await File.WriteAllTextAsync(Path.Combine(seed, "CodeyBox.slnx"), "# solution marker\n");
        await TestSupport.RunGit(seed, "add", "CodeyBox.slnx");
        await TestSupport.RunGit(seed, "commit", "-m", "add solution marker");

        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(workspaceRoot, "repos") },
            NullLogger<LocalGitHost>.Instance);
        var verifier = new SandboxRequiredBuildVerifier(
            new SigtermVmStartSandboxProvider(),
            gitHost,
            new PipelineOptions { SandboxImageReference = "ignored" });

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "sigterm vm start",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = "feature/sigterm-vm-start",
            PushUpstream = false,
        };
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var clone = Path.Combine(workspaceRoot, "branch");
        await TestSupport.RunGit(workspaceRoot, "clone", gitHost.GetRepoPath(repoId), clone);
        await TestSupport.RunGit(clone, "config", "user.email", "t@l");
        await TestSupport.RunGit(clone, "config", "user.name", "T");
        await TestSupport.RunGit(clone, "checkout", "-B", item.WorkBranch);
        await File.WriteAllTextAsync(Path.Combine(clone, "ok.txt"), "ok\n");
        await TestSupport.RunGit(clone, "add", "ok.txt");
        await TestSupport.RunGit(clone, "commit", "-m", "branch exists");
        await TestSupport.RunGit(clone, "push", "origin", $"{item.WorkBranch}:{item.WorkBranch}");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifyAsync(
                new RequiredBuildVerificationRequest
                {
                    WorkItemId = item.Id,
                    ProjectId = item.ProjectId,
                    SandboxPolicy = new RequiredBuildSandboxPolicy(),
                    RepositoryId = repoId,
                    BaseBranch = item.BaseBranch,
                    WorkBranch = item.WorkBranch!,
                    Phase = "audit",
                },
                CancellationToken.None));

        Assert.Contains("exit code 143", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "could not verify required build",
            thrown.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ShutdownCheckpointGuard_ClassifiesShutdownCausedFailures()
    {
        Assert.True(ShutdownCheckpointGuard.IsSandboxDisposalFailure(
            new InvalidOperationException("The Incus sandbox is stopping, preserved, or disposed.")));
        Assert.True(ShutdownCheckpointGuard.IsSandboxDisposalFailure(
            new InvalidOperationException("outer", new ObjectDisposedException("sandbox", "The Incus sandbox is stopping, preserved, or disposed."))));
        Assert.False(ShutdownCheckpointGuard.IsSandboxDisposalFailure(
            new InvalidOperationException("ordinary work failure")));

        Assert.True(ShutdownCheckpointGuard.IsShutdownVmStartInterruption(
            new InvalidOperationException("Incus start VM failed with exit code 143: ")));
        Assert.True(ShutdownCheckpointGuard.IsShutdownVmStartInterruption(
            new InvalidOperationException("outer", new InvalidOperationException("sandbox-create failed: exit code 143"))));
        Assert.False(ShutdownCheckpointGuard.IsShutdownVmStartInterruption(
            new InvalidOperationException("Incus start VM failed with exit code 1: boom")));
    }

    [Fact]
    public async Task CloneReaper_DoesNotReap_ShutdownCheckpointedItem()
    {
        // Even if a slower worker path already overwrote the checkpoint with a
        // terminal row before the refusal guard could engage, the clone reaper
        // must not delete the interrupted work: the shutdown checkpoint stays
        // authoritative and the clone must survive for the clean restart.
        var item = AuditingItem();
        await _store.CreateAsync(item);

        var provider = new RaceFakeProvider();
        provider.Register(item.Id, new ParkingRaceSandbox("vm-reaper"));
        var svc = new SandboxShutdownTeardownService(
            provider,
            _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Dispose);
        await svc.TeardownAllAsync();

        var checkpointed = await _store.GetAsync(item.Id);
        Assert.NotNull(checkpointed);
        Assert.Equal(WorkItemState.WorkComplete, checkpointed.State);

        // Simulate the lost race: the Failed row was written over the
        // checkpoint (written directly because the guarded sink now refuses
        // this exact write).
        await _store.UpdateAsync(checkpointed.With(
            WorkItemState.Failed,
            "The Incus sandbox is stopping, preserved, or disposed.",
            failureKind: "other"));

        var gitHost = new RecordingGitHost();
        var reaper = new WorkItemRepoReaper(
            gitHost,
            _store,
            new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero },
            NullLogger<WorkItemRepoReaper>.Instance);

        var reaped = await reaper.ReapWorkItemAsync(item.Id, force: true);
        Assert.False(reaped, "reaper must not reap an item that shutdown checkpointed");
        Assert.Empty(gitHost.DisposedRepositories);
    }

    [Fact]
    public async Task CloneReaperSweep_Skips_ShutdownCheckpointedItem()
    {
        var item = AuditingItem();
        await _store.CreateAsync(item);

        var provider = new RaceFakeProvider();
        provider.Register(item.Id, new ParkingRaceSandbox("vm-reaper-sweep"));
        var svc = new SandboxShutdownTeardownService(
            provider,
            _store,
            NullLogger<SandboxShutdownTeardownService>.Instance,
            teardownMode: SandboxTeardownMode.Dispose);
        await svc.TeardownAllAsync();

        var checkpointed = await _store.GetAsync(item.Id);
        Assert.NotNull(checkpointed);
        await _store.UpdateAsync(checkpointed.With(
            WorkItemState.Failed,
            "The Incus sandbox is stopping, preserved, or disposed.",
            failureKind: "other"));

        var reposRoot = Path.Combine(_scratch.DirectoryPath, "repos-sweep");
        Directory.CreateDirectory(reposRoot);
        Directory.CreateDirectory(Path.Combine(reposRoot, item.Id.Value.ToString("D") + ".git"));

        var gitHost = new RecordingGitHost();
        var reaper = new WorkItemRepoReaper(
            gitHost,
            _store,
            new RepoRetentionOptions { Enabled = true, GracePeriod = TimeSpan.Zero },
            NullLogger<WorkItemRepoReaper>.Instance,
            repositoriesRoot: reposRoot);

        var summary = await reaper.RunSweepAsync();
        Assert.Equal(0, summary.Reaped);
        Assert.Equal(1, summary.SkippedShutdownCheckpoint);
        Assert.Empty(gitHost.DisposedRepositories);
    }

    private sealed class RaceFakeProvider : ISandboxProvider, IActiveSandboxProvider
    {
        private readonly Dictionary<WorkItemId, IShutdownTeardownSandbox> _active = new();
        public void Register(WorkItemId id, IShutdownTeardownSandbox sandbox) => _active[id] = sandbox;
        public string Name => "fake-shutdown-race";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new ParkingRaceSandbox("fake-shutdown-race-created"));
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> SnapshotActiveSandboxes()
            => _active.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private sealed class ParkingRaceSandbox : IShutdownTeardownSandbox
    {
        private readonly TaskCompletionSource<SandboxExecResult> _parked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ParkingRaceSandbox(string id) => Id = id;
        public string Id { get; }
        public bool DisposeCalled { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => _parked.Task;

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            _parked.TrySetException(new InvalidOperationException(
                "The Incus sandbox is stopping, preserved, or disposed."));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SigtermVmStartSandboxProvider : ISandboxProvider
    {
        public string Name => "sigterm-vm-start";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new InvalidOperationException("Incus start VM failed with exit code 143: ");

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingGitHost : IGitHost
    {
        public List<string> DisposedRepositories { get; } = new();

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => throw new NotSupportedException();
        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId)
            => throw new NotSupportedException();
        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task PushToUpstreamAsync(
            string repositoryId,
            string upstreamUrl,
            string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
        {
            DisposedRepositories.Add(repositoryId);
            return Task.CompletedTask;
        }
        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(
            string repositoryId,
            string baseBranch,
            string workBranch,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

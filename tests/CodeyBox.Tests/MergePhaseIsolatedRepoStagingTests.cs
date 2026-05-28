using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies the merge phase's isolated bare-repo clone lands under
/// <see cref="IGitHost.GetMergeStagingRoot"/> rather than
/// <see cref="Path.GetTempPath"/>. Background: the snap-confined Multipass
/// daemon's AppArmor profile only allows reads inside
/// <c>~/snap/multipass/common/</c>; the operator-configured
/// <c>GitRootDirectory</c> already lives there for the durable bare repo,
/// so staging the merge clone as a sibling inherits the property.
/// Other sandbox providers (process, bubblewrap) do not share the AppArmor
/// constraint, but routing through the git host keeps the staging-location
/// decision on the side that knows where its durable bare repos already live.
/// </summary>
public sealed class MergePhaseIsolatedRepoStagingTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-merge-staging-").FullName;

    public void Dispose() => Directory.Delete(_workspace, recursive: true);

    [Fact]
    public async Task IsolatedMergeRepo_IsClonedUnderGitHostStagingRoot_NotUnderTempPath()
    {
        var gitRoot = Path.Combine(_workspace, "git-root");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var pipeline = CreatePipeline(gitHost);
        var clonedPath = await pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None);

        try
        {
            // Primary invariant: the cloned bare repo lives under whatever the
            // git host reports as its merge staging root. That root is the
            // operator's configured GitRootDirectory — under
            // ~/snap/multipass/common/... in snap-Multipass installations — so
            // the host bind-mount source is in a path the multipass daemon's
            // AppArmor profile allows.
            Assert.Equal(((IGitHost)gitHost).GetMergeStagingRoot(repoId),
                Path.GetDirectoryName(clonedPath));
            Assert.True(Directory.Exists(clonedPath), $"expected isolated bare clone at {clonedPath}");
            Assert.True(File.Exists(Path.Combine(clonedPath, "HEAD")),
                "isolated clone must be a valid bare git repository (HEAD missing)");

            // Regression guard: the old code unconditionally staged into
            // Path.GetTempPath(); make sure we never land back there directly.
            // (gitRoot itself may be a subdirectory of /tmp in tests — that is
            // fine; the failure mode we're guarding against is staging into
            // /tmp itself rather than into the configured bare-repo root.)
            var tempPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            Assert.NotEqual(tempPath, Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitRoot)));
            Assert.NotEqual(
                tempPath,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(clonedPath)!)));
        }
        finally
        {
            try { Directory.Delete(clonedPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task IsolatedMergeRepo_ConcurrentMergeStagingsAndReaperSweep_NoneAreDisturbed()
    {
        // Integration-style coverage for acceptance criterion 3: a real
        // merge-phase staging (bare-clone-and-keep-it) must survive while
        // a background SandboxLeakReaper sweep is running concurrently. The
        // reaper only enumerates VMs via ISandboxProvider — never host
        // filesystem paths — but pinning that contract here exercises the
        // real CreateIsolatedMergeRepositoryAsync path so a future change
        // that adds host-directory cleanup to the reaper would fail this
        // test instead of breaking merges in production.
        var gitRoot = Path.Combine(_workspace, "git-root-concurrent");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);
        var pipeline = CreatePipeline(gitHost);

        var reaperProvider = new FakeSandboxProvider();
        var reaperOpts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1),
            LeakAgeThreshold = TimeSpan.FromMinutes(30),
            PreemptRetention = TimeSpan.FromHours(24),
            AutoDispose = false,
            MaxConcurrentAutoDispose = 4,
        };
        var reaper = new SandboxLeakReaper(
            reaperProvider,
            new NullWebhookDispatcher(),
            reaperOpts,
            NullLogger<SandboxLeakReaper>.Instance);
        using var sweepCts = new CancellationTokenSource();

        // Sweep continuously in the background; the reaper should never
        // touch the host directory the merge staging operations create.
        var sweepLoop = Task.Run(async () =>
        {
            while (!sweepCts.IsCancellationRequested)
            {
                await reaper.RunSweepAsync(CancellationToken.None);
                try { await Task.Delay(TimeSpan.FromMilliseconds(5), sweepCts.Token); }
                catch (OperationCanceledException) { return; }
            }
        });

        // Run 6 merge-staging operations in parallel against the same bare
        // repo. Each clone copies the bare repo's pack data — when the
        // upstream is large enough the clones overlap in wall-clock time,
        // exercising the "slow clone" timing window the production failure
        // surfaced in.
        var stagings = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None))
            .ToArray());

        try
        {
            sweepCts.Cancel();
            await sweepLoop;

            // Every staging clone must still exist on disk and be a valid
            // bare git repo — i.e. the reaper did not touch it, and no
            // GUID collisions in the staging filename caused overwrites.
            Assert.Equal(stagings.Length, stagings.Distinct(StringComparer.Ordinal).Count());
            foreach (var path in stagings)
            {
                Assert.True(Directory.Exists(path), $"staging clone missing: {path}");
                Assert.True(File.Exists(Path.Combine(path, "HEAD")),
                    $"staging clone is not a valid bare git repo: {path}");
                Assert.Equal(gitRoot, Path.GetDirectoryName(path));
            }
        }
        finally
        {
            foreach (var path in stagings)
            {
                try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task IsolatedMergeRepo_SurfacesGitHostStagingRoot()
    {
        // The pipeline must defer staging-root selection to IGitHost; this
        // pins the contract so that a future GitHub-backed or in-memory host
        // (whose layout differs from LocalGitHost's flat siblings) can stage
        // wherever its sandbox provider allows by overriding the method.
        var gitRoot = Path.Combine(_workspace, "git-root-deferred");
        var spyHost = new StagingRootRecordingHost(
            new LocalGitHost(
                new LocalGitHostOptions { RootDirectory = gitRoot },
                NullLogger<LocalGitHost>.Instance));

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await spyHost.EnsureRepositoryAsync(workItemId, seed);

        var pipeline = CreatePipeline(spyHost);
        var clonedPath = await pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None);
        try
        {
            Assert.Contains(repoId, spyHost.StagingRootCalls);
            Assert.Equal(gitRoot, Path.GetDirectoryName(clonedPath));
        }
        finally
        {
            try { Directory.Delete(clonedPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task IsolatedMergeRepo_PipelineSurfacesGitHostStagingFailure()
    {
        // When IGitHost.GetMergeStagingRoot cannot derive a staging path,
        // CreateIsolatedMergeRepositoryAsync must propagate the failure
        // unchanged so the orchestrator surfaces a meaningful error rather
        // than crashing later inside `git clone`.
        var rootlessHost = new RootlessGitHost();
        var workItemId = WorkItemId.New();
        var repoId = "any-repo";

        var pipeline = CreatePipeline(rootlessHost);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None));
        Assert.Contains("merge staging root", ex.Message);
    }

    [Fact]
    public void GetMergeStagingRoot_DefaultDerivesFromRepoParent()
    {
        // LocalGitHost takes the default IGitHost.GetMergeStagingRoot — which
        // returns the directory containing the bare repo path. This pins the
        // default's behaviour so a future override or interface change is
        // intentional.
        var gitRoot = Path.Combine(_workspace, "git-root-default-test");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var repoId = "abc123";
        IGitHost asInterface = gitHost;

        var stagingRoot = asInterface.GetMergeStagingRoot(repoId);

        Assert.Equal(gitRoot, stagingRoot);
        Assert.Equal(Path.GetDirectoryName(asInterface.GetRepoPath(repoId)), stagingRoot);
    }

    [Fact]
    public void GetMergeStagingRoot_DefaultThrowsWhenRepoPathHasNoParent()
    {
        // The default's safety net: when GetRepoPath returns a root-level
        // path (no parent), the default impl must throw with a clear
        // operator-readable message rather than silently staging in a
        // bogus location.
        IGitHost rootHost = new RootRepoPathHost();

        var ex = Assert.Throws<InvalidOperationException>(
            () => rootHost.GetMergeStagingRoot("any"));
        Assert.Contains("merge staging root", ex.Message);
    }

    private async Task<string> CreateSeedRepoAsync()
    {
        var seed = Path.Combine(_workspace, "seed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(seed);
        await TestSupport.RunGit(seed, "init", "-b", "main");
        await TestSupport.RunGit(seed, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(seed, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "file.txt"), "base\n");
        await TestSupport.RunGit(seed, "add", "file.txt");
        await TestSupport.RunGit(seed, "commit", "-m", "base");
        return seed;
    }

    private PipelineRunner CreatePipeline(IGitHost gitHost)
    {
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = "unused",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit(),
        };
        return new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new AgentRegistry([new ScriptedAgent([])]),
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            new InMemoryProjectRepository(project),
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            new SqliteWorkItemStore(stateDb),
            new NullWebhookDispatcher(),
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance);
    }

    /// <summary>
    /// Spy host that delegates everything to LocalGitHost but records each
    /// GetMergeStagingRoot call. Lets a test pin that the pipeline routed
    /// the staging decision through IGitHost rather than re-deriving it.
    /// </summary>
    private sealed class StagingRootRecordingHost : IGitHost
    {
        private readonly LocalGitHost _inner;
        public List<string> StagingRootCalls { get; } = [];

        public StagingRootRecordingHost(LocalGitHost inner) => _inner = inner;

        public string GetMergeStagingRoot(string repositoryId)
        {
            StagingRootCalls.Add(repositoryId);
            return ((IGitHost)_inner).GetMergeStagingRoot(repositoryId);
        }

        public string GetRepoPath(string repositoryId) => _inner.GetRepoPath(repositoryId);
        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) => _inner.GetSandboxAccess(repositoryId);
        public SandboxRepositoryAccess GetIsolatedRepoSandboxAccess(string isolatedRepoHostPath)
            => _inner.GetIsolatedRepoSandboxAccess(isolatedRepoHostPath);
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => _inner.EnsureRepositoryAsync(id, seedFromUrl, ct);
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => _inner.EnsureRepositoryAsync(id, seedFromUrl, baseBranch, ct);
        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => _inner.GetDefaultBranchAsync(repositoryId, ct);
        public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => _inner.PushToUpstreamAsync(repositoryId, upstreamUrl, branch, upstreamEnv, reconcileStrategy, ct);
        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => _inner.DisposeRepositoryAsync(repositoryId, ct);
        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => _inner.RepositoryExistsAsync(id, ct);
        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
            => _inner.GetDiffAsync(repositoryId, baseBranch, workBranch, ct);
    }

    /// <summary>
    /// Host whose GetRepoPath surfaces a value where Path.GetDirectoryName
    /// returns null (e.g. an empty string). Exercises the default
    /// GetMergeStagingRoot's "cannot derive" guard via the pipeline.
    /// </summary>
    private sealed class RootlessGitHost : IGitHost
    {
        public string GetRepoPath(string repositoryId) => "";
        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) =>
            throw new NotSupportedException();
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default) =>
            Task.FromResult("rootless");
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default) =>
            Task.FromResult("rootless");
        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default) =>
            Task.FromResult("main");
        public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default) =>
            Task.FromResult((string.Empty, string.Empty));
    }

    /// <summary>
    /// Host whose GetRepoPath returns a directory root (e.g. "/"), making
    /// Path.GetDirectoryName return null at the interface boundary.
    /// </summary>
    private sealed class RootRepoPathHost : IGitHost
    {
        public string GetRepoPath(string repositoryId) =>
            OperatingSystem.IsWindows() ? "C:\\" : "/";
        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) =>
            throw new NotSupportedException();
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default) =>
            Task.FromResult("root");
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default) =>
            Task.FromResult("root");
        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default) =>
            Task.FromResult("main");
        public Task PushToUpstreamAsync(string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default) =>
            Task.FromResult(true);
        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default) =>
            Task.FromResult((string.Empty, string.Empty));
    }
}

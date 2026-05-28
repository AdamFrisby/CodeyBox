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
    public async Task IsolatedMergeRepo_ParallelStagingCreations_AllProduceUniqueValidClones()
    {
        // Parallel CreateIsolatedMergeRepositoryAsync calls against the same
        // bare repo must each yield a distinct on-disk staging directory.
        // Without per-call uniqueness, two merges launched back-to-back
        // (audit-parallelism or retry interleavings) would clobber each
        // other's clones. Pinning Guid-based filename uniqueness here lets
        // a future change to the path scheme fail this test rather than
        // surface as a hard-to-diagnose "isolated repo missing" at mount.
        var gitRoot = Path.Combine(_workspace, "git-root-parallel-clones");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);
        var pipeline = CreatePipeline(gitHost);

        var stagings = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None))
            .ToArray());

        try
        {
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
    public async Task SandboxLeakReaper_DoesNotEnumerateOrTouchHostFilesystem()
    {
        // Pins the contract behind the merge-staging fix: SandboxLeakReaper
        // operates purely on ISandboxProvider.ListAllManagedAsync — the
        // sandbox provider's VM registry — and must NEVER walk host
        // filesystem paths to find leaked directories. If a future change
        // adds host-side directory cleanup to the reaper, it must explicitly
        // exclude in-flight merge staging (codeybox-merge-*.git) or this
        // test breaks loudly.
        //
        // The host directory layout below is what the reaper would have to
        // crawl to disturb merge staging; we assert ListAllManagedAsync is
        // the only entry point exercised during a sweep.
        var stagingDir = Path.Combine(_workspace, "leak-reaper-host-paths");
        Directory.CreateDirectory(stagingDir);
        var mergeStaging = Path.Combine(stagingDir, $"codeybox-merge-{Guid.NewGuid():N}.git");
        Directory.CreateDirectory(mergeStaging);
        File.WriteAllText(Path.Combine(mergeStaging, "HEAD"), "ref: refs/heads/main\n");

        var reaperProvider = new ListAllOnlySandboxProvider();
        var reaperOpts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromHours(1),
            LeakAgeThreshold = TimeSpan.FromMinutes(30),
            PreemptRetention = TimeSpan.FromHours(24),
            AutoDispose = true,
            MaxConcurrentAutoDispose = 4,
        };
        var reaper = new SandboxLeakReaper(
            reaperProvider,
            new NullWebhookDispatcher(),
            reaperOpts,
            NullLogger<SandboxLeakReaper>.Instance);

        await reaper.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, reaperProvider.ListCalls);
        Assert.True(Directory.Exists(mergeStaging), "reaper must not touch host merge-staging directories");
        Assert.True(File.Exists(Path.Combine(mergeStaging, "HEAD")),
            "reaper must not modify host merge-staging contents");
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
    public async Task RestoreIsolatedMergeRepository_RecreatesMissingClone()
    {
        // The defensive heal path: if the staging directory disappears
        // between create-time and mount-time, the orchestrator's restore
        // callback re-clones the bare repo into the same target path so the
        // mount retry succeeds. Without this, a racing cleanup would kill
        // the work item.
        var gitRoot = Path.Combine(_workspace, "git-root-heal");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);
        var pipeline = CreatePipeline(gitHost);

        var stagingPath = await pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None);
        try
        {
            // Simulate racing cleanup wiping the staging dir.
            Directory.Delete(stagingPath, recursive: true);
            Assert.False(Directory.Exists(stagingPath));

            await pipeline.RestoreIsolatedMergeRepositoryAsync(repoId, stagingPath, CancellationToken.None);

            Assert.True(Directory.Exists(stagingPath), "restore must re-clone the bare repo at the original path");
            Assert.True(File.Exists(Path.Combine(stagingPath, "HEAD")),
                "restored clone must be a valid bare git repository");
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RestoreIsolatedMergeRepository_OverwritesPartialResidueAtTargetPath()
    {
        // git clone refuses to write into a non-empty target. If a partial
        // directory was left at the original path (interrupted cleanup, a
        // stray file from a previous attempt), the restore call must clear
        // it first so the re-clone succeeds. Without this guard the heal
        // path itself would fail and the work item still dies.
        var gitRoot = Path.Combine(_workspace, "git-root-heal-residue");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);
        var pipeline = CreatePipeline(gitHost);

        var stagingPath = await pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None);
        try
        {
            // Leave a partial residue at the staging path.
            Directory.Delete(stagingPath, recursive: true);
            Directory.CreateDirectory(stagingPath);
            File.WriteAllText(Path.Combine(stagingPath, "stale-marker"), "leftover");

            await pipeline.RestoreIsolatedMergeRepositoryAsync(repoId, stagingPath, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(stagingPath, "HEAD")),
                "restored clone must be a valid bare git repo (HEAD missing — partial residue likely blocked the clone)");
            Assert.False(File.Exists(Path.Combine(stagingPath, "stale-marker")),
                "leftover files from the prior partial state must be removed before the re-clone");
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task AttachIsolatedRepoRestoreHook_OnlyAddsRestoreToMatchingMount()
    {
        // The orchestrator wraps GetIsolatedRepoSandboxAccess with a
        // restore-hook attacher so the SandboxMount targeting the isolated
        // bare clone carries the self-heal callback. Mounts that do not
        // point at the isolated clone path (credential mounts, tmpfs slots,
        // host artifacts) must NOT inherit the hook — only the merge clone
        // is recoverable by re-running git clone.
        var gitRoot = Path.Combine(_workspace, "git-root-attach-hook");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);
        var pipeline = CreatePipeline(gitHost);

        var stagingPath = await pipeline.CreateIsolatedMergeRepositoryAsync(repoId, workItemId, CancellationToken.None);
        try
        {
            var baseAccess = ((IGitHost)gitHost).GetIsolatedRepoSandboxAccess(stagingPath);
            // Inject an unrelated additional mount to assert the hook is
            // attached selectively, not broadcast across every mount in the
            // access object.
            var unrelatedMount = new SandboxMount
            {
                SandboxPath = "/audit",
                Tmpfs = true,
                SizeBytes = 1024,
            };
            baseAccess = baseAccess with
            {
                Mounts = baseAccess.Mounts.Concat(new[] { unrelatedMount }).ToArray(),
            };

            var hooked = pipeline.AttachIsolatedRepoRestoreHook(baseAccess, repoId, stagingPath);

            var hookedRepoMount = Assert.Single(hooked.Mounts, m => m.HostPath == stagingPath);
            Assert.NotNull(hookedRepoMount.RestoreHostSourceAsync);
            var hookedTmpfs = Assert.Single(hooked.Mounts, m => m.Tmpfs);
            Assert.Null(hookedTmpfs.RestoreHostSourceAsync);

            // Exercise the wired callback end-to-end: deleting the staging
            // dir, invoking the hook, and re-stating must show the bare
            // clone has been recreated under the same path.
            Directory.Delete(stagingPath, recursive: true);
            await hookedRepoMount.RestoreHostSourceAsync!(CancellationToken.None);
            Assert.True(File.Exists(Path.Combine(stagingPath, "HEAD")),
                "restore callback wired by the orchestrator must re-clone the bare repo at the original path");
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void LocalGitHost_GetIsolatedRepoSandboxAccess_BindsAtRepoMountPathWithDeniedNetwork()
    {
        // LocalGitHost wires an isolated bare clone the same way the durable
        // bare repo is wired (read-write bind mount at /repo, deny network)
        // so the agent inside the sandbox sees an identical clone URL
        // regardless of which on-host path is mounted. This pins that
        // contract: a regression that flipped ReadOnly, dropped the network
        // policy, or changed the sandbox mount path would silently change
        // merge-phase semantics in production.
        var gitRoot = Path.Combine(_workspace, "git-root-isolated-access");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var isolatedHostPath = Path.Combine(gitRoot, "codeybox-merge-fake.git");

        var access = ((IGitHost)gitHost).GetIsolatedRepoSandboxAccess(isolatedHostPath);

        Assert.Equal(LocalGitHost.SandboxRepoMountPath, access.CloneUrlInsideSandbox);
        Assert.Equal(SandboxNetworkPolicy.Denied, access.Network);
        var mount = Assert.Single(access.Mounts);
        Assert.Equal(LocalGitHost.SandboxRepoMountPath, mount.SandboxPath);
        Assert.Equal(isolatedHostPath, mount.HostPath);
        Assert.False(mount.ReadOnly, "merge sandbox must be able to push verification refs back to the isolated bare clone");
        Assert.False(mount.Tmpfs);
    }

    [Fact]
    public void LocalGitHost_GetIsolatedRepoSandboxAccess_LayoutMatchesGetSandboxAccess()
    {
        // The agent inside the sandbox must observe the same clone URL and
        // mount layout whether the orchestrator wires the durable bare repo
        // or an isolated bare clone — otherwise the merge prompt's `git
        // clone /repo` would target a path that only exists for one of the
        // two flows. Pin the two access objects' mount layout matches.
        var gitRoot = Path.Combine(_workspace, "git-root-layout-parity");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var asInterface = (IGitHost)gitHost;

        // GetSandboxAccess uses GetRepoPath(repoId) for HostPath; we cannot
        // call it without a real repoId in the registry, so derive the
        // comparable layout via the public mount-path constant.
        var isolatedAccess = asInterface.GetIsolatedRepoSandboxAccess(
            Path.Combine(gitRoot, "any-isolated.git"));

        Assert.Equal(LocalGitHost.SandboxRepoMountPath, isolatedAccess.CloneUrlInsideSandbox);
        Assert.Equal(SandboxNetworkPolicy.Denied, isolatedAccess.Network);
        var mount = Assert.Single(isolatedAccess.Mounts);
        Assert.Equal(LocalGitHost.SandboxRepoMountPath, mount.SandboxPath);
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

    /// <summary>
    /// ISandboxProvider that counts ListAllManagedAsync calls and refuses any
    /// other method. Used to pin that SandboxLeakReaper sweeps only enumerate
    /// the provider's VM registry — never walk host filesystem paths. Any new
    /// host-side cleanup in the reaper would fail loudly here.
    /// </summary>
    private sealed class ListAllOnlySandboxProvider : ISandboxProvider
    {
        public int ListCalls { get; private set; }
        public string Name => "list-only-fake";

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        }

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => throw new InvalidOperationException(
                "reaper attempted to dispose a host artifact — host filesystem cleanup is not the reaper's job");
    }
}

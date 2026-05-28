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
    public async Task ConcurrentReaperSweepsRunningDuringMergeStaging_LeaveAllStagingDirsIntact()
    {
        // Acceptance criterion #2: simulate SandboxLeakReaper running
        // concurrently with merge-phase setup, assert the merge staging
        // directories survive end-to-end. The test runs a tight reaper-sweep
        // loop in the background while a parallel batch of
        // CreateIsolatedMergeRepositoryAsync calls produces fresh staging
        // clones. The contract being pinned: a real reaper instance, ticking
        // at the same time merge staging is mid-flight, must not delete or
        // otherwise mutate any of the freshly-created codeybox-merge-*.git
        // directories before the orchestrator hands them off to the mount.
        //
        // The reaper's only contract is to call ISandboxProvider.ListAllManagedAsync
        // — never the host filesystem — so the staging-host-path-vs-VM-name
        // distinction is what protects merge staging. If a future change to
        // the reaper started walking the host filesystem (e.g. "also clean
        // stale directories under /tmp"), it would have to whitelist
        // codeybox-merge-*.git paths or this test breaks loudly.
        var gitRoot = Path.Combine(_workspace, "git-root-concurrent");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);
        var pipeline = CreatePipeline(gitHost);

        // Empty-listing provider keeps the reaper from disposing anything real;
        // we are only verifying the reaper does not touch merge staging.
        var reaperProvider = new ListAllOnlySandboxProvider();
        var reaperOpts = new SandboxLeakOptions
        {
            Enabled = true,
            CheckInterval = TimeSpan.FromMinutes(1),
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

        using var reaperLoopCts = new CancellationTokenSource();
        // Background sweeps running while staging is in flight.
        var reaperLoop = Task.Run(async () =>
        {
            while (!reaperLoopCts.IsCancellationRequested)
            {
                await reaper.RunSweepAsync(reaperLoopCts.Token);
                // Yield without sleeping so the loop competes for CPU with
                // the staging-creation tasks below.
                await Task.Yield();
            }
        }, reaperLoopCts.Token);

        // Concurrent staging creates. Use a small task fan-out so we hit
        // overlap windows where a reaper sweep can complete between
        // CreateIsolatedMergeRepositoryAsync's git clone and the test's
        // post-clone assertion. Each task verifies its OWN clone is present
        // immediately after CreateIsolatedMergeRepositoryAsync returns.
        var inflightStagings = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                var clone = await pipeline.CreateIsolatedMergeRepositoryAsync(
                    repoId, workItemId, CancellationToken.None);
                // Sleep a non-zero amount of "time the orchestrator would
                // be spawning a sandbox" so the reaper has further chances
                // to sweep before we re-stat the directory.
                await Task.Delay(50);
                return clone;
            }))
            .ToArray());

        try
        {
            // Allow the reaper a final sweep window before stopping it so the
            // last "is the staging dir still there" assertion runs against a
            // state where the reaper had every opportunity to interfere.
            await Task.Delay(100);
            reaperLoopCts.Cancel();
            try { await reaperLoop; } catch (OperationCanceledException) { /* expected */ }

            // The acceptance invariant: every concurrently-created staging
            // dir is still a valid bare git repo on disk after the reaper
            // ran many sweeps overlapping with the create phase. Each
            // staging dir MUST sit under the configured GitRoot (not under
            // Path.GetTempPath()) — that placement is the structural fix
            // for the b044f8bd incident, and the regression guard below
            // would fail this test if a future change reverted to /tmp
            // staging even while the reaper-doesn't-touch-host-fs contract
            // was preserved.
            var tempPathNormalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            foreach (var path in inflightStagings)
            {
                Assert.True(Directory.Exists(path),
                    $"merge staging dir reaped mid-flight: {path}");
                Assert.True(File.Exists(Path.Combine(path, "HEAD")),
                    $"merge staging dir corrupted mid-flight: {path}");
                Assert.Equal(gitRoot, Path.GetDirectoryName(path));
                Assert.NotEqual(
                    tempPathNormalized,
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetDirectoryName(path)!)));
            }

            // The reaper enumerated its provider but never touched the host
            // filesystem — the only safe contract for merge staging in the
            // current architecture.
            Assert.True(reaperProvider.ListCalls > 0,
                "reaper sweep loop did not run any sweeps — concurrency window not exercised");
        }
        finally
        {
            foreach (var path in inflightStagings)
            {
                try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
            }
        }
    }

    [Fact]
    public async Task ExternalCleanupReapingStagingMidFlight_RestorerHealsItRepeatedly()
    {
        // Active-simulation companion to
        // ConcurrentReaperSweepsRunningDuringMergeStaging_LeaveAllStagingDirsIntact:
        // that test pins the current reaper contract (provider-list-only,
        // never touches the host filesystem). This test pins what would
        // happen if the contract were violated — e.g. a future
        // SandboxLeakReaper variant gained host-side directory cleanup,
        // or an external tool like tmpwatch reaped the staging dir between
        // CreateIsolatedMergeRepositoryAsync and the mount call.
        //
        // The orchestrator-side fix is the ISandboxMountSourceRestorer
        // wired by AttachIsolatedRepoRestoreHook: when the sandbox provider
        // sees a missing host source at mount time, it invokes the
        // restorer, which re-clones the bare repo at the same path. This
        // test simulates repeated reaping by deleting the staging dir and
        // re-invoking the restorer in a sequential loop, mirroring the
        // pattern of repeated mount-retry-with-heal that would run if an
        // external tool reaped the dir multiple times during a long-running
        // mount window. After every reap, the next restore call must
        // recreate a valid bare clone at the same path.
        //
        // Without ISandboxMountSourceRestorer (the pre-fix state), no
        // amount of looping would recover the directory because the
        // orchestrator never re-cloned at mount time.
        //
        // Sequential rather than truly-concurrent on purpose: the production
        // restore path is invoked at most once per mount retry attempt and
        // is not re-entrant. Two concurrent RestoreAsync calls race
        // DeleteDirectoryBestEffort against `git clone`'s pre-flight check
        // and would deadlock or fail spuriously — neither of which models a
        // real reaper. The loop below simulates "reaper deletes, restorer
        // heals, mount succeeds" repeatedly to demonstrate convergence.
        var gitRoot = Path.Combine(_workspace, "git-root-deletion-race");
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
            // Production restorer wiring: AttachIsolatedRepoRestoreHook
            // builds the same ISandboxMountSourceRestorer the merge phase
            // would receive in production.
            var access = ((IGitHost)gitHost).GetIsolatedRepoSandboxAccess(stagingPath);
            var hooked = pipeline.AttachIsolatedRepoRestoreHook(access, repoId, stagingPath);
            var restorer = hooked.Mounts.Single(m => m.HostPath == stagingPath).SourceRestorer!;

            // Five reap-then-restore iterations. Each iteration deletes the
            // staging dir (the simulated reap), then asks the restorer to
            // recreate it. After every restore the directory must be a
            // valid bare repository — a regression that broke the heal
            // path on any iteration would fail this test immediately.
            for (var i = 0; i < 5; i++)
            {
                Assert.True(Directory.Exists(stagingPath),
                    $"iteration {i}: staging dir missing before simulated reap");
                Directory.Delete(stagingPath, recursive: true);
                Assert.False(Directory.Exists(stagingPath));

                await restorer.RestoreAsync(CancellationToken.None);

                Assert.True(Directory.Exists(stagingPath),
                    $"iteration {i}: restore did not recreate {stagingPath}");
                Assert.True(File.Exists(Path.Combine(stagingPath, "HEAD")),
                    $"iteration {i}: restored staging dir is not a valid bare git repository");
            }
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
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
            Assert.NotNull(hookedRepoMount.SourceRestorer);
            var hookedTmpfs = Assert.Single(hooked.Mounts, m => m.Tmpfs);
            Assert.Null(hookedTmpfs.SourceRestorer);

            // Exercise the wired restorer end-to-end: deleting the staging
            // dir, invoking the restorer, and re-stating must show the bare
            // clone has been recreated under the same path.
            Directory.Delete(stagingPath, recursive: true);
            await hookedRepoMount.SourceRestorer!.RestoreAsync(CancellationToken.None);
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
    public async Task LocalGitHost_GetIsolatedRepoSandboxAccess_LayoutMatchesGetSandboxAccess()
    {
        // The agent inside the sandbox must observe the same clone URL and
        // mount layout whether the orchestrator wires the durable bare repo
        // or an isolated bare clone — otherwise the merge prompt's `git
        // clone /repo` would target a path that only exists for one of the
        // two flows. Pin parity by calling BOTH GetSandboxAccess (with a real
        // repoId from EnsureRepositoryAsync) and GetIsolatedRepoSandboxAccess
        // (pointed at a hypothetical sibling staging directory), then
        // comparing the public mount-shape fields that the agent observes.
        // A regression that changed mount count, sandbox path, ReadOnly,
        // clone URL, or network policy on either side would fail this test.
        var gitRoot = Path.Combine(_workspace, "git-root-layout-parity");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var asInterface = (IGitHost)gitHost;

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var durableAccess = asInterface.GetSandboxAccess(repoId);
        var isolatedAccess = asInterface.GetIsolatedRepoSandboxAccess(
            Path.Combine(gitRoot, "any-isolated.git"));

        // Clone URL the agent sees must be identical so the same merge
        // prompt (`git clone /repo`) works in both flows.
        Assert.Equal(durableAccess.CloneUrlInsideSandbox, isolatedAccess.CloneUrlInsideSandbox);
        Assert.Equal(LocalGitHost.SandboxRepoMountPath, isolatedAccess.CloneUrlInsideSandbox);

        // Network policy must match — both flows deny network so the
        // sandbox cannot reach upstream during merge resolution.
        Assert.Equal(durableAccess.Network, isolatedAccess.Network);
        Assert.Equal(SandboxNetworkPolicy.Denied, isolatedAccess.Network);

        // Mount shape parity: same count, same sandbox path, same ReadOnly,
        // same Tmpfs (none). HostPath legitimately differs (durable bare repo
        // vs. isolated staging clone) — that is the whole point of the two
        // flows existing.
        Assert.Equal(durableAccess.Mounts.Count, isolatedAccess.Mounts.Count);
        var durableMount = Assert.Single(durableAccess.Mounts);
        var isolatedMount = Assert.Single(isolatedAccess.Mounts);
        Assert.Equal(durableMount.SandboxPath, isolatedMount.SandboxPath);
        Assert.Equal(durableMount.ReadOnly, isolatedMount.ReadOnly);
        Assert.Equal(durableMount.Tmpfs, isolatedMount.Tmpfs);
        Assert.False(isolatedMount.ReadOnly,
            "merge sandbox must be able to push verification refs back to the isolated bare clone");
    }

    [Fact]
    public void GetMergeStagingRoot_DefaultReturnsRepoParentDirectory_FixedLayoutHost()
    {
        // The default IGitHost.GetMergeStagingRoot implementation must return
        // the parent directory of the bare repo path — that is the invariant
        // PipelineRunner relies on so a single configured GitRoot covers both
        // the durable bare repo and the merge staging clone. Pin the
        // invariant with a fake host that returns a known fixed bare-repo
        // path so the test does not depend on the default's own expression
        // (a regression that shifted by one directory level would still pass
        // a test that mirrored the body).
        IGitHost fixedHost = new FixedRepoPathHost(
            barePath: OperatingSystem.IsWindows()
                ? @"C:\opt\codeybox\repos\b044f8bd.git"
                : "/opt/codeybox/repos/b044f8bd.git");

        var stagingRoot = fixedHost.GetMergeStagingRoot("anything");

        Assert.Equal(
            OperatingSystem.IsWindows() ? @"C:\opt\codeybox\repos" : "/opt/codeybox/repos",
            stagingRoot);
    }

    [Fact]
    public void GetMergeStagingRoot_LocalGitHostDelegatesToConfiguredRoot()
    {
        // Independent invariant for LocalGitHost: the staging root matches
        // the operator-configured GitRoot directory (so a single
        // CodeyBox.GitRootDirectory setting covers both the durable bare
        // repo and the merge staging clone). Driven by LocalGitHost's
        // GetRepoPath layout — not by the default impl body — so a future
        // LocalGitHost override that produced the wrong directory would
        // fail this test.
        var gitRoot = Path.Combine(_workspace, "git-root-local-staging");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var stagingRoot = ((IGitHost)gitHost).GetMergeStagingRoot("abc123");

        Assert.Equal(gitRoot, stagingRoot);
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

    [Fact]
    public void GetMergeStagingRoot_DefaultThrowsWhenRepoPathHasEmptyParent()
    {
        // Regression: on Unix, Path.GetDirectoryName returns string.Empty
        // (not null) for a bare-repo path with no directory component (e.g.
        // "id.git"). The previous `?? throw` guard only caught null, which
        // meant staging silently landed in the orchestrator process CWD
        // — bypassing the GitRoot / snap-mountable-layout invariant this
        // method exists to enforce. The default must reject both null and
        // empty parents with the same operator-readable failure.
        IGitHost rootlessHost = new RootlessGitHost();

        var ex = Assert.Throws<InvalidOperationException>(
            () => rootlessHost.GetMergeStagingRoot("any"));
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
    /// Host whose GetRepoPath returns a known fixed path so the default
    /// GetMergeStagingRoot test can pin "parent directory" semantically
    /// without mirroring the default implementation's expression.
    /// </summary>
    private sealed class FixedRepoPathHost : IGitHost
    {
        private readonly string _barePath;
        public FixedRepoPathHost(string barePath) => _barePath = barePath;
        public string GetRepoPath(string repositoryId) => _barePath;
        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) =>
            throw new NotSupportedException();
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default) =>
            Task.FromResult("fixed");
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default) =>
            Task.FromResult("fixed");
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

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
    public async Task RestoreIsolatedMergeRepository_RepeatedReapAndRestore_AlwaysConverges()
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
        // The orchestrator-side fix is
        // CreateMergeSandboxWithStagingRestoreAsync: when the sandbox
        // provider throws SandboxMountSourceMissingException naming the
        // staging path, the orchestrator calls
        // RestoreIsolatedMergeRepositoryAsync to re-clone and retries
        // CreateAsync. This test exercises RestoreIsolatedMergeRepositoryAsync
        // directly under repeated reap simulation to demonstrate it is
        // idempotent — each subsequent call after a reap recreates a valid
        // bare clone at the same path.
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
            // Five reap-then-restore iterations. Each iteration deletes the
            // staging dir (the simulated reap), then calls
            // RestoreIsolatedMergeRepositoryAsync to recreate it. After
            // every restore the directory must be a valid bare repository
            // — a regression that broke the heal path on any iteration
            // would fail this test immediately.
            for (var i = 0; i < 5; i++)
            {
                Assert.True(Directory.Exists(stagingPath),
                    $"iteration {i}: staging dir missing before simulated reap");
                Directory.Delete(stagingPath, recursive: true);
                Assert.False(Directory.Exists(stagingPath));

                await pipeline.RestoreIsolatedMergeRepositoryAsync(repoId, stagingPath, CancellationToken.None);

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
    public async Task IsolatedMergeRepo_PipelineRoutesCloneThroughGitHost()
    {
        // The pipeline must defer the whole isolated-merge-clone lifecycle to
        // IGitHost; this pins the contract so that a future GitHub-backed or
        // in-memory host (whose layout differs from LocalGitHost's flat
        // siblings) can stage wherever its sandbox provider allows by
        // overriding CreateIsolatedMergeCloneAsync. Without this routing, the
        // orchestrator would couple itself to bare-repo filesystem layout —
        // the architecture finding the iteration-9 audit flagged.
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
            // Primary invariant: the orchestrator went through
            // IGitHost.CreateIsolatedMergeCloneAsync rather than re-implementing
            // the clone itself. A regression that inlined the clone back into
            // PipelineRunner (the iteration-9 architecture finding) would fail
            // this assertion.
            Assert.Contains(repoId, spyHost.CreateIsolatedMergeCloneCalls);
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
    public void VerifyIsolatedMergeRepositoryOnDisk_MissingDirectory_ThrowsForCreate()
    {
        // Pins the post-clone verification helper that LocalGitHost calls
        // immediately after `git clone --bare` returns. A silent partial
        // clone or an external process that removed the directory between
        // clone-exit and verification must surface the
        // InvalidOperationException here instead of as a confusing "Source
        // path does not exist" mount failure later — the exact failure
        // class b044f8bd tracked.
        var missing = Path.Combine(_workspace, "create-nonexistent-staging.git");
        Assert.False(Directory.Exists(missing));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGitHost.VerifyIsolatedMergeCloneOnDisk(missing, "create"));

        Assert.Contains("isolated merge clone create did not land on disk", ex.Message);
        Assert.Contains(missing, ex.Message);
        Assert.Contains("exists=False", ex.Message);
        Assert.Contains("head=False", ex.Message);
    }

    [Fact]
    public void VerifyIsolatedMergeRepositoryOnDisk_MissingDirectory_ThrowsForRestore()
    {
        // The restore call site mirrors the create call site; the same
        // helper must produce a restore-flavored error message so
        // operators reading the audit trail can tell which call failed
        // when only the message is logged.
        var missing = Path.Combine(_workspace, "restore-nonexistent-staging.git");
        Assert.False(Directory.Exists(missing));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGitHost.VerifyIsolatedMergeCloneOnDisk(missing, "restore"));

        Assert.Contains("isolated merge clone restore did not land on disk", ex.Message);
        Assert.Contains(missing, ex.Message);
        Assert.Contains("exists=False", ex.Message);
        Assert.Contains("head=False", ex.Message);
    }

    [Fact]
    public void VerifyIsolatedMergeRepositoryOnDisk_DirectoryExistsButHeadMissing_Throws()
    {
        // A partial clone (or a freshly-created empty directory residue
        // from an interrupted prior attempt) presents as the directory
        // existing but HEAD missing. The verification helper must
        // distinguish this from "valid bare repo" — re-cloning into the
        // residue is how the restore path recovers, but only if the helper
        // says "not valid" first.
        var partial = Path.Combine(_workspace, "partial-clone.git");
        Directory.CreateDirectory(partial);
        File.WriteAllText(Path.Combine(partial, "objects"), "stray file, not a HEAD");
        Assert.True(Directory.Exists(partial));
        Assert.False(File.Exists(Path.Combine(partial, "HEAD")));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            LocalGitHost.VerifyIsolatedMergeCloneOnDisk(partial, "create"));

        Assert.Contains("did not land on disk", ex.Message);
        Assert.Contains("exists=True", ex.Message);
        Assert.Contains("head=False", ex.Message);
    }

    [Fact]
    public void VerifyIsolatedMergeRepositoryOnDisk_ValidBareCloneLayout_DoesNotThrow()
    {
        // Happy-path companion: when the helper sees a directory with a
        // HEAD file, it must return without throwing. Pairs with the
        // failure-path tests above so a regression that inverted the
        // condition (e.g. `if (dirExists && headExists)` instead of
        // `if (!dirExists || !headExists)`) would fail this test.
        var valid = Path.Combine(_workspace, "valid-clone.git");
        Directory.CreateDirectory(valid);
        File.WriteAllText(Path.Combine(valid, "HEAD"), "ref: refs/heads/main\n");

        LocalGitHost.VerifyIsolatedMergeCloneOnDisk(valid, "create");
    }

    [Fact]
    public async Task RestoreIsolatedMergeRepository_OverwritesEmptyResidueAndSucceeds()
    {
        // End-to-end happy-path exercise of the restore verification: an
        // empty directory at the target (interrupted prior attempt, or
        // a fresh CreateDirectory call) must be cleared by the restore
        // path and replaced by a valid bare clone. Belt-and-suspenders
        // alongside the LocalGitHost.VerifyIsolatedMergeCloneOnDisk unit
        // tests above: those cover the failure-message contract; this
        // one runs a real `git clone --bare` so a regression that broke
        // the residue-overwrite or post-clone HEAD check would surface
        // here. The full `_Throws` failure path is impossible to
        // deterministically simulate without a process-runner seam (we
        // would need `git clone` to return 0 without producing HEAD),
        // so coverage of that scenario lives in the unit tests above.
        var gitRoot = Path.Combine(_workspace, "git-root-restore-integration");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);
        var pipeline = CreatePipeline(gitHost);

        var stagingPath = await pipeline.CreateIsolatedMergeRepositoryAsync(
            repoId, workItemId, CancellationToken.None);
        try
        {
            // Wipe the staging clone, leaving the directory itself in
            // place but empty — the failure mode the verifier catches.
            // RestoreIsolatedMergeRepositoryAsync overwrites the leftover
            // residue with a fresh clone, so a regression that removed
            // the verifier would still let this test pass (because the
            // clone succeeded and HEAD is now present). The point of
            // this test is to verify the SUCCESS path runs through the
            // verifier without throwing — paired with the failure-path
            // unit tests above on the helper, the call site is fully
            // covered.
            Directory.Delete(stagingPath, recursive: true);
            Directory.CreateDirectory(stagingPath);

            await pipeline.RestoreIsolatedMergeRepositoryAsync(repoId, stagingPath, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(stagingPath, "HEAD")),
                "restore must land a valid bare repo whose HEAD passes verification");
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RestoreIsolatedMergeRepository_RefusesPathOutsideStagingRoot()
    {
        // Containment guard: RestoreIsolatedMergeRepositoryAsync deletes
        // the target directory recursively before re-cloning. A future
        // wiring bug or a hostile IGitHost override that returned a path
        // outside the configured staging root would turn that delete into
        // an arbitrary host-directory removal (CWE-22). Pin the explicit
        // refusal so a regression that dropped the prefix check would
        // fail this test BEFORE any filesystem mutation runs.
        var gitRoot = Path.Combine(_workspace, "git-root-containment");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);
        var pipeline = CreatePipeline(gitHost);

        // A sibling directory the operator must NOT be able to lose: it
        // lives outside the staging root entirely. The recursive delete
        // would happily wipe it without the containment guard.
        var siblingWorkspace = Path.Combine(_workspace, "sibling-data");
        Directory.CreateDirectory(siblingWorkspace);
        File.WriteAllText(Path.Combine(siblingWorkspace, "important.txt"), "do not delete");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.RestoreIsolatedMergeRepositoryAsync(repoId, siblingWorkspace, CancellationToken.None));

        Assert.Contains("outside staging root", ex.Message);
        Assert.True(File.Exists(Path.Combine(siblingWorkspace, "important.txt")),
            "containment guard must fire BEFORE the recursive delete touches the sibling directory");
    }

    [Fact]
    public async Task CreateMergeSandboxWithStagingRestore_TwoConsecutiveMissingSourceFailures_PropagatesAndDoesNotRetryForever()
    {
        // The retry contract caps re-clones at MergeSandboxStagingRestoreAttempts
        // (production value: 2). If CreateAsync throws
        // SandboxMountSourceMissingException on the second consecutive
        // attempt — i.e. RestoreIsolatedMergeRepositoryAsync re-cloned, but
        // something reaped the path AGAIN before mount could land — the
        // orchestrator must propagate the original exception rather than
        // looping indefinitely. Without this test, a regression that
        // wrapped the retry loop in `while (true)` (or skipped the
        // attempt-count guard) would silently spin and starve the merge
        // phase.
        var gitRoot = Path.Combine(_workspace, "git-root-exhausted-retry");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var alwaysMissingProvider = new AlwaysMissingSourceSandboxProvider();
        var pipeline = CreatePipeline(gitHost, alwaysMissingProvider);

        var stagingPath = await pipeline.CreateIsolatedMergeRepositoryAsync(
            repoId, workItemId, CancellationToken.None);
        try
        {
            var access = ((IGitHost)gitHost).GetIsolatedRepoSandboxAccess(stagingPath);
            var spec = new SandboxSpec { ImageReference = "ignored", Mounts = access.Mounts };

            var ex = await Assert.ThrowsAsync<SandboxMountSourceMissingException>(() =>
                pipeline.CreateMergeSandboxWithStagingRestoreAsync(
                    spec, repoId, stagingPath, CancellationToken.None));

            // The exception that propagates IS the one CreateAsync threw on
            // the final attempt — operators tracing the failure must see
            // the multipass-shape root cause, not a wrapper.
            Assert.Equal(stagingPath, ex.HostPath);

            // Production cap is 2: one initial attempt + one re-clone +
            // retry = 2 total CreateAsync invocations. A regression that
            // looped past the cap or skipped restore-after-first-failure
            // would change this count.
            Assert.Equal(PipelineRunner.MergeSandboxStagingRestoreAttempts, alwaysMissingProvider.CreateAsyncCalls);
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task CreateMergeSandboxWithStagingRestore_MissingSourceForUnrelatedPath_PropagatesWithoutInvokingRestore()
    {
        // The catch filter on CreateMergeSandboxWithStagingRestoreAsync
        // matches SandboxMountSourceMissingException only when its HostPath
        // equals the staging clone. If the provider names a DIFFERENT path
        // (e.g. a credentials tmpfs, or a different mount that happens to
        // be missing), the orchestrator must NOT call
        // RestoreIsolatedMergeRepositoryAsync — running restore against the
        // wrong path would clobber whatever bind source the unrelated
        // failure actually pointed at.
        //
        // This test drives the mismatched-HostPath branch directly: a fake
        // sandbox provider throws SandboxMountSourceMissingException naming
        // an unrelated host path, and the test asserts (1) the exception
        // propagates unchanged, (2) the staging clone is still on disk
        // (restore never ran), and (3) the provider was called exactly
        // once.
        var gitRoot = Path.Combine(_workspace, "git-root-mismatched-path");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var unrelatedPath = Path.Combine(_workspace, "credentials-tmpfs-fake");
        var mismatchProvider = new MissingPathSandboxProvider(unrelatedPath);
        var pipeline = CreatePipeline(gitHost, mismatchProvider);

        var stagingPath = await pipeline.CreateIsolatedMergeRepositoryAsync(
            repoId, workItemId, CancellationToken.None);
        try
        {
            var access = ((IGitHost)gitHost).GetIsolatedRepoSandboxAccess(stagingPath);
            var spec = new SandboxSpec { ImageReference = "ignored", Mounts = access.Mounts };

            var ex = await Assert.ThrowsAsync<SandboxMountSourceMissingException>(() =>
                pipeline.CreateMergeSandboxWithStagingRestoreAsync(
                    spec, repoId, stagingPath, CancellationToken.None));

            Assert.Equal(unrelatedPath, ex.HostPath);
            // The staging clone must NOT have been re-cloned in response —
            // the orchestrator only heals when the missing path is the
            // staging clone itself.
            Assert.Equal(1, mismatchProvider.CreateAsyncCalls);
            // The staging clone must still be present on disk: restore was
            // never invoked, so the dir the orchestrator created in
            // CreateIsolatedMergeRepositoryAsync is untouched.
            Assert.True(Directory.Exists(stagingPath));
            Assert.True(File.Exists(Path.Combine(stagingPath, "HEAD")));
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task CreateIsolatedMergeClone_WritesInFlightMarkerInsideStagingDir()
    {
        // The in-flight marker is the documented atomicity contract for
        // host-side merge staging: any cleanup logic (orchestrator-side
        // reaper, future host-fs reaper, operator cron) must skip a staging
        // directory containing the marker. The marker exists for the lifetime
        // of the create-then-mount window and is removed alongside the
        // staging directory when the merge phase finishes (finally-block
        // DeleteDirectoryBestEffort). This test pins the convention so a
        // regression that dropped the marker write would surface
        // immediately, and so the marker file name remains a stable contract
        // visible to operators reading their own cleanup scripts.
        var gitRoot = Path.Combine(_workspace, "git-root-marker");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var stagingPath = await ((IGitHost)gitHost)
            .CreateIsolatedMergeCloneAsync(repoId, workItemId, CancellationToken.None);
        try
        {
            var markerPath = Path.Combine(stagingPath, IGitHost.IsolatedMergeCloneInFlightMarkerFileName);
            Assert.True(File.Exists(markerPath),
                $"in-flight marker must be present after create: {markerPath}");
            // The marker body names the work item so operators reading the
            // file can attribute the in-flight directory to its owner.
            var body = await File.ReadAllTextAsync(markerPath);
            Assert.Contains(workItemId.ToString(), body);

            // The SIBLING sentinel is the load-bearing artifact during the
            // create window: it must be present from before `git clone`
            // begins until DisposeIsolatedMergeCloneAsync removes it.
            // Without this assertion a regression that dropped
            // WriteInFlightSibling would still let every marker test pass
            // while re-exposing the mid-clone reap race the b044f8bd fix
            // targets.
            var siblingPath = stagingPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix;
            Assert.True(File.Exists(siblingPath),
                $"sibling in-flight sentinel must be present after create: {siblingPath}");
            var siblingBody = await File.ReadAllTextAsync(siblingPath);
            Assert.Contains(workItemId.ToString(), siblingBody);
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
            try { File.Delete(stagingPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task CreateIsolatedMergeClone_GitCloneFails_RemovesSiblingSentinelAndThrows()
    {
        // CreateIsolatedMergeCloneAsync writes the sibling sentinel BEFORE
        // running `git clone --bare`; the catch block is then responsible
        // for deleting that sentinel if the clone (or post-clone HEAD
        // verification) fails, so a failed create does not leave a stray
        // `.inflight` file next to a directory that was never written.
        // Without this test the failure branch — including its sentinel
        // cleanup — is unobserved; a regression that swallowed the
        // non-zero git exit, or removed the catch block, would not be
        // caught.
        //
        // Driving the failure: pass a repositoryId whose bare repo never
        // existed on disk. GetRepoPath simply constructs a path; the
        // staging-root directory is still created by the LocalGitHost
        // ctor so the clone process can launch, but the source path is
        // absent so `git clone --bare` exits non-zero.
        var gitRoot = Path.Combine(_workspace, "git-root-create-fail");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var workItemId = WorkItemId.New();
        const string nonexistentRepoId = "nonexistent-source-repo";
        Assert.False(Directory.Exists(gitHost.GetRepoPath(nonexistentRepoId)),
            "test invariant: source bare repo must not exist so git clone fails");

        // Snapshot any pre-existing staging-root contents so we can verify
        // the failed create did not leave stray `.inflight` siblings.
        var preExistingFiles = Directory.Exists(gitRoot)
            ? new HashSet<string>(Directory.EnumerateFiles(gitRoot), StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IGitHost)gitHost).CreateIsolatedMergeCloneAsync(
                nonexistentRepoId, workItemId, CancellationToken.None));

        Assert.Contains("git clone --bare for merge staging failed", ex.Message);

        // The catch block must have removed the sibling sentinel. We do not
        // know the exact target path (it embeds a fresh Guid), so we assert
        // no NEW `.inflight` file remains in the staging root.
        var leftoverSentinels = Directory.EnumerateFiles(gitRoot)
            .Where(p => p.EndsWith(IGitHost.IsolatedMergeCloneInFlightSiblingSuffix, StringComparison.Ordinal))
            .Where(p => !preExistingFiles.Contains(p))
            .ToArray();
        Assert.True(leftoverSentinels.Length == 0,
            "failed CreateIsolatedMergeCloneAsync must clean up sibling sentinels it wrote: " +
            string.Join(", ", leftoverSentinels));
    }

    [Fact]
    public async Task RestoreIsolatedMergeClone_ReWritesInFlightMarkerAfterRestore()
    {
        // The restore path is the heal branch for mid-flight disappearance;
        // it must re-establish the in-flight marker just like the create
        // path does, otherwise a successful restore would leave the staging
        // dir without protection against the next round of cleanup.
        var gitRoot = Path.Combine(_workspace, "git-root-marker-restore");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var stagingPath = await ((IGitHost)gitHost)
            .CreateIsolatedMergeCloneAsync(repoId, workItemId, CancellationToken.None);
        try
        {
            Directory.Delete(stagingPath, recursive: true);
            // The create-time sibling sentinel may still be sitting next
            // to the deleted directory (Dispose did not run). Wipe it so
            // the assertion below confirms the restore call REWROTE the
            // sentinel rather than just leaving the create-time one in
            // place — a regression that dropped WriteInFlightSibling
            // inside RestoreIsolatedMergeCloneAsync must fail this test.
            var siblingPath = stagingPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix;
            try { File.Delete(siblingPath); } catch { /* best-effort */ }
            Assert.False(File.Exists(siblingPath),
                "test invariant: sibling sentinel must be absent before restore so the assertion below pins the rewrite");

            await ((IGitHost)gitHost)
                .RestoreIsolatedMergeCloneAsync(repoId, stagingPath, CancellationToken.None);

            var markerPath = Path.Combine(stagingPath, IGitHost.IsolatedMergeCloneInFlightMarkerFileName);
            Assert.True(File.Exists(markerPath),
                $"in-flight marker must be re-written after restore: {markerPath}");
            // Sibling sentinel must also be present after restore — the
            // heal path mirrors the create path's in-flight protection,
            // and a regression that dropped the restore-time
            // WriteInFlightSibling would leave the staging dir vulnerable
            // to the same race the original fix targets.
            Assert.True(File.Exists(siblingPath),
                $"sibling in-flight sentinel must be re-written after restore: {siblingPath}");
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
            try { File.Delete(stagingPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RestoreIsolatedMergeClone_GitCloneFails_RemovesSiblingSentinelAndThrows()
    {
        // RestoreIsolatedMergeCloneAsync writes the sibling sentinel before
        // running `git clone --bare` to re-stage. The heal path mirrors the
        // create path's catch-block cleanup: a failed re-clone must throw
        // InvalidOperationException AND remove the sibling sentinel that
        // restore just wrote, so a failed heal does not leave a stray
        // `.inflight` file pinning a directory that is no longer present.
        // Without this test, a regression that swallowed the non-zero git
        // exit (or removed the catch block) would not be caught.
        //
        // Driving the failure: create a staging clone, then dispose the
        // source bare repo so the heal-path `git clone --bare` exits
        // non-zero. The staging dir is wiped before restore so the
        // sentinel write happens through the normal heal flow.
        var gitRoot = Path.Combine(_workspace, "git-root-restore-fail");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);

        var seed = await CreateSeedRepoAsync();
        var workItemId = WorkItemId.New();
        var repoId = await gitHost.EnsureRepositoryAsync(workItemId, seed);

        var stagingPath = await ((IGitHost)gitHost)
            .CreateIsolatedMergeCloneAsync(repoId, workItemId, CancellationToken.None);
        try
        {
            // Simulate the staging dir going missing AND the source bare
            // repo being unavailable when restore runs — `git clone --bare`
            // will exit non-zero because the source path no longer exists.
            Directory.Delete(stagingPath, recursive: true);
            var siblingPath = stagingPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix;
            try { File.Delete(siblingPath); } catch { /* best-effort */ }
            await gitHost.DisposeRepositoryAsync(repoId, CancellationToken.None);
            Assert.False(Directory.Exists(gitHost.GetRepoPath(repoId)),
                "test invariant: source bare repo must be gone so the restore clone fails");

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ((IGitHost)gitHost).RestoreIsolatedMergeCloneAsync(
                    repoId, stagingPath, CancellationToken.None));
            Assert.Contains("git clone --bare for merge restore failed", ex.Message);

            // The catch block must have removed the sentinel restore
            // wrote, so the leftover does not survive past the failed
            // heal call.
            Assert.False(File.Exists(siblingPath),
                $"failed RestoreIsolatedMergeCloneAsync must clean up the sibling sentinel it wrote: {siblingPath}");
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
            try { File.Delete(stagingPath + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix); } catch { /* best-effort */ }
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
    public void GetIsolatedRepoSandboxAccess_DefaultThrowsNotSupportedException_ForHostsThatDoNotBindMount()
    {
        // The default IGitHost.GetIsolatedRepoSandboxAccess implementation
        // throws NotSupportedException because not every host can bind-mount
        // an arbitrary bare-repo path (e.g. a host that exposes git only over
        // a network transport has nothing to mount). PipelineRunner's merge
        // and conflict-rework flows go through this entry point; a regression
        // that silently downgraded the default to "return null" or a default
        // SandboxRepositoryAccess would cause the orchestrator to wire up an
        // empty mount list and the agent inside the sandbox would fail at
        // `git clone /repo` with a confusing error rather than the host
        // surfacing a clear, operator-readable capability mismatch.
        //
        // Drive the default by invoking it on minimal IGitHost stubs that do
        // not override the method — both FixedRepoPathHost and RootlessGitHost
        // qualify. Pin the exact exception type AND that the message names
        // the missing capability so operators can grep diagnostics for it.
        IGitHost fixedHost = new FixedRepoPathHost(
            barePath: OperatingSystem.IsWindows()
                ? @"C:\opt\codeybox\repos\b044f8bd.git"
                : "/opt/codeybox/repos/b044f8bd.git");
        IGitHost rootlessHost = new RootlessGitHost();

        var fixedEx = Assert.Throws<NotSupportedException>(
            () => fixedHost.GetIsolatedRepoSandboxAccess("/anywhere/staging.git"));
        Assert.Contains("isolated bare repo", fixedEx.Message);

        var rootlessEx = Assert.Throws<NotSupportedException>(
            () => rootlessHost.GetIsolatedRepoSandboxAccess("/anywhere/staging.git"));
        Assert.Contains("isolated bare repo", rootlessEx.Message);
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
        => CreatePipeline(gitHost, new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));

    private PipelineRunner CreatePipeline(IGitHost gitHost, ISandboxProvider sandboxProvider)
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
            sandboxProvider,
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
    /// Sandbox provider that always throws SandboxMountSourceMissingException
    /// naming whichever staging mount it sees. Used to drive the
    /// exhausted-retry path in CreateMergeSandboxWithStagingRestoreAsync.
    /// </summary>
    private sealed class AlwaysMissingSourceSandboxProvider : ISandboxProvider
    {
        public string Name => "always-missing-fake";
        public int CreateAsyncCalls { get; private set; }

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateAsyncCalls++;
            foreach (var mount in spec.Mounts)
            {
                if (mount.HostPath is null) continue;
                var name = Path.GetFileName(mount.HostPath);
                if (name.StartsWith("codeybox-merge-", StringComparison.Ordinal)
                    && mount.HostPath.EndsWith(".git", StringComparison.Ordinal))
                {
                    throw new SandboxMountSourceMissingException(
                        mount.HostPath,
                        $"simulated persistent mount source missing: {mount.HostPath}");
                }
            }
            throw new InvalidOperationException("test fake never received a staging mount");
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Sandbox provider that always throws SandboxMountSourceMissingException
    /// naming a fixed, unrelated host path — NOT the staging clone the
    /// orchestrator passed in. Used to drive the HostPath-mismatch branch
    /// in CreateMergeSandboxWithStagingRestoreAsync, asserting the
    /// orchestrator does NOT call RestoreIsolatedMergeRepositoryAsync when
    /// the missing path is something other than the staging clone.
    /// </summary>
    private sealed class MissingPathSandboxProvider : ISandboxProvider
    {
        private readonly string _missingPath;
        public string Name => "mismatched-path-fake";
        public int CreateAsyncCalls { get; private set; }

        public MissingPathSandboxProvider(string missingPath) => _missingPath = missingPath;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CreateAsyncCalls++;
            throw new SandboxMountSourceMissingException(
                _missingPath,
                $"simulated mount source missing for unrelated path: {_missingPath}");
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
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
        public List<string> CreateIsolatedMergeCloneCalls { get; } = [];

        public StagingRootRecordingHost(LocalGitHost inner) => _inner = inner;

        public string GetMergeStagingRoot(string repositoryId)
        {
            StagingRootCalls.Add(repositoryId);
            return ((IGitHost)_inner).GetMergeStagingRoot(repositoryId);
        }

        public Task<string> CreateIsolatedMergeCloneAsync(string repositoryId, WorkItemId workItemId, CancellationToken ct = default)
        {
            CreateIsolatedMergeCloneCalls.Add(repositoryId);
            return ((IGitHost)_inner).CreateIsolatedMergeCloneAsync(repositoryId, workItemId, ct);
        }

        public Task RestoreIsolatedMergeCloneAsync(string repositoryId, string targetPath, CancellationToken ct = default)
            => ((IGitHost)_inner).RestoreIsolatedMergeCloneAsync(repositoryId, targetPath, ct);

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
    /// GetMergeStagingRoot's "cannot derive" guard via the pipeline. The
    /// CreateIsolatedMergeCloneAsync override calls GetMergeStagingRoot
    /// directly so the failure path surfaces the same InvalidOperationException
    /// a real host would when its bare-repo root is misconfigured — instead
    /// of the default NotSupportedException, which would mask the actual
    /// failure mode.
    /// </summary>
    private sealed class RootlessGitHost : IGitHost
    {
        public string GetRepoPath(string repositoryId) => "";
        public Task<string> CreateIsolatedMergeCloneAsync(string repositoryId, WorkItemId workItemId, CancellationToken ct = default)
        {
            _ = ((IGitHost)this).GetMergeStagingRoot(repositoryId);
            return Task.FromResult(string.Empty);
        }
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

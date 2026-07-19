using CodeyBox.Agents;
using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Pipeline-level integration tests for the isolated merge-staging clone
/// lifecycle. Complements the unit tests in
/// <see cref="MergePhaseIsolatedRepoStagingTests"/> by exercising the full
/// Work → Audit → Merge phase chain with a real
/// <see cref="CodeyBox.Sandbox.Process.ProcessSandboxProvider"/>, a real
/// <see cref="CodeyBox.Git.LocalGitHost"/>, and a real
/// <see cref="SandboxLeakReaper"/> running concurrently with the merge
/// phase.
///
/// <para>These tests address the b044f8bd post-mortem's acceptance criteria
/// #3 ("a work item with a large work branch goes through Work → Audit →
/// Merge without the temp dir being reaped mid-flight") and the audit
/// finding that the finally-block cleanup added to
/// <see cref="PipelineRunner.RunAgentMergePhaseAsync"/> lacks a regression
/// test on the failure path — a future change that moved the cleanup back
/// to success-only would not be caught by the existing unit tests, which
/// only exercise the happy path of CreateIsolatedMergeRepositoryAsync.</para>
/// </summary>
[Collection("Pipeline integration")]
public sealed class MergeStagingLifecycleIntegrationTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("codeybox-merge-staging-int-").FullName;

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
    }

    /// <summary>
    /// Acceptance criterion #3 from the b044f8bd report. A work item with a
    /// non-trivial work branch (multi-file seed, multiple work commits, an
    /// auditor that advances main to force the conflict path) goes through
    /// Work → Audit → Merge end-to-end while a
    /// <see cref="SandboxLeakReaper"/> sweep loop runs concurrently. The
    /// reaper's contract is provider-list-only — it must NOT enumerate the
    /// host filesystem under GitRoot — so the isolated merge staging clone
    /// (a sibling of the durable bare repo) must survive every overlap
    /// window with sweep activity until the merge phase finishes.
    ///
    /// <para>Why this lives at integration level: the unit tests in
    /// <see cref="MergePhaseIsolatedRepoStagingTests.ConcurrentReaperSweepsRunningDuringMergeStaging_LeaveAllStagingDirsIntact"/>
    /// pin the create/list invariant in isolation. This test pins the same
    /// invariant under the full PipelineRunner state machine — work-phase
    /// clone, audit iteration, merge-phase isolated clone create, merge
    /// sandbox launch and mount — so a future regression in the wider
    /// pipeline that started reaping in-flight staging would surface as a
    /// failed pipeline, not just a unit assertion.</para>
    /// </summary>
    [Fact]
    public async Task ConflictMergePhase_StagingSurvivesConcurrentReaperSweeps_PipelineCompletesDone()
    {
        var seed = await CreateLargerSeedAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", "main side\n");
        // Wrap the sandbox provider so each CreateAsync observes whether the
        // codeybox-merge-*.git bind source exists on disk AT THE MOMENT the
        // merge-phase sandbox is being built. Without this observation, the
        // test could pass even if the staging directory were deleted and
        // recreated mid-flight (or never existed at all and the pipeline
        // recovered some other way). The b044f8bd acceptance criterion is
        // specifically about mid-flight survival of the staging path, so
        // assert it here at the only moment that matters: when the merge
        // sandbox is about to mount it.
        var stagingObserver = new StagingMountObservingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var syncObserver = new RemoteGitPushSyncObservingSandboxProvider(stagingObserver);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, auditors: [auditor], sandboxProvider: syncObserver);
        auditor.GitRoot = tp.GitRoot;

        // Work agent writes the same file as the auditor's main-advance, so
        // hostMerge.HasConflicts is true and the merge phase creates an
        // isolated bare clone under GitRoot.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side\n"));
        tp.Agent.ConflictResolutionPlan.Enqueue(files =>
        {
            // Constrained text-only resolver path. Merge the two intents.
            var file = Assert.Single(files);
            Assert.Equal("shared.txt", file.Path);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shared.txt"] = "main side\nwork side\n",
            };
        });

        // Background reaper sweep loop. Empty provider list keeps the
        // reaper from disposing anything real; we are validating that its
        // sweeps never observe (or remove) merge staging on the host fs.
        var reaperProvider = new ListAllOnlySandboxProvider();
        var reaper = new SandboxLeakReaper(
            reaperProvider,
            new NullWebhookDispatcher(),
            new SandboxLeakOptions
            {
                Enabled = true,
                CheckInterval = TimeSpan.FromMinutes(1),
                LeakAgeThreshold = TimeSpan.FromMinutes(30),
                PreemptRetention = TimeSpan.FromHours(24),
                AutoDispose = true,
                MaxConcurrentAutoDispose = 4,
            },
            NullLogger<SandboxLeakReaper>.Instance);

        using var reaperLoopCts = new CancellationTokenSource();
        var reaperLoop = Task.Run(async () =>
        {
            while (!reaperLoopCts.IsCancellationRequested)
            {
                await reaper.RunSweepAsync(reaperLoopCts.Token);
                await Task.Yield();
            }
        }, reaperLoopCts.Token);

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        try
        {
            await tp.Pipeline.RunAsync(item, CancellationToken.None);
        }
        finally
        {
            reaperLoopCts.Cancel();
            try { await reaperLoop; } catch (OperationCanceledException) { /* expected */ }
        }

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.LocalSquashSha);

        // Reaper ran at least once during the merge phase. Without this
        // assertion, a regression where the reaper never ticked would make
        // the test pass vacuously.
        Assert.True(reaperProvider.ListCalls > 0,
            "reaper sweep loop never ran — concurrency window was not exercised");

        // Acceptance criterion #3, the part the iteration-7 audit flagged
        // as missing: at least one codeybox-merge-*.git bind source must
        // have been observed on disk at the moment its sandbox was being
        // built. The observer asserts inline that the path existed when
        // CreateAsync ran; here we additionally pin that the observation
        // happened (so a regression that bypassed the staging mount
        // altogether could not pass this test vacuously).
        Assert.True(stagingObserver.StagingMountObservations > 0,
            "merge-phase staging mount was never observed mid-flight — " +
            "either AC#3 setup never produced a staging clone, or the merge " +
            "sandbox bypassed the wrapper");
        Assert.True(syncObserver.MergeVerificationSyncs > 0,
            "merge verification pushed into the isolated remote repo without a following host sync");

        // After completion, no codeybox-merge-*.git directories remain
        // under GitRoot — the finally-block cleanup in
        // RunAgentMergePhaseAsync ran on success.
        AssertNoStagingDirsRemain(tp.GitRoot);
    }

    /// <summary>
    /// End-to-end exercise of the orchestrator-driven mount heal path.
    /// The integration test wraps <see cref="ProcessSandboxProvider"/> in
    /// a provider that mimics multipass behavior: on its first CreateAsync
    /// call for a merge-staging mount, it deletes the staging directory
    /// and throws <see cref="SandboxMountSourceMissingException"/> so the
    /// orchestrator's
    /// <c>CreateMergeSandboxWithStagingRestoreAsync</c> retry kicks in.
    /// The orchestrator re-clones the bare repo and retries CreateAsync,
    /// which the second-call branch lets succeed cleanly.
    ///
    /// <para>This pins the b044f8bd recovery contract end-to-end: the
    /// failure mode (missing bind source at mount time) is reproduced,
    /// the orchestrator-side retry runs, and the pipeline survives. A
    /// regression that broke the
    /// CreateMergeSandboxWithStagingRestoreAsync wiring would land the
    /// pipeline in Failed instead of Done.</para>
    /// </summary>
    [Fact]
    public async Task ConflictMergePhase_StagingReapedBeforeMergeSandboxCreate_OrchestratorRetriesAndPipelineCompletesDone()
    {
        var seed = await CreateLargerSeedAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", "main side\n");

        // The hostile provider observes each spec: on the first
        // CreateAsync call for a merge-staging mount, it deletes the
        // staging clone and throws SandboxMountSourceMissingException
        // (matching what multipass does on exists=no). The orchestrator's
        // CreateMergeSandboxWithStagingRestoreAsync catches it, re-clones
        // via RestoreIsolatedMergeRepositoryAsync, and retries CreateAsync;
        // the second call observes the staging clone back on disk and
        // succeeds.
        var healProvider = new ReapingThenRetryingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, auditors: [auditor], sandboxProvider: healProvider);
        auditor.GitRoot = tp.GitRoot;

        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side\n"));
        tp.Agent.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shared.txt"] = "main side\nwork side\n",
            };
        });

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.LocalSquashSha);

        // The reap-and-throw branch must have fired at least once — that
        // is the whole point of this test (the orchestrator-driven heal
        // path ran).
        Assert.True(healProvider.MissingSourceThrows > 0,
            "SandboxMountSourceMissingException was never thrown — orchestrator heal path did not run");

        // The finally-block cleanup must still have removed the staging
        // clone after the merge phase completed (recovery is orthogonal
        // to cleanup).
        AssertNoStagingDirsRemain(tp.GitRoot);
    }

    /// <summary>
    /// AC#3 with an external, host-side deleter present: simulates the
    /// scenario the b044f8bd post-mortem suspected (tmpwatch / cron /
    /// future host-side reaper sweeping codeybox-merge-*.git under
    /// GitRoot). The deleter respects the documented in-flight marker
    /// convention — directories containing
    /// <see cref="IGitHost.IsolatedMergeCloneInFlightMarkerFileName"/>
    /// are skipped — so an in-flight merge survives even with the
    /// deleter running tight loops alongside the merge phase. The
    /// staging window is widened by a larger multi-file seed so the
    /// deleter has many sweep iterations overlapping with the create →
    /// mount window. Pins the marker contract end-to-end: if a future
    /// regression dropped the marker write, the deleter would race
    /// through and reap the in-flight directory mid-mount — the same
    /// failure class b044f8bd tracked, with the same operator-visible
    /// symptom ("Source path does not exist").
    ///
    /// <para>Test scope: the deleter is a stand-in for any cron-driven
    /// or daemon-driven cleanup that walks the bare-repo root. The
    /// marker convention is documented on
    /// <see cref="IGitHost.IsolatedMergeCloneInFlightMarkerFileName"/>
    /// so operator-authored cleanup scripts honor the same rule this
    /// test pins.</para>
    /// </summary>
    [Fact]
    public async Task ConflictMergePhase_HostSideDeleterRespectsInFlightMarker_PipelineCompletesDone()
    {
        var seed = await CreateLargerSeedAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", "main side\n");

        var stagingObserver = new StagingMountObservingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, auditors: [auditor], sandboxProvider: stagingObserver);
        auditor.GitRoot = tp.GitRoot;

        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side\n"));
        tp.Agent.ConflictResolutionPlan.Enqueue(files =>
        {
            var file = Assert.Single(files);
            Assert.Equal("shared.txt", file.Path);
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["shared.txt"] = "main side\nwork side\n",
            };
        });

        // External deleter: tmpwatch-style host-side cleanup that walks the
        // bare-repo root every loop iteration and deletes
        // codeybox-merge-*.git directories that do NOT contain the
        // documented in-flight marker. The deleter is the stand-in for a
        // real-world host-side reaper / cron script; the marker contract
        // is the only thing keeping it off in-flight staging.
        using var deleterCts = new CancellationTokenSource();
        var deleterStats = new MarkerRespectingDeleterStats();
        var deleterLoop = Task.Run(async () =>
        {
            while (!deleterCts.IsCancellationRequested)
            {
                if (Directory.Exists(tp.GitRoot))
                {
                    foreach (var candidate in Directory.EnumerateDirectories(
                                 tp.GitRoot, "codeybox-merge-*", SearchOption.TopDirectoryOnly))
                    {
                        // The contract is OR: an in-flight directory is
                        // ANY directory whose in-directory marker exists,
                        // OR whose sibling sentinel exists. The sibling
                        // sentinel is the load-bearing one during the
                        // create window (before clone-finish the
                        // in-directory marker cannot be written yet).
                        var inDirMarker = Path.Combine(
                            candidate, IGitHost.IsolatedMergeCloneInFlightMarkerFileName);
                        var siblingMarker = candidate
                            + IGitHost.IsolatedMergeCloneInFlightSiblingSuffix;
                        if (File.Exists(inDirMarker) || File.Exists(siblingMarker))
                        {
                            deleterStats.IncrementSkipped();
                            continue;
                        }

                        try
                        {
                            Directory.Delete(candidate, recursive: true);
                            deleterStats.IncrementDeleted();
                        }
                        catch (DirectoryNotFoundException)
                        {
                            // Race with the orchestrator's finally-block
                            // cleanup — either side winning is fine.
                        }
                        catch (IOException)
                        {
                            // The orchestrator is mid-cleanup of the same
                            // directory; the next sweep will see it gone.
                        }
                    }
                }
                await Task.Yield();
            }
        }, deleterCts.Token);

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        try
        {
            await tp.Pipeline.RunAsync(item, CancellationToken.None);
        }
        finally
        {
            deleterCts.Cancel();
            try { await deleterLoop; } catch (OperationCanceledException) { /* expected */ }
        }

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.LocalSquashSha);

        // The deleter must have observed the in-flight marker at least
        // once — otherwise the test passed vacuously (e.g. if the
        // deleter loop never ticked while staging was on disk). The
        // existing observer also asserts the bind source was a valid
        // bare repo at sandbox create time, so a regression that
        // dropped the marker (letting the deleter reap mid-flight)
        // would surface as either an observer failure or a non-Done
        // pipeline state.
        Assert.True(deleterStats.SkippedCount > 0,
            "deleter never observed the in-flight marker on a staging directory — " +
            "either the marker was dropped or the deleter loop did not overlap with staging");
        Assert.True(stagingObserver.StagingMountObservations > 0,
            "merge-phase staging mount was never observed mid-flight");

        // After completion, no codeybox-merge-*.git directories remain
        // under GitRoot — finally-block cleanup removed the marker and
        // the directory together; the deleter (if still running) sees
        // no in-flight marker and could have removed any residue, but
        // the orchestrator's own cleanup is the load-bearing path.
        AssertNoStagingDirsRemain(tp.GitRoot);
    }

    /// <summary>
    /// The finally-block in <see cref="PipelineRunner.RunAgentMergePhaseAsync"/>
    /// must clean up the isolated bare clone on every exit path, including
    /// when the body throws before sandbox creation finishes. This test
    /// drives a real merge phase with a conflict, then forces the body to
    /// throw via an <see cref="IGitHost"/> decorator whose
    /// <c>GetIsolatedRepoSandboxAccess</c> raises an exception (the call
    /// site is inside the try-block, after <c>CreateIsolatedMergeRepositoryAsync</c>
    /// has already produced a staging directory on disk).
    ///
    /// <para>Without the finally guard, the codeybox-merge-*.git
    /// directories would accumulate under <c>GitRootDirectory</c> as
    /// siblings of the durable bare repo every time merge-phase setup
    /// raised mid-flight — both an operator-hygiene issue and a slow disk
    /// leak. A regression that moved the cleanup back inside the try block
    /// would fail this test.</para>
    /// </summary>
    [Fact]
    public async Task ConflictMergePhase_FailureBeforeMount_FinallyDeletesIsolatedStagingClone()
    {
        var seed = await CreateLargerSeedAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", "main side\n");

        IsolatedAccessThrowingGitHost? wrapper = null;
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            gitHostDecorator: inner =>
            {
                wrapper = new IsolatedAccessThrowingGitHost(inner);
                return wrapper;
            });
        auditor.GitRoot = tp.GitRoot;

        // Force the conflict path so isolatedMergeRepoPath is created.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side\n"));

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // The pipeline observed the injected failure and parked the item.
        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.Done, final!.State);

        Assert.NotNull(wrapper);
        Assert.True(wrapper!.IsolatedAccessCalls > 0,
            "test never reached GetIsolatedRepoSandboxAccess — finally-block cleanup not exercised");

        // The acceptance invariant: the finally block executed and removed
        // the codeybox-merge-*.git directory the merge phase created.
        // Without the finally guard, the directory would remain under
        // GitRoot and the assertion below would fail.
        AssertNoStagingDirsRemain(tp.GitRoot);
    }

    /// <summary>
    /// End-to-end exercise of the orchestrator-driven mount heal path at the
    /// CONFLICT-REWORK call site (<c>PipelineRunner.RunConflictReworkAgentAsync</c>,
    /// PipelineRunner.cs:5649). The companion test
    /// <see cref="ConflictMergePhase_StagingReapedBeforeMergeSandboxCreate_OrchestratorRetriesAndPipelineCompletesDone"/>
    /// pins the merge-phase call site (PipelineRunner.cs:3675); this test pins
    /// the rework iteration's call site because a regression that bypassed
    /// <c>CreateMergeSandboxWithStagingRestoreAsync</c> at line 5649 only —
    /// e.g. a copy-paste mistake routing rework through a plain
    /// <c>ISandboxProvider.CreateAsync</c> — would not be caught by the merge-
    /// phase test or by the helper-direct unit tests in
    /// <see cref="MergePhaseIsolatedRepoStagingTests"/>, which invoke the
    /// helper directly rather than through the rework call site.
    ///
    /// <para>Flow:
    /// <list type="number">
    ///   <item>Merge phase produces its isolated staging clone; the hostile
    ///   provider passes the merge-phase mount through (it is the first
    ///   distinct staging path seen).</item>
    ///   <item>The merge-phase text-only resolver has no plan queued, so it
    ///   surfaces a <see cref="MergeConflictResolutionFailedException"/> and
    ///   the rework iteration engages.</item>
    ///   <item>The rework iteration creates a SECOND staging clone (a
    ///   distinct codeybox-merge-*.git path). On the first CreateAsync that
    ///   names this path, the hostile provider reaps the directory and
    ///   throws <see cref="SandboxMountSourceMissingException"/> — matching
    ///   the b044f8bd multipass exists=no failure mode.</item>
    ///   <item><c>CreateMergeSandboxWithStagingRestoreAsync</c> at line 5649
    ///   catches, calls <c>RestoreIsolatedMergeRepositoryAsync</c>, and
    ///   retries CreateAsync.</item>
    ///   <item>The retried CreateAsync passes through; the rework agent
    ///   resolves the conflict and the pipeline reaches Done.</item>
    /// </list></para>
    /// </summary>
    [Fact]
    public async Task ConflictReworkPhase_StagingReapedBeforeReworkSandboxCreate_OrchestratorRetriesAndPipelineCompletesDone()
    {
        var seed = await CreateLargerSeedAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", "main side\n");

        // Targets the rework-iteration staging mount only. The merge-phase
        // mount happens first and is passed through; the next distinct
        // staging path the provider sees (= the rework iteration's bare
        // clone) is reaped exactly once, after which the orchestrator's
        // heal path runs and the retry succeeds.
        var healProvider = new ReworkIterationStagingReapingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        var syncObserver = new RemoteGitPushSyncObservingSandboxProvider(healProvider);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, auditors: [auditor], sandboxProvider: syncObserver);
        auditor.GitRoot = tp.GitRoot;

        // Drive the conflict path. NO ConflictResolutionPlan is queued, so
        // the merge-phase text-only resolver fails and the orchestrator
        // enters the rework iteration — the path under test.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side\n"));
        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            // Resolve the conflict by keeping both intents, then continue.
            await SandboxWriteFileAsync(sandbox, workDir, "shared.txt", "main side\nwork side\n", ct);
            await SandboxRun(sandbox, ct, "git", "-C", workDir, "add", "shared.txt");
            await SandboxRun(sandbox, ct, "git", "-C", workDir,
                "-c", "core.editor=true",
                "-c", "sequence.editor=true",
                "rebase", "--continue");
            return new AgentResult(true, "resolved", null, null);
        });

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.NotNull(final.LocalSquashSha);
        // The rework iteration must have actually engaged. Without this
        // assertion the test could pass vacuously if the merge phase
        // somehow recovered and skipped rework altogether.
        Assert.Equal(1, final.ConflictReworkAttempts);

        // The reap-and-throw branch must have fired AT THE REWORK CALL SITE
        // (i.e. on the rework iteration's staging path). A regression that
        // routed line 5649 through plain CreateAsync would still record a
        // missing-source throw here, but the pipeline would NOT reach Done
        // because the orchestrator would never re-clone the staging dir.
        // The Done assertion above combined with this throw counter pin
        // both halves: the failure was injected AND the heal path ran.
        Assert.True(healProvider.ReworkStagingMissingSourceThrows > 0,
            "rework-iteration SandboxMountSourceMissingException was never thrown — " +
            "either the rework call site bypassed the heal helper, the merge phase " +
            "never reached rework, or the test setup failed to produce a second staging path");
        Assert.True(syncObserver.ConflictReworkSyncs > 0,
            "conflict rework pushed into the isolated remote repo without a following host sync");

        // Finally-block cleanup must still have removed both staging clones
        // after the pipeline completed (recovery is orthogonal to cleanup).
        AssertNoStagingDirsRemain(tp.GitRoot);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static async Task SandboxRun(ISandbox sandbox, CancellationToken ct, params string[] argv)
    {
        var r = await sandbox.ExecAsync(new SandboxExec { Argv = argv }, ct);
        if (!r.Success)
            throw new InvalidOperationException(
                $"sandbox command failed (exit {r.ExitCode}): {string.Join(' ', argv)}\n{r.Stderr}\n{r.Stdout}");
    }

    private static async Task SandboxWriteFileAsync(ISandbox sandbox, string workDir, string relPath, string content, CancellationToken ct)
    {
        var r = await sandbox.ExecAsync(new SandboxExec
        {
            Argv = ["sh", "-c", "cat > \"$0\"", $"{workDir}/{relPath}"],
            Stdin = content,
        }, ct);
        if (!r.Success)
            throw new InvalidOperationException(
                $"sandbox write failed (exit {r.ExitCode}) for {relPath}: {r.Stderr}");
    }

    private static void AssertNoStagingDirsRemain(string gitRoot)
    {
        var leftover = Directory.EnumerateDirectories(gitRoot, "codeybox-merge-*", SearchOption.TopDirectoryOnly)
            .ToArray();
        Assert.True(leftover.Length == 0,
            "merge-phase finally block did not clean up isolated staging clones: " +
            string.Join(", ", leftover));
    }

    private async Task<string> CreateLargerSeedAsync()
    {
        // A multi-file seed makes the bare-clone step take long enough that
        // a tight reaper sweep loop has many opportunities to overlap with
        // the merge-phase staging window. Each file adds a measurable
        // amount of git-object IO without making the test slow in absolute
        // terms (test still completes in a few seconds).
        var seed = Path.Combine(_workspace, "seed-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(seed);
        await TestSupport.RunGit(seed, "init", "-b", "main");
        await TestSupport.RunGit(seed, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(seed, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(seed, "shared.txt"), "base\n");
        await TestSupport.RunGit(seed, "add", "shared.txt");
        await TestSupport.RunGit(seed, "commit", "-m", "initial shared");
        for (var i = 0; i < 12; i++)
        {
            var name = $"file-{i:D2}.txt";
            await File.WriteAllTextAsync(
                Path.Combine(seed, name),
                string.Concat(Enumerable.Repeat($"line {i}\n", 64)));
            await TestSupport.RunGit(seed, "add", name);
            await TestSupport.RunGit(seed, "commit", "-m", $"file {i}");
        }
        return seed;
    }

    private static WorkItem NewItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "merge-staging-lifecycle",
        Prompt = "write the work file",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
    };

    /// <summary>
    /// Forces a conflict during the merge phase by advancing main from the
    /// audit phase. Mirrors the <c>MainAdvancingAuditor</c> used by
    /// <c>MergeConflictReworkTests</c> — kept private here so this file
    /// has no cross-test linkage that could surprise a reader.
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _workspace;
        private readonly string _path;
        private readonly string _content;

        public string? GitRoot { get; set; }
        public string Name => "advance-main";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public MainAdvancingAuditor(string workspace, string path, string content)
        {
            _workspace = workspace;
            _path = path;
            _content = content;
        }

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = ct;
            if (GitRoot is null)
                throw new InvalidOperationException("GitRoot must be assigned before the auditor runs.");
            var barePath = Path.Combine(GitRoot, context.WorkItemId + ".git");
            var clone = Path.Combine(_workspace, "advance-main-" + Guid.NewGuid().ToString("N")[..8]);
            await TestSupport.RunGit(_workspace, "clone", barePath, clone);
            await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
            await TestSupport.RunGit(clone, "config", "user.name", "Test");
            await TestSupport.RunGit(clone, "checkout", context.BaseBranch);
            await File.WriteAllTextAsync(Path.Combine(clone, _path), _content);
            await TestSupport.RunGit(clone, "commit", "-am", "advance main during audit");
            await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{context.BaseBranch}");
            return new AuditResult(true, []);
        }
    }

    /// <summary>
    /// Thread-safe counters for the marker-respecting host-side deleter.
    /// Lets the AC#3 test assert the deleter actually observed the
    /// in-flight marker at least once during the merge phase.
    /// </summary>
    private sealed class MarkerRespectingDeleterStats
    {
        private int _skipped;
        private int _deleted;
        public int SkippedCount => Volatile.Read(ref _skipped);
        public int DeletedCount => Volatile.Read(ref _deleted);
        public void IncrementSkipped() => Interlocked.Increment(ref _skipped);
        public void IncrementDeleted() => Interlocked.Increment(ref _deleted);
    }

    /// <summary>
    /// ISandboxProvider wrapper that records every SandboxSpec passed to
    /// CreateAsync (so the test can inspect mount wiring) and delegates
    /// otherwise. Sandboxes built by this provider behave identically to
    /// the inner provider's; only the spec is observed in passing.
    /// </summary>
    private sealed class SpecRecordingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        public List<SandboxSpec> CapturedSpecs { get; } = new();

        public SpecRecordingSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            CapturedSpecs.Add(spec);
            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    /// <summary>
    /// Observes isolated-remote git pushes and requires a completed sandbox
    /// sync after them. This pins the distributed-VM path where a push updates
    /// a remote writable mount that host-side import code reads immediately.
    /// </summary>
    private sealed class RemoteGitPushSyncObservingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private int _mergeVerificationSyncs;
        private int _conflictReworkSyncs;

        public RemoteGitPushSyncObservingSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;
        public int MergeVerificationSyncs => Volatile.Read(ref _mergeVerificationSyncs);
        public int ConflictReworkSyncs => Volatile.Read(ref _conflictReworkSyncs);

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => new RemoteGitPushSyncObservingSandbox(await _inner.CreateAsync(spec, ct), this);

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);

        private void RecordMergeVerificationSync() => Interlocked.Increment(ref _mergeVerificationSyncs);
        private void RecordConflictReworkSync() => Interlocked.Increment(ref _conflictReworkSyncs);

        private sealed class RemoteGitPushSyncObservingSandbox : ISandbox
        {
            private readonly ISandbox _inner;
            private readonly RemoteGitPushSyncObservingSandboxProvider _owner;
            private int _pendingMergeVerificationPush;
            private int _pendingConflictReworkPush;

            public RemoteGitPushSyncObservingSandbox(
                ISandbox inner,
                RemoteGitPushSyncObservingSandboxProvider owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public string Id => _inner.Id;
            public SandboxAgentOutputTransportKind AgentOutputTransportKind => _inner.AgentOutputTransportKind;
            public SandboxBatchLaunchMode BatchLaunchMode => _inner.BatchLaunchMode;

            public async Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            {
                var result = await _inner.ExecAsync(exec, ct);
                if (!result.Success) return result;

                if (IsGitPushToRef(exec.Argv, "refs/codeybox/merge-verification/"))
                    Interlocked.Exchange(ref _pendingMergeVerificationPush, 1);
                if (IsGitPushToRef(exec.Argv, "refs/codeybox/conflict-rework/"))
                    Interlocked.Exchange(ref _pendingConflictReworkPush, 1);

                return result;
            }

            public async Task SyncStateToHostAsync(CancellationToken ct = default)
            {
                var mergeVerificationPending = Interlocked.Exchange(ref _pendingMergeVerificationPush, 0) == 1;
                var conflictReworkPending = Interlocked.Exchange(ref _pendingConflictReworkPush, 0) == 1;

                await _inner.SyncStateToHostAsync(ct);

                if (mergeVerificationPending) _owner.RecordMergeVerificationSync();
                if (conflictReworkPending) _owner.RecordConflictReworkSync();
            }

            public Task KillActiveExecsAsync(CancellationToken ct = default)
                => _inner.KillActiveExecsAsync(ct);

            public Task<byte[]> GetScreenshotAsync(CancellationToken ct = default)
                => _inner.GetScreenshotAsync(ct);

            public Task SynthesizeInputAsync(IReadOnlyList<SandboxInputEvent> events, CancellationToken ct = default)
                => _inner.SynthesizeInputAsync(events, ct);

            public Task<SandboxAccessibilitySnapshot?> GetAccessibilityAtPointAsync(
                int x,
                int y,
                CancellationToken ct = default)
                => _inner.GetAccessibilityAtPointAsync(x, y, ct);

            public Task<string?> GetAccessibilityTreeJsonAsync(CancellationToken ct = default)
                => _inner.GetAccessibilityTreeJsonAsync(ct);

            public ValueTask DisposeAsync()
                => _inner.DisposeAsync();

            private static bool IsGitPushToRef(IReadOnlyList<string> argv, string refPrefix)
                => argv.Count >= 5
                    && argv.Contains("push", StringComparer.Ordinal)
                    && argv.Any(arg => arg.StartsWith($"HEAD:{refPrefix}", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// ISandboxProvider wrapper that, for each CreateAsync call, asserts
    /// the bind-mount host path of any codeybox-merge-*.git staging clone
    /// exists on disk AT THE MOMENT the sandbox is being built — the
    /// exact mid-flight observation b044f8bd acceptance criterion #3
    /// calls for. Without this hook, the AC#3 test could only assert
    /// terminal state (Done) and post-run cleanup, neither of which
    /// distinguishes "staging survived" from "staging was reaped and
    /// the pipeline silently recovered some other way".
    /// </summary>
    private sealed class StagingMountObservingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        public int StagingMountObservations { get; private set; }

        public StagingMountObservingSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            foreach (var mount in spec.Mounts)
            {
                if (mount.HostPath is null) continue;
                var isStagingClone = Path.GetFileName(mount.HostPath)
                    .StartsWith("codeybox-merge-", StringComparison.Ordinal)
                    && mount.HostPath.EndsWith(".git", StringComparison.Ordinal);
                if (!isStagingClone) continue;

                // The b044f8bd observable: the bind source must exist on
                // disk at the moment its sandbox is being built. A
                // regression that reaped staging mid-flight (or never
                // created it in the first place) would fail this loud
                // assertion at the only moment that matters for the
                // mount step.
                Assert.True(Directory.Exists(mount.HostPath),
                    $"staging mount bind source missing at sandbox create time: {mount.HostPath}");
                Assert.True(File.Exists(Path.Combine(mount.HostPath, "HEAD")),
                    $"staging mount bind source is not a valid bare repo at sandbox create time: {mount.HostPath}");
                StagingMountObservations++;
            }
            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    /// <summary>
    /// ISandboxProvider wrapper that mimics the production multipass mount
    /// loop's failure-on-exists-no semantics. On its FIRST CreateAsync
    /// call that includes a merge-staging mount (codeybox-merge-*.git),
    /// it reaps the staging directory and throws
    /// <see cref="SandboxMountSourceMissingException"/> naming that path —
    /// matching what <c>MultipassSandboxProvider.MountSingleBindWithRetryAsync</c>
    /// does when it sees <c>exists=no</c>. Subsequent calls pass through
    /// to the inner provider, letting the orchestrator's
    /// <c>CreateMergeSandboxWithStagingRestoreAsync</c> retry succeed
    /// once it has re-cloned the bare repo.
    ///
    /// <para>The reap-and-throw sequence runs only for the merge staging
    /// path. Work / audit / non-conflict mounts are passed through
    /// untouched so the rest of the pipeline behaves identically to a
    /// plain ProcessSandboxProvider run.</para>
    /// </summary>
    private sealed class ReapingThenRetryingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private readonly HashSet<string> _alreadyReaped = new(StringComparer.Ordinal);
        public int MissingSourceThrows { get; private set; }

        public ReapingThenRetryingSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            foreach (var mount in spec.Mounts)
            {
                if (mount.HostPath is null) continue;
                var isStagingClone = Path.GetFileName(mount.HostPath)
                    .StartsWith("codeybox-merge-", StringComparison.Ordinal)
                    && mount.HostPath.EndsWith(".git", StringComparison.Ordinal);
                if (!isStagingClone) continue;

                // Reap exactly once per staging path. The orchestrator
                // retries CreateAsync after re-cloning; the second call
                // must observe the staging clone back on disk and
                // proceed without another reap.
                if (!_alreadyReaped.Add(mount.HostPath)) continue;

                if (Directory.Exists(mount.HostPath))
                    Directory.Delete(mount.HostPath, recursive: true);
                MissingSourceThrows++;
                throw new SandboxMountSourceMissingException(
                    mount.HostPath,
                    $"simulated multipass mount source missing: {mount.HostPath}");
            }

            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    /// <summary>
    /// ISandboxProvider wrapper that targets the CONFLICT-REWORK call site
    /// (<see cref="PipelineRunner.RunConflictReworkAgentAsync"/>,
    /// PipelineRunner.cs:5649) — companion to
    /// <see cref="ReapingThenRetryingSandboxProvider"/>, which targets the
    /// merge-phase call site. The merge phase always runs first and produces
    /// the first staging path the provider sees; that path is passed through
    /// untouched. The rework iteration produces a SECOND, distinct staging
    /// path (a new codeybox-merge-*.git bare clone). The first CreateAsync
    /// naming that second path is reaped: the directory is deleted and
    /// <see cref="SandboxMountSourceMissingException"/> is thrown to mimic
    /// what <c>MultipassSandboxProvider.MountSingleBindWithRetryAsync</c>
    /// does on <c>exists=no</c>. The orchestrator's
    /// <c>CreateMergeSandboxWithStagingRestoreAsync</c> at line 5649 catches,
    /// restores, and retries; the retried CreateAsync passes through to the
    /// inner provider so the rework iteration can complete.
    ///
    /// <para>The two-staging-path design is load-bearing: passing the
    /// merge-phase mount through ensures the failure injection is unique to
    /// the rework call site, so the test's signal (pipeline reaches Done +
    /// throws == 1) only fires when the rework call site really wired
    /// through the heal helper.</para>
    /// </summary>
    private sealed class ReworkIterationStagingReapingSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private readonly HashSet<string> _stagingPathsSeen = new(StringComparer.Ordinal);
        private readonly HashSet<string> _alreadyReaped = new(StringComparer.Ordinal);
        private string? _firstStagingPath;
        public int ReworkStagingMissingSourceThrows { get; private set; }

        public ReworkIterationStagingReapingSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            foreach (var mount in spec.Mounts)
            {
                if (mount.HostPath is null) continue;
                var isStagingClone = Path.GetFileName(mount.HostPath)
                    .StartsWith("codeybox-merge-", StringComparison.Ordinal)
                    && mount.HostPath.EndsWith(".git", StringComparison.Ordinal);
                if (!isStagingClone) continue;

                _stagingPathsSeen.Add(mount.HostPath);
                _firstStagingPath ??= mount.HostPath;

                // First staging path (merge phase): pass through unchanged
                // — this test only targets the rework call site.
                if (string.Equals(mount.HostPath, _firstStagingPath, StringComparison.Ordinal))
                    continue;

                // Second+ staging path (rework iteration): reap exactly once.
                // The orchestrator's heal retry then sees the path back on
                // disk and the next CreateAsync for the same path passes
                // through normally.
                if (!_alreadyReaped.Add(mount.HostPath)) continue;

                if (Directory.Exists(mount.HostPath))
                    Directory.Delete(mount.HostPath, recursive: true);
                ReworkStagingMissingSourceThrows++;
                throw new SandboxMountSourceMissingException(
                    mount.HostPath,
                    $"simulated multipass mount source missing at rework call site: {mount.HostPath}");
            }

            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    /// <summary>
    /// ISandboxProvider that only honors ListAllManagedAsync (with a
    /// counter so the test can assert sweeps ran). Throws if the reaper
    /// ever calls DisposeLeakedAsync or CreateAsync. Used to confirm the
    /// reaper's host-side contract: enumerate the provider's VM registry,
    /// never walk the host filesystem.
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
                "reaper attempted to dispose a host artifact during the integration run");
    }

    /// <summary>
    /// IGitHost decorator that throws on
    /// <see cref="IGitHost.GetIsolatedRepoSandboxAccess"/> so the merge
    /// phase fails inside its try-block after
    /// <c>CreateIsolatedMergeRepositoryAsync</c> has produced the staging
    /// directory on disk. Drives the failure-path cleanup test.
    /// </summary>
    private sealed class IsolatedAccessThrowingGitHost : IGitHost
    {
        private readonly IGitHost _inner;
        public int IsolatedAccessCalls { get; private set; }
        public IsolatedAccessThrowingGitHost(IGitHost inner) => _inner = inner;

        public SandboxRepositoryAccess GetIsolatedRepoSandboxAccess(string isolatedRepoHostPath)
        {
            IsolatedAccessCalls++;
            throw new InvalidOperationException(
                $"simulated GetIsolatedRepoSandboxAccess failure for path '{isolatedRepoHostPath}'");
        }

        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default)
            => _inner.EnsureRepositoryAsync(id, seedFromUrl, ct);
        public Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, string? baseBranch, CancellationToken ct = default)
            => _inner.EnsureRepositoryAsync(id, seedFromUrl, baseBranch, ct);
        public SandboxRepositoryAccess GetSandboxAccess(string repositoryId) => _inner.GetSandboxAccess(repositoryId);
        public string GetRepoPath(string repositoryId) => _inner.GetRepoPath(repositoryId);
        public string GetMergeStagingRoot(string repositoryId) => _inner.GetMergeStagingRoot(repositoryId);
        public Task<string> CreateIsolatedMergeCloneAsync(string repositoryId, WorkItemId workItemId, CancellationToken ct = default)
            => _inner.CreateIsolatedMergeCloneAsync(repositoryId, workItemId, ct);
        public Task RestoreIsolatedMergeCloneAsync(string repositoryId, string targetPath, CancellationToken ct = default)
            => _inner.RestoreIsolatedMergeCloneAsync(repositoryId, targetPath, ct);
        public Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default)
            => _inner.GetDefaultBranchAsync(repositoryId, ct);
        public Task PushToUpstreamAsync(
            string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
            CancellationToken ct = default)
            => _inner.PushToUpstreamAsync(repositoryId, upstreamUrl, branch, upstreamEnv, reconcileStrategy, ct);
        public Task<string?> FetchUpstreamBranchAsync(
            string repositoryId, string upstreamUrl, string branch,
            IReadOnlyDictionary<string, string> upstreamEnv,
            CancellationToken ct = default)
            => _inner.FetchUpstreamBranchAsync(repositoryId, upstreamUrl, branch, upstreamEnv, ct);
        public Task SetBranchToCommitAsync(string repositoryId, string branch, string sha, CancellationToken ct = default)
            => _inner.SetBranchToCommitAsync(repositoryId, branch, sha, ct);
        public Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default)
            => _inner.DisposeRepositoryAsync(repositoryId, ct);
        public Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default)
            => _inner.RepositoryExistsAsync(id, ct);
        public Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
            => _inner.BranchExistsAsync(repositoryId, branch, ct);
        public Task<bool> BranchHasCommitsAheadAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
            => _inner.BranchHasCommitsAheadAsync(repositoryId, baseBranch, workBranch, ct);
        public Task<(string DiffStat, string FullDiff)> GetDiffAsync(string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
            => _inner.GetDiffAsync(repositoryId, baseBranch, workBranch, ct);
        public Task<GitMergeTreeResult> ComputeMergeTreeAsync(string repositoryId, string mainCommit, string workCommit, CancellationToken ct = default)
            => _inner.ComputeMergeTreeAsync(repositoryId, mainCommit, workCommit, ct);
        public Task<string> ResolveCommitAsync(string repositoryId, string commitish, CancellationToken ct = default)
            => _inner.ResolveCommitAsync(repositoryId, commitish, ct);
        public Task ResetWorkBranchToBaseAsync(string repositoryId, string workBranch, string baseBranch, CancellationToken ct = default)
            => _inner.ResetWorkBranchToBaseAsync(repositoryId, workBranch, baseBranch, ct);
        public Task<string> ResolveTreeAsync(string repositoryId, string treeish, CancellationToken ct = default)
            => _inner.ResolveTreeAsync(repositoryId, treeish, ct);
        public Task<string> ReadTextFileAsync(string repositoryId, string treeish, string path, CancellationToken ct = default)
            => _inner.ReadTextFileAsync(repositoryId, treeish, path, ct);
        public Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string? pathPrefix, CancellationToken ct = default)
            => _inner.ListFilesAsync(repositoryId, treeish, pathPrefix, ct);
        public Task<IReadOnlyList<GitChangedPath>> GetChangedPathsAsync(string repositoryId, string fromTreeish, string toTreeish, CancellationToken ct = default)
            => _inner.GetChangedPathsAsync(repositoryId, fromTreeish, toTreeish, ct);
        public Task<string> GetUnifiedDiffAsync(string repositoryId, string fromTreeish, string toTreeish, string path, CancellationToken ct = default)
            => _inner.GetUnifiedDiffAsync(repositoryId, fromTreeish, toTreeish, path, ct);
    }
}

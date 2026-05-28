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
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
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
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, auditors: [auditor], sandboxProvider: stagingObserver);
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
        Assert.NotNull(final.MergeSha);

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

        // After completion, no codeybox-merge-*.git directories remain
        // under GitRoot — the finally-block cleanup in
        // RunAgentMergePhaseAsync ran on success.
        AssertNoStagingDirsRemain(tp.GitRoot);
    }

    /// <summary>
    /// Wiring guard for the production fix. PipelineRunner attaches an
    /// <see cref="ISandboxMountSourceRestorer"/> to the merge-phase sandbox
    /// spec only when the conflict path runs through
    /// <see cref="PipelineRunner.AttachIsolatedRepoRestoreHook"/>. This
    /// test drives the full conflict-merge pipeline through a sandbox
    /// provider that captures every <see cref="SandboxSpec"/> at
    /// <see cref="ISandboxProvider.CreateAsync"/> time, then asserts the
    /// merge spec carries a mount with a non-null restorer pointing at
    /// the staging directory the merge phase just created. A regression
    /// that dropped the AttachIsolatedRepoRestoreHook call (or wired it to
    /// the wrong mount) would fail this test loudly.
    ///
    /// <para>Companion to the unit assertions in
    /// <c>MergePhaseIsolatedRepoStagingTests.AttachIsolatedRepoRestoreHook_OnlyAddsRestoreToMatchingMount</c>:
    /// that test pins the helper's behavior on a hand-constructed access.
    /// This one pins that the production merge phase actually invokes the
    /// helper before handing the spec to the sandbox provider.</para>
    /// </summary>
    [Fact]
    public async Task ConflictMergePhase_AttachesSourceRestorerOnStagingMountOfMergeSandboxSpec()
    {
        var seed = await CreateLargerSeedAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", "main side\n");
        var recordingProvider = new SpecRecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, auditors: [auditor], sandboxProvider: recordingProvider);
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

        // The merge phase is the only one that mounts an isolated bare
        // clone (work/audit phases mount the durable bare repo without a
        // restorer). Find the captured spec whose mounts include a
        // codeybox-merge-*.git path.
        var mergeSpec = recordingProvider.CapturedSpecs
            .FirstOrDefault(s => s.Mounts.Any(m =>
                m.HostPath is { } hp &&
                Path.GetFileName(hp).StartsWith("codeybox-merge-", StringComparison.Ordinal) &&
                hp.EndsWith(".git", StringComparison.Ordinal)));
        Assert.NotNull(mergeSpec);

        var stagingMount = mergeSpec!.Mounts.Single(m =>
            m.HostPath is { } hp &&
            Path.GetFileName(hp).StartsWith("codeybox-merge-", StringComparison.Ordinal));
        Assert.NotNull(stagingMount.SourceRestorer);

        // The restorer must point at the durable bare repo, not the
        // staging path it is asked to heal. Sanity-check by invoking it
        // after a simulated delete and re-checking the path; a regression
        // that wired the restorer to a stale closure (wrong repoId,
        // wrong target) would fail this assertion rather than silently
        // healing the wrong path.
        var stagingPath = stagingMount.HostPath!;
        if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
        await stagingMount.SourceRestorer!.RestoreAsync(CancellationToken.None);
        try
        {
            Assert.True(File.Exists(Path.Combine(stagingPath, "HEAD")),
                $"restorer wired by merge phase did not recreate {stagingPath}");
        }
        finally
        {
            try { Directory.Delete(stagingPath, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Companion wiring guard for the conflict-rework call site
    /// (<see cref="PipelineRunner.AttachIsolatedRepoRestoreHook"/> invoked
    /// at <c>PipelineRunner.cs:5634</c>). The rework path creates its own
    /// isolated bare clone and a fresh sandbox; the merge-phase spec test
    /// above does not exercise that call site, so a regression that
    /// dropped the helper invocation in the rework branch alone would
    /// otherwise go undetected.
    ///
    /// <para>Drives the rework path by leaving the merge-phase text-only
    /// resolver's plan empty (so <see cref="MergeConflictResolutionFailedException"/>
    /// fires) and queues a clean rework resolution. The captured rework
    /// sandbox spec must have a non-null restorer on its staging mount.</para>
    /// </summary>
    [Fact]
    public async Task ConflictReworkPhase_AttachesSourceRestorerOnStagingMountOfReworkSandboxSpec()
    {
        var seed = await CreateLargerSeedAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", "main side\n");
        var recordingProvider = new SpecRecordingSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance));
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed, auditors: [auditor], sandboxProvider: recordingProvider);
        auditor.GitRoot = tp.GitRoot;

        tp.Agent.WorkPlan.Enqueue(new FileWrite("shared.txt", "work side\n"));
        // No ConflictResolutionPlan entry -> merge-phase resolver fails ->
        // pipeline transitions to the rework path. The rework plan below
        // resolves cleanly so the pipeline reaches Done after rework.
        tp.Agent.ConflictReworkPlan.Enqueue(async (sandbox, workDir, ct) =>
        {
            // sandbox.ExecAsync rewrites argv-form sandbox-absolute paths
            // to their host-fs equivalents (see ProcessSandboxProvider);
            // shell redirection inside a "sh -c" string would not be
            // rewritten, so use the stdin-into-`cat > "$0"` idiom (mirror
            // of MergeConflictReworkTests' WriteFileAsync helper).
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", $"{workDir}/shared.txt"],
                Stdin = "main side\nwork side\n",
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workDir, "add", "shared.txt"],
            }, ct);
            await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workDir, "-c", "core.editor=true",
                    "-c", "sequence.editor=true", "rebase", "--continue"],
            }, ct);
            return new AgentResult(true, "resolved", null, null);
        });

        var item = NewItem("codeybox/" + WorkItemId.New().ToString()[..8]);
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.True(final!.ConflictReworkAttempts > 0,
            "rework path did not run — captured spec set does not include the rework sandbox");

        // Capture every spec whose mounts include a codeybox-merge-*.git
        // bind source. Both the merge-phase sandbox (3668) and the
        // rework sandbox (5634) produce such a spec; both must carry a
        // restorer. The rework wiring is what this test specifically
        // pins — a regression that dropped AttachIsolatedRepoRestoreHook
        // at PipelineRunner.cs:5634 alone would fail this assertion even
        // if the merge-phase wiring at 3668 remained correct.
        var stagingSpecs = recordingProvider.CapturedSpecs
            .Where(s => s.Mounts.Any(m =>
                m.HostPath is { } hp &&
                Path.GetFileName(hp).StartsWith("codeybox-merge-", StringComparison.Ordinal) &&
                hp.EndsWith(".git", StringComparison.Ordinal)))
            .ToList();
        Assert.True(stagingSpecs.Count >= 2,
            $"expected at least 2 staging specs (merge + rework); captured {stagingSpecs.Count}");

        foreach (var spec in stagingSpecs)
        {
            var stagingMount = spec.Mounts.Single(m =>
                m.HostPath is { } hp &&
                Path.GetFileName(hp).StartsWith("codeybox-merge-", StringComparison.Ordinal));
            Assert.NotNull(stagingMount.SourceRestorer);
        }
    }

    /// <summary>
    /// End-to-end exercise of the multipass-equivalent mount heal path.
    /// The integration test wraps <see cref="ProcessSandboxProvider"/> in
    /// a provider that mimics multipass behavior: before each CreateAsync,
    /// any bind mount whose host path is missing AND carries an
    /// <see cref="ISandboxMountSourceRestorer"/> has its restorer invoked
    /// (this is the same recovery the production
    /// <c>MountSingleBindWithRetryAsync</c> loop performs when it sees
    /// <c>exists=no</c>).
    ///
    /// <para>The test then injects mid-flight deletion: between when the
    /// merge phase creates the staging clone and when the merge sandbox is
    /// built, an external task deletes the staging directory. The
    /// orchestrator-side restorer the merge phase attached must heal the
    /// directory before the inner provider's CreateAsync runs, and the
    /// pipeline must complete Done despite the simulated reap.</para>
    ///
    /// <para>This pins the b044f8bd recovery contract end-to-end: the
    /// failure mode (missing bind source at mount time) is reproduced,
    /// the wired restorer runs, and the pipeline survives. A regression
    /// that broke the restorer wiring or the mount-heal handoff would
    /// land the pipeline in Failed instead of Done.</para>
    /// </summary>
    [Fact]
    public async Task ConflictMergePhase_StagingReapedBeforeMergeSandboxCreate_RestorerHealsItAndPipelineCompletesDone()
    {
        var seed = await CreateLargerSeedAsync();
        var auditor = new MainAdvancingAuditor(_workspace, "shared.txt", "main side\n");

        // The hostile provider observes each spec, deletes staging
        // dirs (codeybox-merge-*.git) before invoking the restorer, then
        // delegates to ProcessSandboxProvider. The deletion-then-restore
        // sequence reproduces the multipass mount loop's behavior: a
        // failed mount with exists=no triggers the restorer; the
        // subsequent mount sees the recreated source and succeeds.
        var healProvider = new ReapingThenRestoringSandboxProvider(
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
        Assert.NotNull(final.MergeSha);

        // The restorer must have been invoked at least once — that is
        // the whole point of this test (the recovery path ran).
        Assert.True(healProvider.RestoreInvocations > 0,
            "ISandboxMountSourceRestorer was never invoked — the heal path did not run");

        // The finally-block cleanup must still have removed the staging
        // clone after the merge phase completed (recovery is orthogonal
        // to cleanup).
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

    // ── helpers ─────────────────────────────────────────────────────────────

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
    /// loop's recovery path. Before delegating CreateAsync to the inner
    /// provider, it deletes any bind-mount host path that looks like a
    /// merge-staging clone (codeybox-merge-*.git) to simulate the
    /// b044f8bd failure mode (reaper / external cleanup removed the bind
    /// source between create and mount), then invokes the mount's
    /// <see cref="ISandboxMountSourceRestorer"/> if one is wired —
    /// matching what <c>MultipassSandboxProvider.MountSingleBindWithRetryAsync</c>
    /// does when it sees <c>exists=no</c> after a failed mount attempt.
    ///
    /// <para>The reap-then-restore sequence runs only for the merge
    /// staging path. Work / audit / non-conflict mounts are passed through
    /// untouched so the rest of the pipeline behaves identically to a
    /// plain ProcessSandboxProvider run.</para>
    /// </summary>
    private sealed class ReapingThenRestoringSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        public int RestoreInvocations { get; private set; }

        public ReapingThenRestoringSandboxProvider(ISandboxProvider inner) => _inner = inner;

        public string Name => _inner.Name;

        public async Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            foreach (var mount in spec.Mounts)
            {
                if (mount.HostPath is null) continue;
                var isStagingClone = Path.GetFileName(mount.HostPath)
                    .StartsWith("codeybox-merge-", StringComparison.Ordinal)
                    && mount.HostPath.EndsWith(".git", StringComparison.Ordinal);
                if (!isStagingClone) continue;

                // Reap the staging dir so the inner CreateAsync would
                // otherwise observe a missing bind source — the exact
                // class of failure b044f8bd reported under multipass.
                if (Directory.Exists(mount.HostPath))
                    Directory.Delete(mount.HostPath, recursive: true);

                // Self-heal via the wired restorer, the way the production
                // multipass mount loop would. A missing restorer here would
                // mean the orchestrator never wired the heal path and the
                // pipeline would die — that is the failure this test exists
                // to catch as a regression.
                Assert.NotNull(mount.SourceRestorer);
                await mount.SourceRestorer!.RestoreAsync(ct);
                RestoreInvocations++;
                Assert.True(Directory.Exists(mount.HostPath),
                    $"restorer did not recreate {mount.HostPath}");
            }

            return await _inner.CreateAsync(spec, ct);
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
        public Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string pathPrefix, CancellationToken ct = default)
            => _inner.ListFilesAsync(repositoryId, treeish, pathPrefix, ct);
        public Task<IReadOnlyList<GitChangedPath>> GetChangedPathsAsync(string repositoryId, string fromTreeish, string toTreeish, CancellationToken ct = default)
            => _inner.GetChangedPathsAsync(repositoryId, fromTreeish, toTreeish, ct);
        public Task<string> GetUnifiedDiffAsync(string repositoryId, string fromTreeish, string toTreeish, string path, CancellationToken ct = default)
            => _inner.GetUnifiedDiffAsync(repositoryId, fromTreeish, toTreeish, path, ct);
    }
}

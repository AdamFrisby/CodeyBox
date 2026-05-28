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
        Assert.NotNull(final.MergeSha);

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

using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for <c>MaybeIncrementalRebaseAsync</c>: the between-iteration
/// incremental rebase that keeps a long-lived work branch close to base so
/// the merge-time rebase has smaller and rarer conflicts.
///
/// <para>
/// Spec-required cases (see work item rework prompt):
/// <list type="bullet">
///   <item>Disabled flag is a no-op (default behaviour preserved).</item>
///   <item>Skipped for branches outside the pickup-rebase-owned prefix.</item>
///   <item>Clean rebase advances the work branch onto the freshly-advanced base.</item>
///   <item>Rebase failure is swallowed (work item completes against the un-rebased branch).</item>
///   <item>Cancellation propagates (no swallow on shutdown / operator cancel).</item>
/// </list>
/// </para>
/// </summary>
[Collection("Pipeline integration")]
public sealed class IncrementalRebaseBetweenAuditIterationsTests : IDisposable
{
    private readonly string _workspace;

    public IncrementalRebaseBetweenAuditIterationsTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-incremental-rebase-").FullName;

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
    }

    [Fact]
    public async Task DisabledFlag_DoesNotRebaseBetweenIterations()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var advancingAuditor = new MainAdvancingAuditor(seed, blockingFirstThenPass: true);
        var reworkObservation = new ReworkObservationProbe();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [advancingAuditor],
            incrementalRebase: new IncrementalRebaseSnapshot(new IncrementalRebaseOptions { Enabled = false }));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2"));

        // Capture HEAD's ancestry at the rework dispatch (the SECOND work
        // call). The initial work call fires BEFORE main is advanced and
        // would trivially report origin/main in ancestry; it must be
        // skipped. The rework call fires AFTER iter 1 + (a no-op
        // MaybeIncrementalRebaseAsync, because the flag is off), so the
        // rebase-vs-no-rebase divergence is observable.
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            if (!reworkObservation.InitialWorkSeen)
            {
                reworkObservation.InitialWorkSeen = true;
                return;
            }
            if (reworkObservation.SnapshotTaken) return;
            reworkObservation.SnapshotTaken = true;
            await reworkObservation.CaptureAsync(sandbox, workingDirectory, ct);
        };

        var item = NewItem();
        advancingAuditor.BarePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // The advancing auditor must have fired during iter 1.
        Assert.True(advancingAuditor.AdvancedMain);
        // Probe ran on rework dispatch (proves the dispatch happened post
        // the disabled-flag no-op).
        Assert.True(reworkObservation.SnapshotTaken);
        // CRITICAL: with the flag off, the incremental rebase must NOT have
        // run between iter 1 and rework — so the rework agent observes an
        // un-rebased branch whose ancestry does NOT yet contain the
        // advanced origin/main. A regression that ran the rebase despite
        // Enabled=false would advance the branch and flip this assertion.
        Assert.False(reworkObservation.AdvancedMainInAncestry,
            "with Enabled=false, the rework agent must see the un-rebased work branch");
    }

    [Fact]
    public async Task EnabledButNonOwnedBranch_DoesNotRebaseBetweenIterations()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var advancingAuditor = new MainAdvancingAuditor(seed, blockingFirstThenPass: true);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [advancingAuditor],
            incrementalRebase: new IncrementalRebaseSnapshot(new IncrementalRebaseOptions { Enabled = true }));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2"));

        // Non-owned branch: outside the codeybox/<id-prefix> namespace.
        // ValidatePickupRebaseWorkBranch in the rebase core throws if a
        // non-owned branch is force-pushed, so a successful run proves the
        // gate fired BEFORE the rebase core was entered.
        var item = NewItem("feature/not-pickup-owned");
        advancingAuditor.BarePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(advancingAuditor.AdvancedMain);
    }

    [Fact]
    public async Task EnabledOwnedBranch_RebasesWorkBranchOntoAdvancedBaseBeforeRework()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var preAdvanceMainCapture = new MainShaCapture();
        var advancingAuditor = new MainAdvancingAuditor(seed, blockingFirstThenPass: true, preAdvanceCapture: preAdvanceMainCapture);
        // ReworkObservationProbe inspects the work-branch tree the rework
        // agent sees on dispatch. If the incremental rebase ran, the
        // rework sandbox's HEAD ancestry will contain the advanced main
        // commit ("main advanced" — committed by the auditor before iter 2's
        // rework). If it did NOT run, the ancestry will not yet contain that
        // commit (the un-rebased branch sits on the pre-advance main).
        var reworkObservation = new ReworkObservationProbe();

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [advancingAuditor],
            incrementalRebase: new IncrementalRebaseSnapshot(new IncrementalRebaseOptions { Enabled = true }));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        // Snapshot what the rework agent sees the moment it dispatches —
        // before writing the rework file. The initial work call (first
        // BeforeWorkAsync) fires BEFORE the auditor advances main, so
        // origin/main and HEAD trivially share ancestry; we must skip it
        // and capture on the rework dispatch (second call) where the
        // rebase-vs-no-rebase divergence is observable.
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            if (!reworkObservation.InitialWorkSeen)
            {
                reworkObservation.InitialWorkSeen = true;
                return;
            }
            if (reworkObservation.SnapshotTaken) return;
            reworkObservation.SnapshotTaken = true;
            await reworkObservation.CaptureAsync(sandbox, workingDirectory, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
        advancingAuditor.BarePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(advancingAuditor.AdvancedMain);
        Assert.NotNull(preAdvanceMainCapture.Sha);
        Assert.NotEqual(string.Empty, advancingAuditor.AdvancedMainSha);

        // Critical assertion: the rework agent observed an already-rebased
        // work branch — i.e. the advanced main commit is in HEAD's ancestry
        // when rework started. Without the incremental rebase, this would
        // only be true after the pickup-time rebase (which runs LATER, at
        // merge), so the rework agent would see the un-rebased branch.
        Assert.True(reworkObservation.SnapshotTaken);
        Assert.True(reworkObservation.AdvancedMainInAncestry,
            "rework agent should see the work branch rebased onto advanced main");
    }

    [Fact]
    public async Task ReBaseFailure_IsSwallowedWorkItemProceeds()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Configure the auditor to advance main with a conflicting change.
        // ConflictResolutionPlan starts EMPTY, so when MaybeIncrementalRebaseAsync
        // (running between iter 1 and the rework dispatch) hits the conflict
        // on a.txt, the resolver cascade exhausts (ScriptedAgent returns
        // failure when its plan queue is empty), the rebase core throws
        // MergeConflictResolutionFailedException, and the catch in
        // MaybeIncrementalRebaseAsync MUST swallow it so the work item
        // proceeds. A single plan entry is enqueued LATER (via BeforeWorkAsync
        // on the rework dispatch, AFTER the incremental rebase has already
        // failed) so the merge-time pickup-rebase has a viable resolver.
        var advancingAuditor = new MainAdvancingAuditor(seed, blockingFirstThenPass: true, conflictingFile: "a.txt");
        var reworkObservation = new ReworkObservationProbe();

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [advancingAuditor],
            incrementalRebase: new IncrementalRebaseSnapshot(new IncrementalRebaseOptions { Enabled = true }));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "work-v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "work-v2-after-rework\n"));

        // Initial work call: do nothing (incremental rebase hasn't run yet —
        // we need the plan EMPTY when it runs).
        // Rework call: by now the incremental rebase has failed-and-been-
        // swallowed; enqueue the resolver entries the merge-time pickup-rebase
        // will need. Two entries because the work branch has two commits
        // (initial work + rework), each touching a.txt — git rebase replays
        // them in order and each commit's conflict triggers a fresh agentic
        // resolver invocation. Also capture the rework dispatch's HEAD
        // ancestry to confirm the un-rebased branch reached the rework agent.
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            if (!reworkObservation.InitialWorkSeen)
            {
                reworkObservation.InitialWorkSeen = true;
                return;
            }
            if (reworkObservation.SnapshotTaken) return;
            reworkObservation.SnapshotTaken = true;
            await reworkObservation.CaptureAsync(sandbox, workingDirectory, ct);
            tp.Agent.ConflictResolutionPlan.Enqueue(_ =>
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["a.txt"] = "merged\n",
                });
            tp.Agent.ConflictResolutionPlan.Enqueue(_ =>
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["a.txt"] = "merged\n",
                });
        };

        var item = NewItem();
        advancingAuditor.BarePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // The work item must complete — the audit/rework loop is not
        // sensitive to incremental rebase failures. If the catch in
        // MaybeIncrementalRebaseAsync were removed, the
        // MergeConflictResolutionFailedException raised by the empty
        // cascade would propagate up and tear the work item to Failed.
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.True(reworkObservation.SnapshotTaken);
        // The incremental rebase FAILED (didn't advance the branch), so the
        // rework agent saw the un-rebased branch. If the rebase had
        // succeeded (e.g. the plan had been seeded too early), this would
        // flip to true and the test would catch the mis-setup.
        Assert.False(reworkObservation.AdvancedMainInAncestry,
            "incremental rebase should have failed-and-been-swallowed, leaving the rework branch un-rebased");
    }

    [Fact]
    public async Task CancellationDuringRebase_Propagates()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var cts = new CancellationTokenSource();
        var cancellingAuditor = new MainAdvancingAuditor(seed, blockingFirstThenPass: true, cancelAfterFirstIteration: cts);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [cancellingAuditor],
            incrementalRebase: new IncrementalRebaseSnapshot(new IncrementalRebaseOptions { Enabled = true }));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        // The second WorkPlan entry MUST NOT be consumed — cancellation
        // should land before the rework agent dispatches.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("never-consumed.txt", "should-not-run"));

        var item = NewItem();
        cancellingAuditor.BarePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        await tp.Store.CreateAsync(item);

        // Cancellation propagates as OperationCanceledException at the
        // pipeline-runner surface — the audit/rework loop must not swallow
        // it via the best-effort catch in MaybeIncrementalRebaseAsync.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tp.Pipeline.RunAsync(item, cts.Token));

        // Cancellation arrived between audit iter 1 and the rework dispatch;
        // the rework agent never ran, so its plan entry is still queued.
        Assert.Single(tp.Agent.WorkPlan);
    }

    [Fact]
    public async Task ProvisioningDeferredDuringRebase_Propagates()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var advancingAuditor = new MainAdvancingAuditor(seed, blockingFirstThenPass: true);
        var deferAt = new SandboxProvisioningDeferredException(
            provider: "multipass",
            operation: "clone",
            errorClass: "multipass-clone-target-already-exists",
            detail: "clone target collision exhausted",
            recheckIn: TimeSpan.FromMinutes(1));
        var sandboxes = new ThrowingTimingPhaseSandboxProvider(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            "incremental-rebase",
            deferAt);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [advancingAuditor],
            incrementalRebase: new IncrementalRebaseSnapshot(new IncrementalRebaseOptions { Enabled = true }),
            sandboxProvider: sandboxes);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("should-not-run.txt", "rework should not start"));

        var item = NewItem();
        advancingAuditor.BarePath = tp.GitHost.GetRepoPath(item.Id.ToString());
        await tp.Store.CreateAsync(item);

        var thrown = await Assert.ThrowsAsync<SandboxProvisioningDeferredException>(
            () => tp.Pipeline.RunAsync(item, CancellationToken.None));

        Assert.Same(deferAt, thrown);
        Assert.Single(tp.Agent.WorkPlan);
        var persisted = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.NotEqual(WorkItemState.Failed, persisted!.State);
    }

    private static WorkItem NewItem(string? workBranch = null)
    {
        var id = WorkItemId.New();
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("test-project"),
            Title = "incremental-rebase-test",
            Prompt = "do thing",
            BaseBranch = "main",
            WorkBranch = workBranch ?? $"codeybox/{id.ToString()[..8]}",
            PushUpstream = false,
        };
    }

    private sealed class ThrowingTimingPhaseSandboxProvider : ISandboxProvider
    {
        private readonly ISandboxProvider _inner;
        private readonly string _timingPhase;
        private readonly SandboxProvisioningDeferredException _exception;

        public ThrowingTimingPhaseSandboxProvider(
            ISandboxProvider inner,
            string timingPhase,
            SandboxProvisioningDeferredException exception)
        {
            _inner = inner;
            _timingPhase = timingPhase;
            _exception = exception;
        }

        public string Name => _inner.Name;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
        {
            if (string.Equals(spec.TimingPhase, _timingPhase, StringComparison.Ordinal))
                throw _exception;

            return _inner.CreateAsync(spec, ct);
        }

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => _inner.ListAllManagedAsync(ct);

        public Task DisposeLeakedAsync(string name, CancellationToken ct)
            => _inner.DisposeLeakedAsync(name, ct);
    }

    private sealed class MainShaCapture
    {
        public string? Sha { get; set; }
    }

    /// <summary>
    /// Auditor that advances the seed repo's main branch during iteration 1
    /// (before returning a blocking finding). Iteration 2 returns clean so
    /// the work item reaches Done. Optionally writes a conflicting change to
    /// <paramref name="conflictingFile"/> to exercise the failure path.
    ///
    /// <para>
    /// Set <see cref="BarePath"/> after the pipeline has constructed its
    /// LocalGitHost so iter-1's seed-advance is also fetched into the bare
    /// repo. Without this, sandboxes clone an unrefreshed bare in which
    /// origin/main is still pre-advance — making the incremental rebase a
    /// silent no-op (baseAlreadyAncestor returns true and the rebase early
    /// returns) and erasing every observable difference between "rebase ran"
    /// and "rebase was skipped".
    /// </para>
    /// </summary>
    private sealed class MainAdvancingAuditor : IAuditor
    {
        private readonly string _seedRepoPath;
        private readonly bool _blockingFirstThenPass;
        private readonly string? _conflictingFile;
        private readonly CancellationTokenSource? _cancelAfterFirstIteration;
        private readonly MainShaCapture? _preAdvanceCapture;
        private int _runCount;

        public MainAdvancingAuditor(
            string seedRepoPath,
            bool blockingFirstThenPass,
            string? conflictingFile = null,
            CancellationTokenSource? cancelAfterFirstIteration = null,
            MainShaCapture? preAdvanceCapture = null)
        {
            _seedRepoPath = seedRepoPath;
            _blockingFirstThenPass = blockingFirstThenPass;
            _conflictingFile = conflictingFile;
            _cancelAfterFirstIteration = cancelAfterFirstIteration;
            _preAdvanceCapture = preAdvanceCapture;
        }

        public string Name => "MainAdvancing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public bool AdvancedMain { get; private set; }
        public string AdvancedMainSha { get; private set; } = string.Empty;
        public string? BarePath { get; set; }

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _runCount++;
            if (_runCount == 1)
            {
                if (_preAdvanceCapture is not null)
                {
                    var (_, sha, _) = await TestSupport.RunGit(_seedRepoPath, "rev-parse", "main");
                    _preAdvanceCapture.Sha = sha.Trim();
                }

                await TestSupport.RunGit(_seedRepoPath, "config", "user.email", "audit@test.com");
                await TestSupport.RunGit(_seedRepoPath, "config", "user.name", "Audit");
                var advancePath = _conflictingFile ?? "advanced.txt";
                await File.WriteAllTextAsync(Path.Combine(_seedRepoPath, advancePath), "main advanced\n", ct);
                await TestSupport.RunGit(_seedRepoPath, "add", advancePath);
                await TestSupport.RunGit(_seedRepoPath, "commit", "-m", "main advanced");
                var (_, advancedSha, _) = await TestSupport.RunGit(_seedRepoPath, "rev-parse", "main");
                AdvancedMainSha = advancedSha.Trim();
                AdvancedMain = true;

                if (BarePath is not null)
                {
                    // Propagate the seed's main advance into the bare repo
                    // — mirrors what FetchUpstreamAsync does during a fresh
                    // EnsureRepositoryAsync call. The pipeline only does this
                    // ONCE at start, so without an explicit fetch here the
                    // bare repo holds a pre-advance origin/main for the rest
                    // of the run.
                    await TestSupport.RunGit(
                        BarePath, "fetch", "--no-tags", "--prune",
                        _seedRepoPath, "+refs/heads/main:refs/heads/main");
                }

                _cancelAfterFirstIteration?.Cancel();

                return _blockingFirstThenPass
                    ? new AuditResult(false, [new AuditFinding("MainAdvancing", AuditSeverity.Error, "needs fix", "x")])
                    : new AuditResult(true, []);
            }

            return new AuditResult(true, []);
        }
    }

    /// <summary>
    /// Captures the work-branch ancestry as seen by the rework agent.
    /// Callers gate <see cref="CaptureAsync"/> on
    /// <see cref="InitialWorkSeen"/> so the FIRST work-agent dispatch
    /// (which runs before main advances and would trivially report origin/main
    /// in ancestry) is skipped; the SECOND dispatch — the rework after iter
    /// 1 advanced main — is what carries signal.
    /// </summary>
    private sealed class ReworkObservationProbe
    {
        public bool InitialWorkSeen { get; set; }
        public bool SnapshotTaken { get; set; }
        public bool AdvancedMainInAncestry { get; set; }

        public async Task CaptureAsync(ISandbox sandbox, string workingDirectory, CancellationToken ct)
        {
            // origin/main is the advanced base by the time rework dispatches
            // (the auditor moved main forward during iter 1). The work agent
            // does git fetch + git checkout, so this is observable from the
            // rework sandbox. We check whether origin/main is an ancestor of
            // HEAD — true iff the work branch was rebased ONTO origin/main.
            var rc = await sandbox.ExecAsync(new SandboxExec
            {
                Argv = ["git", "-C", workingDirectory, "merge-base", "--is-ancestor", "origin/main", "HEAD"],
            }, ct);
            AdvancedMainInAncestry = rc.Success;
        }
    }
}

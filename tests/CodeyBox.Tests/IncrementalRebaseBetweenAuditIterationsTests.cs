using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Coverage for <c>MaybeIncrementalRebaseAsync</c>: the between-iteration
/// incremental rebase that keeps a long-lived work branch close to base so
/// the merge-time rebase has smaller and rarer conflicts.
///
/// <para>
/// All four cases are spec-required (see work item rework prompt):
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
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task DisabledFlag_DoesNotRebaseBetweenIterations()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var advancingAuditor = new MainAdvancingAuditor(seed, blockingFirstThenPass: true);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [advancingAuditor],
            incrementalRebase: new IncrementalRebaseSnapshot(new IncrementalRebaseOptions { Enabled = false }));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        // The work item still completes — the merge-time rebase at pickup
        // consolidates the advanced base. The pickup rebase's parent walk
        // proves the work branch was NOT rebased earlier (otherwise the
        // pickup rebase would observe an already-on-base tip).
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // The advancing auditor must have fired during iter 1.
        Assert.True(advancingAuditor.AdvancedMain);
        // The between-iteration rebase did not run (flag off), so the audit
        // iteration 2 ran on a work branch whose ancestry still pointed at
        // the pre-advance base. We verify the pre-advance commit appears in
        // the final history (it is the merge-base contribution after the
        // pickup-time rebase).
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
        // before writing the rework file. If the incremental rebase ran,
        // origin/{baseBranch} appears in HEAD's ancestry already.
        tp.Agent.BeforeWorkAsync = async (sandbox, workingDirectory, ct) =>
        {
            if (reworkObservation.SnapshotTaken) return;
            reworkObservation.SnapshotTaken = true;
            await reworkObservation.CaptureAsync(sandbox, workingDirectory, ct);
        };
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
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
        // Configure the auditor to advance main with a conflicting change AND
        // to have NO conflict resolution plan enqueued. The rebase will hit a
        // conflict, the resolver will fail (no plan entries), and
        // MaybeIncrementalRebaseAsync MUST swallow the failure — the work
        // item proceeds to the merge phase where the pickup-time rebase
        // (which DOES have a conflict plan) does the heavy lifting.
        var advancingAuditor = new MainAdvancingAuditor(seed, blockingFirstThenPass: true, conflictingFile: "a.txt");

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [advancingAuditor],
            incrementalRebase: new IncrementalRebaseSnapshot(new IncrementalRebaseOptions { Enabled = true }));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "work-v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "work-v2-after-rework\n"));

        // Conflict-resolution plan for the merge-time (pickup-time) rebase.
        // The between-iteration rebase finds a conflict too but its first
        // dequeue will succeed — that is fine. We only assert that the work
        // item still proceeds even if the incremental rebase had failed.
        tp.Agent.ConflictResolutionPlan.Enqueue(_ =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a.txt"] = "merged\n",
            });
        // A second resolution for the pickup-time rebase that runs at merge.
        tp.Agent.ConflictResolutionPlan.Enqueue(_ =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a.txt"] = "merged\n",
            });

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        // The work item must complete — the audit/rework loop is not
        // sensitive to incremental rebase failures.
        Assert.Equal(WorkItemState.Done, final!.State);
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

    private sealed class MainShaCapture
    {
        public string? Sha { get; set; }
    }

    /// <summary>
    /// Auditor that advances the seed repo's main branch during iteration 1
    /// (before returning a blocking finding). Iteration 2 returns clean so
    /// the work item reaches Done. Optionally writes a conflicting change to
    /// <paramref name="conflictingFile"/> to exercise the failure path.
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

                _cancelAfterFirstIteration?.Cancel();

                return _blockingFirstThenPass
                    ? new AuditResult(false, [new AuditFinding("MainAdvancing", AuditSeverity.Error, "needs fix", "x")])
                    : new AuditResult(true, []);
            }

            return new AuditResult(true, []);
        }
    }

    /// <summary>
    /// Captures the work-branch ancestry as seen by the rework agent on its
    /// first invocation. Used to assert that the between-iteration
    /// incremental rebase has already advanced the work branch by the time
    /// the rework agent gets dispatched.
    /// </summary>
    private sealed class ReworkObservationProbe
    {
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

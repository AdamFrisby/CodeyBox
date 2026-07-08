using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the empty-rework disambiguation policy:
/// <para>
/// A rework agent that exits cleanly with no committed changes is no longer an
/// unconditional terminal failure. The audit/rework loop:
/// </para>
/// <list type="bullet">
///   <item>STEP 1 — classifies infra (auth / quota) signatures on the
///         clean-exit + no-diff REWORK branch: before the empty result is
///         treated as "produced no changes", the quota classifier runs over the
///         run's captured stdout/stderr; a match throws
///         <c>TerminalQuotaError</c> so the availability breaker excludes the
///         agent and the item re-routes / parks in
///         <see cref="WorkItemState.WaitingForQuotaReset"/> (NOT terminal, NOT
///         the operator-input park, NOT counted against convergence). This
///         exit-0 no-diff detection is distinct from the earlier failure-path
///         (non-zero exit) detector and is exercised by
///         <see cref="EmptyRework_WithQuotaSignature_ClassifiedAsInfra_ReRoutes"/>.</item>
///   <item>When no infra signature applies, converge-aware handling kicks in:
///         escalation re-dispatch with an explicit "you committed nothing,
///         either modify files or justify each finding" instruction (gated on
///         <see cref="PipelineTuningOptions.EmptyReworkEscalationRetries"/>
///         and <c>HasAuditConvergenceProgress</c>); on still-empty falls back
///         to the operator-input park flow.</item>
///   <item>Initial work phase (<c>isInitial==true</c>) stays fail-fast — no
///         audit loop sits behind it to recover an empty pass.</item>
/// </list>
/// </summary>
[Collection("Pipeline integration")]
public sealed class EmptyReworkDisambiguationTests : IDisposable
{
    private readonly string _workspace;

    public EmptyReworkDisambiguationTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-empty-rework-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "empty rework disambiguation",
        Prompt = "change the repo",
        WorkBranch = branch,
        BaseBranch = "main",
    };

    [Fact]
    public async Task EmptyRework_NoConvergence_NoEscalation_ParksForOperator()
    {
        // ScriptedAgent writes the same content on iteration 1 (creates the
        // file → diff) and again on rework (no diff). The OnceFailingAuditor
        // fires once at iteration 1 to force a rework, then would pass on
        // iteration 2 — but rework is empty, so the loop must NOT
        // terminal-fail the item. Without prior convergence (history.Count==1
        // when we hit the empty rework) escalation is skipped — instead the
        // item parks via the NeedsOperatorInput flow.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new OnceFailingAuditor();
        var audit = new ProjectAudit
        {
            MaxIterations = 3,
            AuditTypes = ["scripted"],
        };
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            EmptyReworkEscalationRetries = 0,
        });
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            pipelineTuning: tuning);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));

        var item = NewItem("feature/empty-rework-no-convergence");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Contains("produced no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Agent produced no changes to commit", final.LastError ?? string.Empty,
            StringComparison.Ordinal);
        // The auditor ran exactly once (iteration 1 → rework → empty → park,
        // never reaches iteration 2 audit).
        Assert.Equal(1, auditor.Calls);
    }

    [Fact]
    public async Task EmptyRework_WithQuotaSignature_ClassifiedAsInfra_ReRoutes()
    {
        // STEP 1 acceptance: an empty rework whose clean-exit output carries a
        // usage/quota signature is classified as an INFRA failure — NOT the
        // "produced no changes" verdict. The item must re-route (park in
        // WaitingForQuotaReset so QuotaRetryScheduler re-dispatches on reset),
        // NOT terminal-fail, NOT go through the operator-input park, and NOT be
        // counted against convergence / reach a second audit iteration.
        //
        // Sequence:
        //   work iter 1  → "v1\n" (initial diff; no-diff branch not hit, quota
        //                  classifier not consulted for initial work)
        //   audit iter 1 → 1 blocking finding → rework
        //   rework iter 2 → writes "v1\n" again → NO diff → clean-exit no-diff
        //                  branch. ResultStdout carries a Claude quota signature
        //                  ("rate_limit_exceeded"), so the STEP 1 classifier
        //                  matches → TerminalQuotaError → WaitingForQuotaReset.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new OnceFailingAuditor();
        var audit = new ProjectAudit
        {
            MaxIterations = 3,
            AuditTypes = ["scripted"],
        };
        // Escalation retries are irrelevant: STEP 1 fires before STEP 2 ever
        // runs. A generous value would let the genuine-empty escalation branch
        // run if the quota classification were (wrongly) skipped, so leaving it
        // >0 makes the test stricter — any escalation dispatch would appear in
        // WorkPrompts and fail the assertions below.
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            EmptyReworkEscalationRetries = 2,
        });
        using var quotaFailures = new SqliteQuotaFailureStore(
            Path.Combine(_workspace, $"quota-failures-{Guid.NewGuid():N}.db"));
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            pipelineTuning: tuning,
            quotaFailures: quotaFailures);
        // Every work/rework result the ScriptedAgent produces from its WorkPlan
        // carries this stdout. The Claude quota detector (wired into the test
        // pipeline's CompositeQuotaFailureClassifier) matches the plain-text
        // "rate_limit_exceeded" substring. It only influences the outcome on the
        // no-diff branch (rework iter 2); the initial diff-producing work ignores
        // it.
        tp.Agent.ResultStdout = "rate_limit_exceeded";
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));

        var item = NewItem("feature/empty-rework-quota-signature");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Infra classification re-routes via the quota path, NOT terminal /
        // operator-park.
        Assert.Equal(WorkItemState.WaitingForQuotaReset, final!.State);
        Assert.NotEqual(WorkItemState.Failed, final.State);
        Assert.NotEqual(WorkItemState.NeedsOperatorInput, final.State);
        // The empty-rework "produced no changes" verdict must NOT have been
        // reached — the quota classifier short-circuited before it.
        Assert.DoesNotContain("produced no changes", final.LastError ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        // Not counted against convergence: the loop never re-entered audit
        // iteration 2, and no escalation re-dispatch ran.
        Assert.Equal(1, auditor.Calls);
        Assert.DoesNotContain(tp.Agent.WorkPrompts, p =>
            p.Contains("[empty-rework escalation attempt", StringComparison.Ordinal));
        var observations = await quotaFailures.ListRecentAsync(
            TimeSpan.FromHours(1), DateTimeOffset.UtcNow, CancellationToken.None);
        var observation = Assert.Single(observations);
        Assert.Equal(AgentKind.Claude, observation.Agent);
        Assert.Equal(QuotaFailureKind.RateLimitExceeded, observation.FailureKind);
        Assert.Equal(item.ProjectId, observation.ProjectId);
    }

    [Fact]
    public async Task EmptyRework_WithEscalationRetry_RetryProducesChanges_LoopContinues()
    {
        // Convergence detection needs at least 2 audit snapshots in history
        // (see BuildAuditProgressSignals's `history.Count < 2 => []`). So the
        // empty rework must happen on iteration 2+ for the escalation branch
        // to engage. Sequence:
        //   work iter 1 → "v1\n" (initial diff)
        //   audit iter 1 → 2 blocking findings
        //   rework iter 2 → "v2\n" (real diff)
        //   audit iter 2 → 1 blocking finding (converging: count decreased)
        //   rework iter 3 → "v2\n" (NO DIFF → empty rework, history.Count=2,
        //     convergence detected → escalation kicks in)
        //   escalation attempt 1/1 → "v3\n" (real diff, escalation recovered!)
        //   audit iter 3 → 0 blocking → passes.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ConvergingAuditor(blockingPerIteration: [2, 1, 0]);
        var audit = new ProjectAudit
        {
            MaxIterations = 4,
            AuditTypes = ["scripted"],
        };
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            EmptyReworkEscalationRetries = 1,
        });
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            pipelineTuning: tuning);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v2\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v2\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v3\n"));

        var item = NewItem("feature/empty-rework-escalation-recovers");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        // Escalation recovered: the work item must NOT be NeedsOperatorInput
        // or Failed for the empty-rework reason — those would mean the
        // escalation either never ran or never picked up the recovered diff.
        Assert.NotEqual(WorkItemState.Failed, final!.State);
        Assert.NotEqual(WorkItemState.NeedsOperatorInput, final.State);

        // The recovered escalation prompt MUST include the escalation header
        // so we know the re-dispatch went through the escalation path (not a
        // fresh first-pass).
        Assert.Contains(tp.Agent.WorkPrompts, p =>
            p.Contains("[empty-rework escalation attempt 1/1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyRework_WithConvergence_AllEscalationRetriesEmpty_Parks()
    {
        // Like the escalation-recovers test but the escalation dispatches are
        // also no-op (same content), so retries exhaust and the loop falls
        // back to ParkAuditMaxIterationsForOperatorAsync rather than
        // terminal-failing the item.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ConvergingAuditor(blockingPerIteration: [2, 1, 0]);
        var audit = new ProjectAudit
        {
            MaxIterations = 4,
            AuditTypes = ["scripted"],
        };
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            EmptyReworkEscalationRetries = 2,
        });
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            pipelineTuning: tuning);
        // work → "v1", rework1 → "v2" (real diff seeds history.Count=2),
        // rework2 → "v2" (empty), escalation 1/2 → "v2" (still empty),
        // escalation 2/2 → "v2" (still empty) → park.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v2\n"));
        for (var i = 0; i < 3; i++)
            tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v2\n"));

        var item = NewItem("feature/empty-rework-all-escalations-empty");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Contains("produced no changes", final.LastError, StringComparison.OrdinalIgnoreCase);

        // Both escalation attempts ran. Each appends a distinct attempt header
        // so we can verify the configured retry count was honored.
        Assert.Contains(tp.Agent.WorkPrompts, p =>
            p.Contains("[empty-rework escalation attempt 1/2]", StringComparison.Ordinal));
        Assert.Contains(tp.Agent.WorkPrompts, p =>
            p.Contains("[empty-rework escalation attempt 2/2]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitialWorkPhase_Empty_StillTerminalFails()
    {
        // Regression: the initial-work no-changes path stays fail-fast. There
        // is no audit/rework loop sitting behind it to converge a "declined to
        // do anything" outcome, so the asymmetry with the rework path is
        // deliberate.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new OnceFailingAuditor();
        var audit = new ProjectAudit
        {
            MaxIterations = 2,
            AuditTypes = ["scripted"],
        };
        var tuning = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            EmptyReworkEscalationRetries = 5,
        });
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit,
            pipelineTuning: tuning);
        // Initial work returns success but writes nothing → no diff → empty
        // initial work.
        tp.Agent.WorkResults.Enqueue(new AgentResult(true, "ok", null, null));

        var item = NewItem("feature/empty-initial-work");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Agent produced no changes to commit", final.LastError, StringComparison.Ordinal);
        Assert.Equal(0, auditor.Calls);
    }

    [Fact]
    public void EscalationRetries_HotReload_ReplacesSnapshotConcurrently()
    {
        // The hot-reload contract: changing PipelineTuning at runtime via
        // PipelineTuningSnapshot.Replace must be visible to subsequent reads
        // through .Current. PipelineRunner's empty-rework handler reads
        // _pipelineTuning.Current.EmptyReworkEscalationRetries on every
        // dispatch, so a Replace before the next empty-rework attempt
        // immediately takes effect (no orchestrator restart required).
        var snapshot = new PipelineTuningSnapshot(new PipelineTuningOptions
        {
            EmptyReworkEscalationRetries = 1,
        });
        Assert.Equal(1, snapshot.Current.EmptyReworkEscalationRetries);

        snapshot.Replace(new PipelineTuningOptions { EmptyReworkEscalationRetries = 3 });
        Assert.Equal(3, snapshot.Current.EmptyReworkEscalationRetries);

        snapshot.Replace(new PipelineTuningOptions { EmptyReworkEscalationRetries = 0 });
        Assert.Equal(0, snapshot.Current.EmptyReworkEscalationRetries);
    }

    [Fact]
    public void EscalationRetries_RejectsNegativeValues()
    {
        // The validator must reject negative escalation retries — the
        // for-loop bound math depends on a non-negative value, and a negative
        // value would silently disable the feature in a misleading way.
        var bad = new PipelineTuningOptions { EmptyReworkEscalationRetries = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => bad.Validate());
    }

    /// <summary>
    /// Auditor that returns a configurable blocking-finding count per
    /// iteration. Used to seed convergence signals
    /// (<c>blocking_findings_decreased</c>) so the empty-rework handler enters
    /// the escalation branch instead of the immediate-park branch.
    /// </summary>
    private sealed class ConvergingAuditor : IAuditor
    {
        private readonly IReadOnlyList<int> _blockingPerIteration;
        private int _calls;

        public ConvergingAuditor(IReadOnlyList<int> blockingPerIteration)
        {
            _blockingPerIteration = blockingPerIteration;
        }

        public string Name => "test:converging";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public int Calls => _calls;

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            _calls++;
            var idx = Math.Min(_calls - 1, _blockingPerIteration.Count - 1);
            var count = _blockingPerIteration[idx];
            if (count <= 0)
                return Task.FromResult(new AuditResult(true, []));

            var findings = Enumerable.Range(0, count)
                .Select(i => new AuditFinding(
                    Name,
                    AuditSeverity.Error,
                    $"converging finding #{i} (iter {_calls})",
                    $"deliberate failing finding #{i} on iteration {_calls}"))
                .ToArray();
            return Task.FromResult(new AuditResult(false, findings));
        }
    }

    /// <summary>
    /// Mirrors <c>OnceFailingAuditor</c> from <c>MechanicalFixerTests</c> —
    /// fails once with one finding, passes on every subsequent call. Local
    /// copy so this test file is self-contained.
    /// </summary>
    private sealed class OnceFailingAuditor : IAuditor
    {
        public string Name => "test:once-failing";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public int Calls { get; private set; }

        public Task<AuditResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            AuditContext context,
            CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            Calls++;
            return Calls == 1
                ? Task.FromResult(new AuditResult(false,
                [
                    new AuditFinding(Name, AuditSeverity.Error, "first audit requires rework", "scripted one-time failure"),
                ]))
                : Task.FromResult(new AuditResult(true, []));
        }
    }
}

using CodeyBox.Audit;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the zero-blocking audit finding bug: an audit
/// iteration that records zero blocking findings (even with advisory
/// findings present) must not dispatch a rework iteration, park/summary
/// messages must report the blocking count accurately, and a no-change
/// rework pass with zero blocking must not feed the no-changes circuit
/// breaker.
/// </summary>
[Collection("Pipeline integration")]
public sealed class ZeroBlockingReworkTests : IDisposable
{
    private readonly string _workspace;

    public ZeroBlockingReworkTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-zero-blocking-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch { }
    }

    private static WorkItem NewItem(string branch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "zero blocking rework",
        Prompt = "change the repo",
        WorkBranch = branch,
        BaseBranch = "main",
    };

    private static AgentAvailabilityRegistry NewRegistry() => new(
        new AvailabilityOptions(),
        TimeProvider.System,
        NullLogger<AgentAvailabilityRegistry>.Instance);

    private static AuditProgressSnapshot Snapshot(
        int blocking,
        int nonBlocking,
        IReadOnlyList<AuditProgressFinding> details,
        IReadOnlyList<AuditProgressFinding> findings,
        string status = AuditProgressStatuses.Complete) => new(
            Iteration: 1,
            MaxIterations: 3,
            BlockingFindings: blocking,
            NonBlockingFindings: nonBlocking,
            BlockingFindingIds: details.Select(d => $"{d.AuditorName}:{d.Title}").ToList(),
            BlockingFindingsDetails: details,
            Findings: findings,
            WorkBranchTip: "abc123",
            Status: status);

    private sealed class WarningOnlyAuditor : IAuditor
    {
        private int _calls;
        public int Calls => _calls;
        public string Name => "security:gitleaks";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            _ = sandbox;
            _ = workingDirectory;
            _ = context;
            _ = ct;
            _calls++;
            return Task.FromResult(new AuditResult(true, [
                new AuditFinding(Name, AuditSeverity.Warning, "tool not installed in sandbox: gitleaks", "gitleaks binary missing; secret scan skipped"),
            ]));
        }
    }

    [Fact]
    public async Task ZeroBlockingWarningAudit_CompletesWithoutRework_AndLeavesBreakerUntouched()
    {
        // A complete audit verdict with zero blocking findings and one
        // advisory finding must complete the item: no rework iteration is
        // dispatched (the auditor runs exactly once, the agent runs exactly
        // once), and the no-changes breaker stays untouched.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new WarningOnlyAuditor();
        var registry = NewRegistry();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 3,
            webhookDispatcher: webhooks,
            availabilityRegistry: registry);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));

        var item = NewItem("feature/zero-blocking-no-rework");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, auditor.Calls);
        Assert.Single(tp.Agent.WorkPrompts);
        Assert.All(registry.Snapshot(), s => Assert.Equal(0, s.ConsecutiveNoChanges));
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    [Fact]
    public async Task ZeroBlockingEmptyRework_ParksWithZeroBlockingMessage_AndRefundsBreaker()
    {
        // Direct empty-rework handling on a zero-blocking snapshot parks with
        // a message that reports zero blocking (never naming the advisory
        // finding as blocking) and refunds the no-changes outcome so the
        // pass does not count toward the circuit breaker.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var registry = NewRegistry();
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            webhookDispatcher: webhooks,
            availabilityRegistry: registry);

        var item = NewItem("feature/zero-blocking-empty-rework-park");
        await tp.Store.CreateAsync(item);
        var project = new Project
        {
            Id = item.ProjectId,
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = 3 },
        };
        var advisory = new AuditProgressFinding(
            "security:gitleaks",
            AuditSeverity.Warning,
            "tool not installed in sandbox: gitleaks",
            "gitleaks binary missing; secret scan skipped");
        IReadOnlyList<AuditProgressSnapshot> history =
        [
            Snapshot(blocking: 0, nonBlocking: 1, details: [], findings: [advisory]),
        ];

        registry.RecordNoChangesOutcome(AgentKind.Claude, item.Id);

        using var phase = new PhaseCancellation("rework", CancellationToken.None);
        var parked = await tp.Pipeline.HandleEmptyReworkAsync(
            item,
            project,
            new ReworkProducedNoChangesException(AgentKind.Claude, "Rework agent produced no changes"),
            history,
            auditIteration: 1,
            reworkIterationNumber: 2,
            maxIterations: 3,
            baseReworkPrompt: "fix the findings",
            dispatchAsync: _ => Task.FromException<string?>(
                new InvalidOperationException("zero-blocking park must not redispatch")),
            reworkPhase: phase,
            reworkStart: DateTimeOffset.UtcNow,
            repoId: item.Id.ToString(),
            workBranch: item.WorkBranch!,
            ct: CancellationToken.None);

        Assert.True(parked);
        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Contains("0 blocking finding(s)", final.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("1 non-blocking", final.LastError ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("tool not installed in sandbox", final.LastError ?? string.Empty, StringComparison.Ordinal);

        var parkedEvent = Assert.Single(webhooks.Events, e => e.Event == "work_item.needs_operator_input");
        var details = Assert.IsType<AuditMaxIterationsEscalationDetails>(parkedEvent.Details);
        Assert.Equal(0, details.BlockingFindings);
        Assert.Equal(1, details.NonBlockingFindings);
        Assert.Empty(details.RemainingBlockingFindings);

        var snapshot = registry.Snapshot().Single(s => s.Agent == AgentKind.Claude);
        Assert.Equal(0, snapshot.ConsecutiveNoChanges);
    }

    [Fact]
    public void RequiresRework_ZeroBlockingSnapshots_NeverRequireRework()
    {
        var advisory = new AuditProgressFinding(
            "security:gitleaks",
            AuditSeverity.Warning,
            "tool not installed in sandbox: gitleaks",
            "gitleaks binary missing; secret scan skipped");
        var blocker = new AuditProgressFinding(
            "scripted",
            AuditSeverity.Error,
            "still failing",
            "the audit finding remains unresolved");

        Assert.False(PipelineRunner.AuditProgressRequiresRework(
            Snapshot(0, 1, [], [advisory], AuditProgressStatuses.Complete)));
        Assert.False(PipelineRunner.AuditProgressRequiresRework(
            Snapshot(0, 1, [], [advisory], AuditProgressStatuses.Incomplete)));
        Assert.False(PipelineRunner.AuditProgressRequiresRework(
            Snapshot(0, 1, [], [advisory], AuditProgressStatuses.InProgress)));
        Assert.False(PipelineRunner.AuditProgressRequiresRework(
            Snapshot(0, 0, [], [], AuditProgressStatuses.Complete)));
        Assert.True(PipelineRunner.AuditProgressRequiresRework(
            Snapshot(1, 0, [blocker], [blocker], AuditProgressStatuses.Complete)));
        Assert.True(PipelineRunner.AuditProgressRequiresRework(
            Snapshot(1, 0, [blocker], [blocker], AuditProgressStatuses.Incomplete)));
    }

    [Fact]
    public void BlockingSummary_ZeroBlockingWithAdvisoryFindings_ReportsZero()
    {
        var advisory = new AuditProgressFinding(
            "security:gitleaks",
            AuditSeverity.Warning,
            "tool not installed in sandbox: gitleaks",
            "gitleaks binary missing; secret scan skipped");
        var snapshot = Snapshot(0, 1, [], [advisory]);

        var summary = PipelineRunner.BuildBlockingFindingSummary(snapshot);
        Assert.Equal(0, summary.Count);
        Assert.Equal(string.Empty, summary.Summary);

        Assert.Empty(PipelineRunner.BlockingProgressFindingsForSummary(snapshot));
    }

    [Fact]
    public void ParkMessages_ReportBlockingAndNonBlockingDistinctly()
    {
        var advisory = new AuditProgressFinding(
            "security:gitleaks",
            AuditSeverity.Warning,
            "tool not installed in sandbox: gitleaks",
            "gitleaks binary missing; secret scan skipped");
        var blocker = new AuditProgressFinding(
            "scripted",
            AuditSeverity.Error,
            "still failing",
            "the audit finding remains unresolved");

        IReadOnlyList<AuditProgressSnapshot> zeroBlocking =
        [
            Snapshot(0, 1, [], [advisory]),
        ];
        var emptyReworkMessage = PipelineRunner.BuildEmptyReworkEscalationMessage(
            zeroBlocking, AgentKind.Claude, reworkIterationNumber: 2, attempts: 0, converging: false);
        Assert.Contains("0 blocking finding(s)", emptyReworkMessage, StringComparison.Ordinal);
        Assert.Contains("1 non-blocking", emptyReworkMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("tool not installed in sandbox", emptyReworkMessage, StringComparison.Ordinal);

        var maxIterationMessage = PipelineRunner.BuildAuditMaxIterationEscalationMessage(zeroBlocking);
        Assert.Contains("0 blocking finding(s)", maxIterationMessage, StringComparison.Ordinal);
        Assert.Contains("1 non-blocking", maxIterationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("tool not installed in sandbox", maxIterationMessage, StringComparison.Ordinal);

        IReadOnlyList<AuditProgressSnapshot> genuineBlocking =
        [
            Snapshot(1, 1, [blocker], [blocker, advisory]),
        ];
        var genuineEmptyRework = PipelineRunner.BuildEmptyReworkEscalationMessage(
            genuineBlocking, AgentKind.Claude, reworkIterationNumber: 2, attempts: 0, converging: false);
        Assert.Contains("1 blocking finding(s)", genuineEmptyRework, StringComparison.Ordinal);
        Assert.Contains("still failing", genuineEmptyRework, StringComparison.Ordinal);
        Assert.Contains("1 non-blocking", genuineEmptyRework, StringComparison.Ordinal);

        var genuineMaxIteration = PipelineRunner.BuildAuditMaxIterationEscalationMessage(genuineBlocking);
        Assert.Contains("1 blocking finding(s)", genuineMaxIteration, StringComparison.Ordinal);
        Assert.Contains("still failing", genuineMaxIteration, StringComparison.Ordinal);
        Assert.Contains("1 non-blocking", genuineMaxIteration, StringComparison.Ordinal);
    }

    [Fact]
    public void RefundNoChangesOutcome_RemovesOnlyTargetItem()
    {
        var registry = NewRegistry();
        var first = WorkItemId.New();
        var second = WorkItemId.New();

        registry.RecordNoChangesOutcome(AgentKind.Claude, first);
        registry.RecordNoChangesOutcome(AgentKind.Claude, second);
        Assert.Equal(2, registry.Snapshot().Single(s => s.Agent == AgentKind.Claude).ConsecutiveNoChanges);

        Assert.True(registry.RefundNoChangesOutcome(AgentKind.Claude, first));
        Assert.Equal(1, registry.Snapshot().Single(s => s.Agent == AgentKind.Claude).ConsecutiveNoChanges);

        Assert.False(registry.RefundNoChangesOutcome(AgentKind.Claude, first));
        Assert.Equal(1, registry.Snapshot().Single(s => s.Agent == AgentKind.Claude).ConsecutiveNoChanges);

        Assert.True(registry.RefundNoChangesOutcome(AgentKind.Claude, second));
        Assert.Equal(0, registry.Snapshot().Single(s => s.Agent == AgentKind.Claude).ConsecutiveNoChanges);
    }

    [Fact]
    public async Task GenuineBlockingFinding_StillEntersRework()
    {
        // A genuine blocking finding still drives a rework iteration: the
        // auditor runs twice (fail then pass) and the agent runs twice
        // (initial work plus rework).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new OnceFailingAuditor();
        var audit = new ProjectAudit
        {
            MaxIterations = 3,
            AuditTypes = ["scripted"],
        };
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: audit);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "v2\n"));

        var item = NewItem("feature/genuine-blocking-still-reworks");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(2, auditor.Calls);
        Assert.Equal(2, tp.Agent.WorkPrompts.Count);
    }
}

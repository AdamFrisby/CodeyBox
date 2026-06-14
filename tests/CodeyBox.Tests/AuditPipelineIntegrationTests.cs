using CodeyBox.Core;
using CodeyBox.Audit.Presets;
using CodeyBox.Orchestrator;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Audit-loop integration tests using a scripted auditor.
///   - audit passes first iteration → straight to merge → Done
///   - audit fails then passes after rework → Done
///   - audit fails max iterations → AuditFailed (terminal)
///   - rework agent makes no changes → fail fast (Failed)
///   - no auditors registered → audit phase is a no-op
/// </summary>
[Collection("GlobalSerilog")]
public sealed class AuditPipelineIntegrationTests : IDisposable
{
    private readonly string _workspace;
    public AuditPipelineIntegrationTests() => _workspace = Directory.CreateTempSubdirectory("codeybox-audit-").FullName;
    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task AuditPasses_FirstIteration_ReachesDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditFailsThenPassesAfterRework_ReachesDone()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "needs fix", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-after-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task AuditFailsAllIterations_ReachesAuditFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor], maxAuditIterations: 1);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("did not pass after 1 iterations", final.LastError);
    }

    [Fact]
    public async Task AuditReachesMaxIterations_WithProgress_ParksForOperatorAndPreservesWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false,
            [
                new AuditFinding("Lint", AuditSeverity.Error, "first remaining gap", "x", "tests/A.cs:1"),
                new AuditFinding("Lint", AuditSeverity.Error, "second remaining gap", "x", "tests/B.cs:2"),
            ]),
            new AuditOutcome(false,
            [
                new AuditFinding("Lint", AuditSeverity.Error, "second remaining gap", "x", "tests/B.cs:2"),
            ]),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            webhookDispatcher: webhooks);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "work iteration\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "rework iteration\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Equal(item.WorkBranch, final.WorkBranch);
        Assert.Contains("parked for operator review", final.LastError);

        var escalation = Assert.Single(webhooks.Events, e => e.Event == "work_item.needs_operator_input");
        var details = Assert.IsType<AuditMaxIterationsEscalationDetails>(escalation.Details);
        Assert.True(details.ProgressObserved);
        Assert.Contains("blocking_findings_decreased", details.ProgressSignals);
        Assert.Equal(2, details.History.Count);
        Assert.Equal(2, details.History[0].Findings.Count);
        Assert.Single(details.History[1].Findings);
        Assert.Single(details.RemainingBlockingFindings);
        Assert.Equal("Lint", details.History[0].Findings[0].Auditor);
        Assert.Equal("Error", details.History[0].Findings[0].Severity);
        Assert.Equal("first remaining gap", details.History[0].Findings[0].Title);
        Assert.Equal("x", details.History[0].Findings[0].Description);
        Assert.Equal("tests/A.cs:1", details.History[0].Findings[0].Location);
        Assert.Equal("second remaining gap", details.RemainingBlockingFindings[0].Title);

        var bareRepo = tp.GitHost.GetRepoPath(await tp.GitHost.EnsureRepositoryAsync(item.Id, seed));
        var (_, branchFile, _) = await TestSupport.RunGit(bareRepo, "show", $"{final.WorkBranch}:slow.txt");
        Assert.Equal("rework iteration\n", branchFile);
    }

    [Fact]
    public async Task AuditReachesMaxIterations_WithStableFindingButBranchProgress_ParksForOperator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("tests:meaningfulness-review", AuditSeverity.Error, "assert real contract", "add direct assertions", "tests/WorkerPool.cs:10")]),
            new AuditOutcome(false, [new AuditFinding("tests:meaningfulness-review", AuditSeverity.Error, "assert real contract", "add direct assertions", "tests/WorkerPool.cs:10")]),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            webhookDispatcher: webhooks);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("coverage.txt", "round 1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("coverage.txt", "round 2\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        var details = Assert.IsType<AuditMaxIterationsEscalationDetails>(
            Assert.Single(webhooks.Events, e => e.Event == "work_item.needs_operator_input").Details);
        Assert.Contains("work_branch_tip_changed", details.ProgressSignals);
        Assert.Single(details.RemainingBlockingFindings);
        Assert.Equal("assert real contract", details.RemainingBlockingFindings[0].Title);
    }

    [Fact]
    public async Task AuditReachesMaxIterations_WithOnlyNonBlockingFindingDecrease_ParksForOperator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false,
            [
                new AuditFinding("quality:llm-review", AuditSeverity.Error, "still blocked", "x", "src/A.cs:1"),
                new AuditFinding("quality:llm-review", AuditSeverity.Warning, "cleanup note", "y", "src/B.cs:2"),
            ]),
            new AuditOutcome(false,
            [
                new AuditFinding("quality:llm-review", AuditSeverity.Error, "still blocked", "x", "src/A.cs:1"),
            ]),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            webhookDispatcher: webhooks);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var details = Assert.IsType<AuditMaxIterationsEscalationDetails>(
            Assert.Single(webhooks.Events, e => e.Event == "work_item.needs_operator_input").Details);
        Assert.Contains("total_findings_decreased", details.ProgressSignals);
    }

    [Fact]
    public async Task AuditReachesMaxIterations_WithChangedBlockingFinding_ParksForOperator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("quality:llm-review", AuditSeverity.Error, "old blocker", "x", "src/A.cs:1")]),
            new AuditOutcome(false, [new AuditFinding("quality:llm-review", AuditSeverity.Error, "new blocker", "x", "src/B.cs:2")]),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            webhookDispatcher: webhooks);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var details = Assert.IsType<AuditMaxIterationsEscalationDetails>(
            Assert.Single(webhooks.Events, e => e.Event == "work_item.needs_operator_input").Details);
        Assert.Contains("blocking_findings_changed", details.ProgressSignals);
        Assert.Equal("new blocker", details.RemainingBlockingFindings[0].Title);
    }

    [Fact]
    public async Task RetryParkedAuditMaxIterations_ContinuesAuditHistoryAtNextIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("tests:meaningfulness-review", AuditSeverity.Error, "assert real contract", "x", "tests/A.cs:1")]),
            new AuditOutcome(false, [new AuditFinding("tests:meaningfulness-review", AuditSeverity.Error, "assert real contract", "x", "tests/A.cs:1")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            auditReportStore: reports,
            webhookDispatcher: new CapturingWebhookDispatcher());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "work iteration\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "rework iteration\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var parked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, parked!.State);

        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(
            tp.Store,
            queue,
            tp.GitHost,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(parked, from: null);
        Assert.True(retry.Success, retry.Error);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "resumed rework iteration\n"));
        var resumed = await tp.Store.GetAsync(item.Id);
        await tp.Pipeline.RunAsync(resumed!, CancellationToken.None);

        Assert.Equal([1, 2, 3], auditor.SeenIterations);
        var iterations = await tp.Store.GetIterationsAsync(item.Id);
        Assert.Contains(iterations, i => i.Iteration == 3);
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        var bareRepo = tp.GitHost.GetRepoPath(await tp.GitHost.EnsureRepositoryAsync(item.Id, seed));
        var (_, branchFile, _) = await TestSupport.RunGit(bareRepo, "show", $"{final.WorkBranch}:slow.txt");
        Assert.Equal("resumed rework iteration\n", branchFile);
    }

    [Fact]
    public async Task RetryParkedAuditMaxIterations_AutoPicksAuditAndKeepsWorkBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false,
            [
                new AuditFinding("Lint", AuditSeverity.Error, "first remaining gap", "x", "tests/A.cs:1"),
                new AuditFinding("Lint", AuditSeverity.Error, "second remaining gap", "x", "tests/B.cs:2"),
            ]),
            new AuditOutcome(false,
            [
                new AuditFinding("Lint", AuditSeverity.Error, "second remaining gap", "x", "tests/B.cs:2"),
            ]),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            webhookDispatcher: new CapturingWebhookDispatcher());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "work iteration\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "rework iteration\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var parked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, parked!.State);

        var queue = new InMemoryTaskQueue();
        var retrier = new WorkItemRetrier(
            tp.Store,
            queue,
            tp.GitHost,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(parked, from: null);

        Assert.True(retry.Success, retry.Error);
        Assert.Equal("audit", retry.ActualFrom);
        Assert.Equal(WorkItemState.WorkComplete, retry.ResumeState);
        var resumed = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);
        Assert.Equal(parked.WorkBranch, resumed.WorkBranch);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RetryFromWork_IgnoresAuditHistoryFromPreviousBranchAttempt()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("tests:meaningfulness-review", AuditSeverity.Error, "old branch blocker", "x", "tests/A.cs:1")]),
            new AuditOutcome(false, [new AuditFinding("tests:meaningfulness-review", AuditSeverity.Error, "old branch blocker", "x", "tests/A.cs:1")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            auditReportStore: reports,
            webhookDispatcher: new CapturingWebhookDispatcher());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "old work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "old rework\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var parked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, parked!.State);

        var retrier = new WorkItemRetrier(
            tp.Store,
            new InMemoryTaskQueue(),
            tp.GitHost,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(parked, from: "work");
        Assert.True(retry.Success, retry.Error);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "fresh work\n"));
        var resumed = await tp.Store.GetAsync(item.Id);
        await tp.Pipeline.RunAsync(resumed!, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1, 2, 1], auditor.SeenIterations);
    }

    [Fact]
    public async Task PartialPersistedAuditReports_DoNotAdvanceRecoveredIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var auditorA = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        var auditorB = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorB");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditorA, auditorB],
            maxAuditIterations: 2,
            auditReportStore: reports);

        var item = NewItem();
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "partial.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);
        reports.Add(new AuditReport
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = item.Id.ToString(),
            Iteration = 1,
            AuditorName = "AuditorA",
            AuditorKind = "tool",
            WorstSeverity = "Error",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1).AddSeconds(1),
            DurationMs = 1000,
            Findings =
            [
                new AuditReportFinding(
                    "old-finding",
                    "Error",
                    "partial stale blocker",
                    "only one auditor persisted before shutdown",
                    ["tests/A.cs"],
                    [1]),
            ],
        });

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1], auditorA.SeenIterations);
        Assert.Equal([1], auditorB.SeenIterations);
    }

    [Fact]
    public async Task RecoveredWorkComplete_AuditStartTransitionPreservesRecoveryAttempts()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            webhookDispatcher: webhooks);

        var item = NewItem() with { RecoveryAttempts = 2 };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var auditing = Assert.Single(webhooks.Events, e => e.Event == "work_item.auditing");
        var auditingItem = Assert.IsType<WorkItem>(auditing.WorkItem);
        Assert.Equal(WorkItemState.Auditing, auditingItem.State);
        Assert.Equal(2, auditingItem.RecoveryAttempts);
        Assert.Equal(WorkItemState.Done, (await tp.Store.GetAsync(item.Id))!.State);
    }

    [Fact]
    public async Task RecoveredWorkComplete_AuditFailureVerdictPreservesRecoveryAttempts()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "still broken", "x")]),
        ], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 1);

        var item = NewItem() with { RecoveryAttempts = 2 };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal(2, final.RecoveryAttempts);
        Assert.Equal([1], auditor.SeenIterations);
    }

    [Fact]
    public async Task StopOnFirstPersistedAuditReports_AdvanceRecoveredIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var auditorA = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("AuditorA", AuditSeverity.Error, "first blocker", "x")]),
            new AuditOutcome(false, [new AuditFinding("AuditorA", AuditSeverity.Error, "first blocker", "x")]),
            new AuditOutcome(true, []),
        ], "AuditorA");
        var auditorB = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorB");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditorA, auditorB],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 2,
                AuditTypes = ["scripted"],
                StopOnFirstFailure = true,
            },
            auditReportStore: reports,
            webhookDispatcher: new CapturingWebhookDispatcher());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "work iteration\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "rework iteration\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var parked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, parked!.State);

        var retrier = new WorkItemRetrier(
            tp.Store,
            new InMemoryTaskQueue(),
            tp.GitHost,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkItemRetrier>.Instance);
        var retry = await retrier.RetryAsync(parked, from: null);
        Assert.True(retry.Success, retry.Error);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("slow.txt", "resumed rework iteration\n"));
        var resumed = await tp.Store.GetAsync(item.Id);
        await tp.Pipeline.RunAsync(resumed!, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1, 2, 3], auditorA.SeenIterations);
        Assert.Equal([3], auditorB.SeenIterations);
    }

    [Fact]
    public async Task RequiredBuildPersistedAuditReports_AdvanceRecoveredIteration_WhenBuildReportPresent()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            auditReportStore: reports,
            requiredBuildVerifier: new TestRequiredBuildVerifier(
                RequiredBuildProbeResult.Applies,
                RequiredBuildVerificationResult.Passed(0, "ok")));

        var item = NewItem();
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        reports.Add(Report(item.Id, 1, "AuditorA", "Error", startedAt,
        [
            new AuditReportFinding("old-a", "Error", "old blocker", "x", ["tests/A.cs"], [1]),
        ]));
        reports.Add(Report(item.Id, 1, RequiredBuildGateIdentity.AuditorName, "None", startedAt.AddSeconds(1), []));
        await tp.Store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt: null,
            Progress(1, 2,
            [
                Payload("AuditorA", "Error", "old blocker", "x", "tests/A.cs:1"),
            ]),
            startedAt.AddSeconds(2));

        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "recovered rework\n"));
        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([2], auditor.SeenIterations);
    }

    [Fact]
    public async Task ExhaustedPersistedAuditHistory_WithBlockingFindings_DoesNotPassOnRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var webhooks = new CapturingWebhookDispatcher();
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: ProjectAudit.MaxIterationBudget,
            auditReportStore: reports,
            webhookDispatcher: webhooks);

        var item = NewItem() with { AuditMaxIterations = ProjectAudit.MaxIterationBudget };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        reports.Add(Report(item.Id, ProjectAudit.MaxIterationBudget - 1, "AuditorA", "Error", startedAt,
        [
            new AuditReportFinding("old-a", "Error", "first blocker", "x", ["tests/A.cs"], [1]),
            new AuditReportFinding("old-b", "Error", "second blocker", "x", ["tests/B.cs"], [2]),
        ]));
        reports.Add(Report(item.Id, ProjectAudit.MaxIterationBudget, "AuditorA", "Error", startedAt.AddSeconds(1),
        [
            new AuditReportFinding("old-b", "Error", "second blocker", "x", ["tests/B.cs"], [2]),
        ]));
        await tp.Store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt: null,
            Progress(ProjectAudit.MaxIterationBudget - 1, ProjectAudit.MaxIterationBudget,
            [
                Payload("AuditorA", "Error", "first blocker", "x", "tests/A.cs:1"),
                Payload("AuditorA", "Error", "second blocker", "x", "tests/B.cs:2"),
            ]),
            startedAt.AddSeconds(2));
        await tp.Store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt: null,
            Progress(ProjectAudit.MaxIterationBudget, ProjectAudit.MaxIterationBudget,
            [
                Payload("AuditorA", "Error", "second blocker", "x", "tests/B.cs:2"),
            ]),
            startedAt.AddSeconds(3));

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Empty(auditor.SeenIterations);
        Assert.Single(webhooks.Events, e => e.Event == "work_item.needs_operator_input");
    }

    [Fact]
    public async Task ExhaustedPersistedAuditHistory_WithUnchangedBlockingFindings_HardFails()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: ProjectAudit.MaxIterationBudget,
            webhookDispatcher: webhooks);

        var item = NewItem() with { AuditMaxIterations = ProjectAudit.MaxIterationBudget };
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);

        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var unchangedBlocking =
            new[] { Payload("AuditorA", "Error", "same blocker", "x", "tests/A.cs:1") };
        await tp.Store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt: null,
            Progress(ProjectAudit.MaxIterationBudget - 1, ProjectAudit.MaxIterationBudget, unchangedBlocking),
            startedAt);
        await tp.Store.RecordAuditProgressAsync(
            item.Id,
            workAttemptStartedAt: null,
            Progress(ProjectAudit.MaxIterationBudget, ProjectAudit.MaxIterationBudget, unchangedBlocking),
            startedAt.AddSeconds(1));

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Contains("same blocker", final.LastError);
        Assert.Empty(auditor.SeenIterations);
        Assert.DoesNotContain(webhooks.Events, e => e.Event == "work_item.needs_operator_input");
        Assert.Single(webhooks.Events, e => e.Event == "work_item.audit_failed");
    }

    [Fact]
    public async Task RequiredBuildPersistedAuditReports_DoNotAdvanceRecoveredIteration_WhenBuildReportMissing()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new CapturingAuditReportStore();
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            auditReportStore: reports,
            requiredBuildVerifier: new TestRequiredBuildVerifier(
                RequiredBuildProbeResult.Applies,
                RequiredBuildVerificationResult.Passed(0, "ok")));

        var item = NewItem();
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);
        reports.Add(Report(item.Id, 1, "AuditorA", "Error", DateTimeOffset.UtcNow.AddMinutes(-1),
        [
            new AuditReportFinding("old-a", "Error", "old blocker", "x", ["tests/A.cs"], [1]),
        ]));

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1], auditor.SeenIterations);
    }

    [Fact]
    public async Task AuditReportLoadFailure_DoesNotControlAuditRetry()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new ThrowingAuditReportStore();
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            auditReportStore: reports);

        var item = NewItem();
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1], auditor.SeenIterations);
    }

    [Fact]
    public async Task AuditReportPersistenceFailure_DoesNotFailWorkItem()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var reports = new ThrowingCreateAuditReportStore();
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            auditReportStore: reports);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work complete\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1], auditor.SeenIterations);
    }

    [Fact]
    public async Task AuditProgressLoadFailure_TransitionsToFailedWithInfrastructureKind()
    {
        // Durable audit-progress history is the convergence-preservation
        // primitive: if loading it throws (disk fault, corruption, missing
        // table), restarting the audit loop from iteration 1 would silently
        // discard prior trajectory. PipelineRunner.RunAsync therefore wraps the
        // load failure in AuditHistoryLoadFailedException and routes the work
        // item to Failed/failureKind=infrastructure so the operator can
        // intervene rather than silently re-pay the iteration budget.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            auditProgressOverride: new ThrowingGetAuditProgressStore());

        var item = NewItem();
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed, item.BaseBranch);
        var bareRepo = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(bareRepo, item.WorkBranch!, "work.txt", "work complete\n", "work commit");
        var workComplete = item with { State = WorkItemState.WorkComplete };
        await tp.Store.CreateAsync(workComplete);

        await tp.Pipeline.RunAsync(workComplete, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("audit", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditProgressPersistenceFailure_TransitionsToFailedWithInfrastructureKind()
    {
        // Persistence is the durability boundary: an iteration that ran but
        // never recorded its progress would re-run on retry with stale history,
        // wasting the iteration the operator was already charged for. Wrap the
        // persistence failure in AuditHistoryPersistenceFailedException so the
        // pipeline transitions the work item to Failed/failureKind=infrastructure
        // and the operator knows the trajectory was lost.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor([new AuditOutcome(true, [])], "AuditorA");
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 2,
            auditProgressOverride: new ThrowingRecordAuditProgressStore());
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work complete\n"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal("infrastructure", final.FailureKind);
        Assert.Contains("audit", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkItemAuditMaxIterations_ExtendsProjectAuditIterationBudget()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("quality:llm-review", AuditSeverity.Error, "needs one rework", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                BudgetOverrideMaxIterations = 2,
                AuditTypes = ["scripted"],
            });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("priority.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("priority.txt", "rework\n"));

        var item = NewItem() with { AuditMaxIterations = 2 };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1, 2], auditor.SeenIterations);
    }

    [Fact]
    public async Task AuditComplexityBudget_ExtendsProjectAuditIterationBudget()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("quality:llm-review", AuditSeverity.Error, "needs rework", "x")]),
            new AuditOutcome(false, [new AuditFinding("quality:llm-review", AuditSeverity.Error, "needs another rework", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                BudgetOverrideMaxIterations = 3,
                AuditTypes = ["scripted"],
                ComplexityIterationBudgets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hard"] = 3,
                },
            });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("complex.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("complex.txt", "rework-1\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("complex.txt", "rework-2\n"));

        var item = NewItem() with { AuditComplexity = "hard" };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal([1, 2, 3], auditor.SeenIterations);
    }

    [Fact]
    public async Task WorkItemAuditMaxIterations_WithoutProjectOverrideCap_DoesNotExtendProjectBudget()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("quality:llm-review", AuditSeverity.Error, "still blocked", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            maxAuditIterations: 1);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("priority.txt", "work\n"));

        var item = NewItem() with { AuditMaxIterations = 2 };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal([1], auditor.SeenIterations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public async Task AuditComplexityBudget_NullOrUnknownComplexity_DoesNotUseConfiguredBudget(string? complexity)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("quality:llm-review", AuditSeverity.Error, "still blocked", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectAudit: new ProjectAudit
            {
                MaxIterations = 1,
                BudgetOverrideMaxIterations = 3,
                AuditTypes = ["scripted"],
                ComplexityIterationBudgets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hard"] = 3,
                },
            });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("complex.txt", "work\n"));

        var item = NewItem() with { AuditComplexity = complexity };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.Equal([1], auditor.SeenIterations);
    }

    [Fact]
    public async Task ReworkProducesNoChanges_FailsFast()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "fix me", "x")]),
            new AuditOutcome(true, []),
        ]);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "same-content"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "same-content"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("no changes", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoAuditorsRegistered_SkipsPhaseEntirely()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed); // no auditors
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "one"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task ProjectDefaultUatProfile_AuditLogRecordsOnlyUatAuditors()
    {
        var sink = new TestSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
            using var tp = TestSupport.BuildPipeline(
                _workspace,
                seed,
                projectAudit: new ProjectAudit
                {
                    Profile = AuditProfilePresets.Uat,
                    Profiles = AuditProfilePresets.CreateBuiltIns(),
                },
                presetCatalogOverride: new UatIntegrationCatalog());
            tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "one"));

            var item = NewItem();
            await tp.Store.CreateAsync(item);
            await tp.Pipeline.RunAsync(item, CancellationToken.None);

            var final = await tp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, final!.State);

            var auditorRuns = sink.Events
                .Where(e => GetScalar<string>(e, "EventName") == "auditor.run")
                .Select(e => GetScalar<string>(e, "AuditorName") ?? string.Empty)
                .ToArray();

            Assert.Equal(
                [
                    "csharp:format-check",
                    "csharp:build-WaE",
                    "csharp:test-pass",
                    "security:gitleaks",
                    "security:semgrep",
                    "security:llm-review",
                    "cheating:deterministic-patterns",
                ],
                auditorRuns);

            Assert.DoesNotContain("completeness:llm-review", auditorRuns);
            Assert.DoesNotContain("cheating:llm-review", auditorRuns);

            var profileEvent = Assert.Single(sink.Events,
                e => GetScalar<string>(e, "EventName") == "audit.profile_selected");
            Assert.Equal(AuditProfilePresets.Uat, GetScalar<string>(profileEvent, "AuditProfile"));
            Assert.Equal(auditorRuns, GetStringSequence(profileEvent, "AuditorNames"));
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task WorkItemAuditorProfile_InvokesOnlyRequestedProfileAuditors()
    {
        var sink = new TestSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
            using var tp = TestSupport.BuildPipeline(
                _workspace,
                seed,
                projectAudit: new ProjectAudit
                {
                    AuditTypes = ["security"],
                    Profiles = new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["uat"] = new() { AuditTypes = ["cheating"] },
                    },
                },
                presetCatalogOverride: new UatIntegrationCatalog());
            tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "one"));

            var item = NewItem() with { AuditorProfile = "uat" };
            await tp.Store.CreateAsync(item);
            await tp.Pipeline.RunAsync(item, CancellationToken.None);

            var auditorRuns = sink.Events
                .Where(e => GetScalar<string>(e, "EventName") == "auditor.run")
                .Select(e => GetScalar<string>(e, "AuditorName") ?? string.Empty)
                .ToArray();

            Assert.Equal(["cheating:deterministic-patterns", "cheating:llm-review"], auditorRuns);
            Assert.DoesNotContain("security:gitleaks", auditorRuns);

            var profileEvent = Assert.Single(sink.Events,
                e => GetScalar<string>(e, "EventName") == "audit.profile_selected");
            Assert.Equal("uat", GetScalar<string>(profileEvent, "AuditProfile"));
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previousLogger;
        }
    }

    [Fact]
    public async Task NullAuditorProfile_InvokesProjectDefaultProfileAuditors()
    {
        var sink = new TestSink();
        var previousLogger = Log.Logger;
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(sink)
            .CreateLogger();

        try
        {
            var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
            using var tp = TestSupport.BuildPipeline(
                _workspace,
                seed,
                projectAudit: new ProjectAudit
                {
                    AuditTypes = ["security"],
                    Profiles = new Dictionary<string, ProjectAudit>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["uat"] = new() { AuditTypes = ["cheating"] },
                    },
                },
                presetCatalogOverride: new UatIntegrationCatalog());
            tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "one"));

            var item = NewItem();
            await tp.Store.CreateAsync(item);
            await tp.Pipeline.RunAsync(item, CancellationToken.None);

            var auditorRuns = sink.Events
                .Where(e => GetScalar<string>(e, "EventName") == "auditor.run")
                .Select(e => GetScalar<string>(e, "AuditorName") ?? string.Empty)
                .ToArray();

            Assert.Equal(["security:gitleaks", "security:semgrep", "security:llm-review"], auditorRuns);
            Assert.DoesNotContain("cheating:deterministic-patterns", auditorRuns);

            var profileEvent = Assert.Single(sink.Events,
                e => GetScalar<string>(e, "EventName") == "audit.profile_selected");
            Assert.Equal(ProjectAudit.DefaultProfileName, GetScalar<string>(profileEvent, "AuditProfile"));
        }
        finally
        {
            Log.CloseAndFlush();
            Log.Logger = previousLogger;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostShutdownDuringAudit_StopsAfterAuditorDrains(bool blockingFinding)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var auditor = new DrainingAuditor(blockingFinding);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, auditors: [auditor]);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        using var hostShutdown = new CancellationTokenSource();
        var run = Task.Run(() => tp.Pipeline.RunAsync(item, CancellationToken.None, hostShutdown.Token));

        await auditor.Started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await hostShutdown.CancelAsync();
        auditor.Release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Auditing, final!.State);
        Assert.Empty(tp.Agent.WorkPlan);
    }

    private sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class ScriptedAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _plan;
        public ScriptedAuditor(IEnumerable<AuditOutcome> plan, string name = "Scripted")
        {
            _plan = new Queue<AuditOutcome>(plan);
            Name = name;
        }
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public List<int> SeenIterations { get; } = [];
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            if (_plan.Count == 0) throw new InvalidOperationException("no plan entries left");
            SeenIterations.Add(context.Iteration);
            var outcome = _plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }

    private sealed class CapturingAuditReportStore : IAuditReportStore
    {
        private readonly object _gate = new();
        private readonly List<AuditReport> _reports = [];

        public void Add(AuditReport report)
        {
            lock (_gate)
                _reports.Add(report);
        }

        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
        {
            lock (_gate)
                _reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
        {
            lock (_gate)
            {
                return Task.FromResult<IReadOnlyList<AuditReport>>(
                    _reports
                        .Where(r => r.WorkItemId == workItemId)
                        .OrderBy(r => r.Iteration)
                        .ThenBy(r => r.AuditorName, StringComparer.Ordinal)
                        .ToList());
            }
        }

        public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class ThrowingAuditReportStore : IAuditReportStore
    {
        public Task CreateAsync(AuditReport report, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => throw new InvalidOperationException("audit report store unavailable");

        public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class ThrowingCreateAuditReportStore : IAuditReportStore
    {
        public Task CreateAsync(AuditReport report, CancellationToken ct = default)
            => throw new InvalidOperationException("audit report insert failed");

        public Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditReport>>([]);

        public Task<string?> GetRawOutputAsync(string workItemId, int iteration, string auditorName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class ThrowingGetAuditProgressStore : IAuditProgressStore
    {
        public Task RecordAuditProgressAsync(
            WorkItemId workItemId,
            DateTimeOffset? workAttemptStartedAt,
            AuditProgressRecord progress,
            DateTimeOffset recordedAt,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AuditProgressRecord>> GetAuditProgressAsync(
            WorkItemId workItemId,
            DateTimeOffset? workAttemptStartedAt,
            CancellationToken ct = default)
            => throw new InvalidOperationException("audit progress store unavailable");
    }

    private sealed class ThrowingRecordAuditProgressStore : IAuditProgressStore
    {
        public Task RecordAuditProgressAsync(
            WorkItemId workItemId,
            DateTimeOffset? workAttemptStartedAt,
            AuditProgressRecord progress,
            DateTimeOffset recordedAt,
            CancellationToken ct = default)
            => throw new InvalidOperationException("audit progress insert failed");

        public Task<IReadOnlyList<AuditProgressRecord>> GetAuditProgressAsync(
            WorkItemId workItemId,
            DateTimeOffset? workAttemptStartedAt,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AuditProgressRecord>>([]);
    }

    private sealed class DrainingAuditor : IAuditor
    {
        private readonly bool _blockingFinding;

        public DrainingAuditor(bool blockingFinding) => _blockingFinding = blockingFinding;

        public string Name => "Draining";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            var findings = _blockingFinding
                ? (IReadOnlyList<AuditFinding>)[new AuditFinding("Draining", AuditSeverity.Error, "needs fix", "x")]
                : [];
            return new AuditResult(!_blockingFinding, findings);
        }
    }

    private sealed class UatIntegrationCatalog : IPresetCatalog
    {
        public IReadOnlyList<IAuditor> ResolveLanguage(string name, PresetContext ctx)
            => name.Equals("csharp", StringComparison.OrdinalIgnoreCase)
                ? [
                    new PassingAuditor("csharp:format-check"),
                    new PassingAuditor("csharp:build-WaE"),
                    new PassingAuditor("csharp:test-pass"),
                ]
                : [];

        public IReadOnlyList<IAuditor> ResolveAuditType(string name, PresetContext ctx)
            => name.ToLowerInvariant() switch
            {
                "security" =>
                [
                    new PassingAuditor("security:gitleaks"),
                    new PassingAuditor("security:semgrep"),
                    new PassingAuditor("security:llm-review"),
                ],
                "cheating" =>
                [
                    new PassingAuditor("cheating:deterministic-patterns"),
                    new PassingAuditor("cheating:llm-review"),
                ],
                _ => [],
            };

        public IReadOnlyList<string> KnownLanguages => ["csharp"];
        public IReadOnlyList<string> KnownAuditTypes => ["security", "cheating"];
        public string LlmPromptFrameTemplate => "{{reviewFocus}}\n{{resultFile}}";
    }

    private sealed class PassingAuditor(string name) : IAuditor
    {
        public string Name { get; } = name;
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
            => Task.FromResult(new AuditResult(true, []));
    }

    private async Task<string> CommitToBareBranchAsync(
        string barePath,
        string branch,
        string fileName,
        string contents,
        string subject)
    {
        var clone = Path.Combine(_workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", branch, "origin/main");
        await File.WriteAllTextAsync(Path.Combine(clone, fileName), contents);
        await TestSupport.RunGit(clone, "add", fileName);
        await TestSupport.RunGit(clone, "commit", "-m", $"{subject}\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        var sha = (await TestSupport.RunGit(clone, "rev-parse", "HEAD")).stdout.Trim();
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
        return sha;
    }

    private static AuditReport Report(
        WorkItemId workItemId,
        int iteration,
        string auditorName,
        string worstSeverity,
        DateTimeOffset startedAt,
        IReadOnlyList<AuditReportFinding> findings) => new()
        {
            Id = Guid.NewGuid().ToString(),
            WorkItemId = workItemId.ToString(),
            Iteration = iteration,
            AuditorName = auditorName,
            AuditorKind = "tool",
            WorstSeverity = worstSeverity,
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(1),
            DurationMs = 1000,
            Findings = findings,
        };

    private static AuditProgressRecord Progress(
        int iteration,
        int maxIterations,
        IReadOnlyList<AuditProgressFinding> findings,
        string? workBranchTip = null)
    {
        var blocking = findings
            .Where(f => f.Severity >= AuditSeverity.Error)
            .ToList();
        var blockingIds = blocking
            .Select(f => FindingIdComputer.Compute(f.AuditorName, f.Title, ParsePayloadFiles(f.Location)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        return new AuditProgressRecord(
            iteration,
            maxIterations,
            blocking.Count,
            findings.Count - blocking.Count,
            blockingIds,
            blocking,
            findings,
            workBranchTip);
    }

    private static AuditProgressFinding Payload(
        string auditor,
        string severity,
        string title,
        string description,
        string? location = null) => new(
            auditor,
            AuditSeverityParser.Parse(severity),
            title,
            description,
            location);

    private static IReadOnlyList<string> ParsePayloadFiles(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return [];
        var first = location.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        var lastColon = first.LastIndexOf(':');
        return [lastColon > 1 && int.TryParse(first[(lastColon + 1)..], out _) ? first[..lastColon] : first];
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not ScalarValue sv)
            return default;
        if (sv.Value is T t)
            return t;
        if (typeof(T) == typeof(int) && sv.Value is long l)
            return (T)(object)(int)l;
        return default;
    }

    private static IReadOnlyList<string> GetStringSequence(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var prop) || prop is not SequenceValue seq)
            return [];
        return seq.Elements
            .OfType<ScalarValue>()
            .Select(v => v.Value?.ToString() ?? string.Empty)
            .ToArray();
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "audit test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
    };
}

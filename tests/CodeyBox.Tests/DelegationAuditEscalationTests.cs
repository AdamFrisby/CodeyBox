using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeyBox.Tests;

/// <summary>
/// Pipeline-level coverage for delegation triggers: automatic escalation
/// when audit iterations reach their configured maximum without passing
/// (and its bounds), operator-note propagation into the brief, and the
/// delegation-failure flag lifecycle. Sweep-level repeated-failure coverage
/// lives in <see cref="DelegationSweepEscalationTests"/>.
///
/// Requires git on PATH.
/// </summary>
[Collection("Pipeline integration")]
public sealed class DelegationAuditEscalationTests : IDisposable
{
    private readonly string _workspace;
    private readonly TestSupport.AmbientGitConfigScope _gitConfigScope;

    public DelegationAuditEscalationTests()
    {
        _workspace = Directory.CreateTempSubdirectory("codeybox-delaudit-").FullName;
        _gitConfigScope = TestSupport.AmbientGitConfigScope.Clear();
    }

    public void Dispose()
    {
        _gitConfigScope.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task AuditMaxIterations_Armed_EscalatesToDelegation()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var escalationOpts = new DelegationEscalationOptions { Enabled = true };
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            auditors: [new ScriptedAuditor([Blocking(2), Blocking(1), Passing()])],
            maxAuditIterations: 2,
            delegationEscalationOptions: escalationOpts);
        var item = NewQueuedItem();
        await tp.Store.CreateAsync(item);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "rework\n"));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Delegating, final!.State);
        Assert.True(final.DelegationRequested);
        Assert.True(final.AutoDelegationEscalated);
        Assert.Contains(DelegationTriggers.AuditMaxIterations, final.DelegationReason);
        // Escalation must not consume the failure signal: the park message
        // rides into LastError and the audit verdicts stay queryable.
        Assert.Contains("max iteration budget", final.LastError);
        var progress = await tp.Store.GetAllAuditProgressForWorkItemAsync(item.Id);
        Assert.True(progress.Count >= 2);
    }

    [Fact]
    public async Task AuditMaxIterations_Disarmed_ParksForOperator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            auditors: [new ScriptedAuditor([Blocking(2), Blocking(1), Passing()])],
            maxAuditIterations: 2);
        var item = NewQueuedItem();
        await tp.Store.CreateAsync(item);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "rework\n"));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.False(final.DelegationRequested);
        Assert.False(final.AutoDelegationEscalated);
        Assert.Contains("max iteration budget", final.LastError);
    }

    [Fact]
    public async Task AuditMaxIterations_ArmedButAlreadyEscalated_ParksForOperator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var escalationOpts = new DelegationEscalationOptions { Enabled = true };
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            auditors: [new ScriptedAuditor([Blocking(2), Blocking(1), Passing()])],
            maxAuditIterations: 2,
            delegationEscalationOptions: escalationOpts);
        // The single automatic escalation was already consumed by an earlier
        // turn: the item reaches the same non-convergence point again.
        var item = NewQueuedItem() with { AutoDelegationEscalated = true };
        await tp.Store.CreateAsync(item);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework.txt", "rework\n"));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(final);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.False(final.DelegationRequested);
    }

    [Fact]
    public async Task OperatorNote_ReachesBrief_AndClearsOnCompletion()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            auditors: [new ScriptedAuditor([Passing(), Passing()])]);
        var item = NewQueuedItem() with { WorkBranch = "feature/delegate-note" };
        var barePath = await EnsureRepoWithPriorWorkAsync(tp, item, seed);
        await tp.Store.CreateAsync(item);

        var escalation = new DelegationEscalationService(tp.Store, tp.Queue, () => new DelegationEscalationOptions());
        var delegated = await escalation.DelegateAsync(
            item, DelegationTriggers.Operator, "focus on the auth race",
            markAutoEscalated: false, failureContext: null);
        Assert.True(delegated.Delegated, delegated.Error);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("delegate.txt", "delegated\n"));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // The note served its purpose in this turn's brief and is gone.
        Assert.Null(final.DelegationNote);
        var recorded = Assert.Single(await tp.DelegationEvents!.ListByWorkItemAsync(item.Id));
        Assert.Equal(DelegationOutcomes.Completed, recorded.Outcome);
        Assert.Contains("## Operator Direction", recorded.Brief);
        Assert.Contains("focus on the auth race", recorded.Brief);
        Assert.Contains("focus on the auth race", tp.Agent.WorkPrompts[0]);

        var (_, blob, _) = await TestSupport.RunGit(barePath, "show", "main:delegate.txt");
        Assert.Equal("delegated\n", blob);
    }

    [Fact]
    public async Task FailedDelegation_SetsFlag_BlocksAuto_AllowsOperator()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            auditors: [new ScriptedAuditor([Passing(), Passing()])]);
        var item = NewQueuedItem() with { WorkBranch = "feature/delegate-fails" };
        await EnsureRepoWithPriorWorkAsync(tp, item, seed);
        await tp.Store.CreateAsync(item);

        var escalation = new DelegationEscalationService(tp.Store, tp.Queue, () => new DelegationEscalationOptions());
        var first = await escalation.DelegateAsync(
            item, DelegationTriggers.Operator, "first try",
            markAutoEscalated: false, failureContext: null);
        Assert.True(first.Delegated, first.Error);
        tp.Agent.WorkResults.Enqueue(new AgentResult(false, "delegate exploded", null, null));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await tp.Store.GetAsync(item.Id);
        Assert.NotNull(parked);
        Assert.Equal(WorkItemState.NeedsOperatorInput, parked!.State);
        Assert.True(parked.DelegationFailed);
        Assert.Null(parked.DelegationNote);
        var recorded = Assert.Single(await tp.DelegationEvents!.ListByWorkItemAsync(item.Id));
        Assert.Equal(DelegationOutcomes.Failed, recorded.Outcome);

        // The automatic path stays closed for this item …
        var armed = new DelegationEscalationService(
            tp.Store, tp.Queue,
            () => new DelegationEscalationOptions { Enabled = true });
        Assert.False(armed.IsAutoTriggerArmed(DelegationTriggers.AuditMaxIterations, parked));
        var auto = await armed.DelegateAsync(
            parked, DelegationTriggers.AuditMaxIterations, null, true, null);
        Assert.False(auto.Delegated);

        // … but an explicit operator delegation still authorizes a turn.
        var manual = await armed.DelegateAsync(
            parked, DelegationTriggers.Operator, "second try", false, null);
        Assert.True(manual.Delegated, manual.Error);
        var rearmed = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Delegating, rearmed!.State);
        Assert.Equal("second try", rearmed.DelegationNote);
    }

    [Fact]
    public async Task RepeatedFailure_FullLoop_FailedDelegationBlocksSecondAuto()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var escalationOpts = new DelegationEscalationOptions { Enabled = true };
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            // One blocking verdict per audit run; the plan never passes so
            // every run ends at the same terminal AuditFailed point.
            auditors: [new ScriptedAuditor([Blocking(1), Blocking(1), Blocking(1), Blocking(1)])],
            maxAuditIterations: 1,
            delegationEscalationOptions: escalationOpts);
        var recovery = new TerminalFailureRecoveryService(
            tp.Store,
            new WorkItemRetrier(
                tp.Store, tp.Queue, tp.GitHost, NullLogger<WorkItemRetrier>.Instance,
                auditProgress: tp.Store),
            new DefaultTerminalFailureClassifier(),
            optionsAccessor: () => new TerminalFailureRecoveryOptions
            {
                Enabled = true,
                BaseBackoff = TimeSpan.FromMinutes(1),
                MaxBackoff = TimeSpan.FromMinutes(30),
                JitterFraction = 0,
                MaxAutoRetriesPerWorkItem = 3,
                PeriodicCheckInterval = TimeSpan.FromMinutes(1),
            },
            log: NullLogger<TerminalFailureRecoveryService>.Instance,
            jitter: _ => 500,
            delegationEscalation: new DelegationEscalationService(tp.Store, tp.Queue, () => escalationOpts));
        var retrier = new WorkItemRetrier(
            tp.Store, tp.Queue, tp.GitHost, NullLogger<WorkItemRetrier>.Instance,
            auditProgress: tp.Store);

        var item = NewQueuedItem();
        await tp.Store.CreateAsync(item);

        // Run 1: work commits, audit fails terminally (episode 1).
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work1.txt", "one\n"));
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var afterRun1 = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, afterRun1!.State);
        Assert.Equal(1, afterRun1.TerminalFailureCount);

        // Sweep 1: below the repeated-failure threshold — stays parked.
        await RunSweepAsync(recovery);
        Assert.Equal(WorkItemState.AuditFailed, (await tp.Store.GetAsync(item.Id))!.State);

        // Operator retries from work; run 2 fails terminally (episode 2).
        var retry1 = await retrier.RetryAsync(afterRun1, from: "work", trigger: "manual");
        Assert.True(retry1.Success, retry1.Error);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work2.txt", "two\n"));
        await tp.Pipeline.RunAsync(await tp.Store.GetAsync(item.Id) ?? item, CancellationToken.None);
        var afterRun2 = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, afterRun2!.State);
        Assert.Equal(2, afterRun2.TerminalFailureCount);

        // Sweep 2: threshold reached — escalates automatically (once).
        await RunSweepAsync(recovery);
        var escalated = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Delegating, escalated!.State);
        Assert.True(escalated.AutoDelegationEscalated);

        // Run 3: the delegation turn itself fails — parks with the flag set.
        tp.Agent.WorkResults.Enqueue(new AgentResult(false, "delegate exploded", null, null));
        await tp.Pipeline.RunAsync(escalated, CancellationToken.None);
        var afterRun3 = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, afterRun3!.State);
        Assert.True(afterRun3.DelegationFailed);

        // Operator retries from work; run 4 fails terminally (episode 3).
        var retry2 = await retrier.RetryAsync(afterRun3, from: "work", trigger: "manual");
        Assert.True(retry2.Success, retry2.Error);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("work4.txt", "four\n"));
        await tp.Pipeline.RunAsync(await tp.Store.GetAsync(item.Id) ?? item, CancellationToken.None);
        var afterRun4 = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, afterRun4!.State);
        Assert.Equal(3, afterRun4.TerminalFailureCount);

        // Sweep 3: the failed delegation blocks a second automatic
        // escalation — the item stays terminally failed and visible.
        await RunSweepAsync(recovery);
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AuditFailed, final!.State);
        Assert.False(final.DelegationRequested);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WorkItem NewQueuedItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "escalation test",
        Prompt = "do the thing",
        BaseBranch = "main",
        PushUpstream = false,
        State = WorkItemState.Queued,
    };

    private static async Task RunSweepAsync(TerminalFailureRecoveryService recovery)
    {
        var sweep = typeof(TerminalFailureRecoveryService).GetMethod(
            "RunPeriodicSweepAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var recoveryOptions = new TerminalFailureRecoveryOptions
        {
            Enabled = true,
            BaseBackoff = TimeSpan.FromMinutes(1),
            MaxBackoff = TimeSpan.FromMinutes(30),
            JitterFraction = 0,
            MaxAutoRetriesPerWorkItem = 3,
            PeriodicCheckInterval = TimeSpan.FromMinutes(1),
        };
        await (Task)sweep.Invoke(recovery, [recoveryOptions, CancellationToken.None])!;
    }

    private async Task<string> EnsureRepoWithPriorWorkAsync(TestPipeline tp, WorkItem item, string seed)
    {
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        var clone = Path.Combine(_workspace, "clone-" + Guid.NewGuid().ToString("N")[..8]);
        await TestSupport.RunGit(_workspace, "clone", barePath, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@test.com");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", item.WorkBranch!, "origin/main");
        await File.WriteAllTextAsync(Path.Combine(clone, "prior.txt"), "prior work\n");
        await TestSupport.RunGit(clone, "add", "prior.txt");
        await TestSupport.RunGit(clone, "commit", "-m", $"prior work\n\n{CodeyBoxTrailers.CoAuthoredBy}");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{item.WorkBranch}");
        return barePath;
    }

    private static AuditOutcome Blocking(int count) =>
        new(false, Enumerable.Range(1, count)
            .Select(i => new AuditFinding("Lint", AuditSeverity.Error, $"needs fix {i}", $"x{i}"))
            .ToList());

    private static AuditOutcome Passing() => new(true, []);

    private sealed record AuditOutcome(bool Passed, IReadOnlyList<AuditFinding> Findings);

    private sealed class ScriptedAuditor(IEnumerable<AuditOutcome> plan) : IAuditor
    {
        private readonly Queue<AuditOutcome> _plan = new(plan);
        public string Name => "Scripted";
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            if (_plan.Count == 0) throw new InvalidOperationException("no plan entries left");
            var outcome = _plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }
}

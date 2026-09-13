using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

/// <summary>
/// End-to-end delegation-phase tests: an item parked in <see cref="WorkItemState.Delegating"/>
/// with an explicit trigger runs exactly one unconstrained turn in a sandbox,
/// then re-enters the normal flow at the audit phase. Failure and no-change
/// outcomes park at <see cref="WorkItemState.NeedsOperatorInput"/> and never
/// re-enter the phase without a new explicit trigger.
///
/// Requires git on PATH.
/// </summary>
[Collection("Pipeline integration")]
public sealed class DelegationPhaseTests : IDisposable
{
    private readonly string _workspace;
    private readonly TestSupport.AmbientGitConfigScope _gitConfigScope;
    public DelegationPhaseTests()
    {
        // The ambient harness may inject GIT_CONFIG_* (e.g.
        // safe.bareRepository=explicit) that changes bare-repo discovery and
        // breaks the LocalGitHost seed/clone plumbing these tests run through.
        _workspace = Directory.CreateTempSubdirectory("codeybox-delegation-").FullName;
        _gitConfigScope = TestSupport.AmbientGitConfigScope.Clear();
    }
    public void Dispose()
    {
        _gitConfigScope.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task Success_RunsOneTurnThenAudits_BeforeAnyMerge()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new RecordingWebhookDispatcher();
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            auditors: [new ScriptedAuditor([Blocking(), Passing()])],
            maxAuditIterations: 3,
            webhookDispatcher: webhooks);
        // The failed cycle left prior work behind; the delegate stacks on top.
        var item = NewDelegatingItem("feature/delegated");
        var barePath = await EnsureRepoWithPriorWorkAsync(tp, item, seed);
        await tp.Store.CreateAsync(item);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("delegate.txt", "delegated\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework-fix.txt", "rework fix\n"));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        // Exactly one attempt was consumed; the one-shot trigger is gone.
        Assert.Equal(1, final.DelegationAttempts);
        Assert.False(final.DelegationRequested);

        // The delegate prompt combined the composed brief with the latitude
        // instruction over the original prompt. (The audit-loop rework turn
        // enqueues a second prompt, so the delegate's is the first.)
        var delegatePrompt = tp.Agent.WorkPrompts[0];
        Assert.Contains("# Delegation task", delegatePrompt);
        Assert.Contains(item.Prompt, delegatePrompt);
        Assert.Contains("latitude", delegatePrompt);

        // The phase ordering is delegation -> audit -> merge: the delegate's
        // change was verified by the same gates, never merged on assurance.
        var snapshots = webhooks.Events.ToList();
        var delegationStarted = snapshots.FindIndex(e => e.Event == "iteration.started"
            && IsPhase(e, "delegation"));
        var auditStarted = snapshots.FindIndex(e => e.Event == "audit.started");
        var mergeStarted = snapshots.FindIndex(e => e.Event == "merge.started");
        Assert.True(delegationStarted >= 0);
        Assert.True(auditStarted > delegationStarted);
        Assert.True(mergeStarted > auditStarted);

        // First-class event: brief + agent/model + resulting diff, retrievable.
        var events = await tp.DelegationEvents!.ListByWorkItemAsync(item.Id);
        var recorded = Assert.Single(events);
        Assert.Equal(1, recorded.Attempt);
        Assert.Equal(DelegationOutcomes.Completed, recorded.Outcome);
        Assert.Equal("claude", recorded.Agent.Value);
        Assert.Contains(item.Title, recorded.Brief);
        Assert.Contains("delegate.txt", recorded.DiffStat + recorded.ResultDiff);

        // The delegate's change actually merged downstream of audit.
        var (_, blob, _) = await TestSupport.RunGit(barePath, "show", "main:delegate.txt");
        Assert.Equal("delegated\n", blob);
    }

    [Fact]
    public async Task NoChanges_ParksAtNeedsOperatorInput_WithReason()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, enableDelegation: true);
        var item = NewDelegatingItem("feature/delegate-empty");
        await EnsureRepoWithPriorWorkAsync(tp, item, seed);
        await tp.Store.CreateAsync(item);
        // Success exit, but nothing written: no diff for the turn.
        tp.Agent.WorkResults.Enqueue(new AgentResult(true, "ok-no-changes", null, null));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Contains("no changes", final.LastError);
        Assert.Equal(1, final.DelegationAttempts);
        Assert.False(final.DelegationRequested);

        var recorded = Assert.Single(await tp.DelegationEvents!.ListByWorkItemAsync(item.Id));
        Assert.Equal(DelegationOutcomes.NoChanges, recorded.Outcome);
        Assert.NotNull(recorded.Reason);
    }

    [Fact]
    public async Task AgentFailure_ParksAtNeedsOperatorInput_AndCannotReenterWithoutTrigger()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, enableDelegation: true);
        var item = NewDelegatingItem("feature/delegate-fails");
        await EnsureRepoWithPriorWorkAsync(tp, item, seed);
        await tp.Store.CreateAsync(item);
        tp.Agent.WorkResults.Enqueue(new AgentResult(false, "delegate exploded", "some stdout", "some stderr"));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var parked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, parked!.State);
        Assert.Contains("reported failure", parked.LastError);
        Assert.Equal(1, parked.DelegationAttempts);
        var promptsAfterFailure = tp.Agent.WorkPrompts.Count;

        var recorded = Assert.Single(await tp.DelegationEvents!.ListByWorkItemAsync(item.Id));
        Assert.Equal(DelegationOutcomes.Failed, recorded.Outcome);

        // The failure path must not re-enter the phase: a Delegating entry
        // smuggled in without a fresh trigger is refused, not run.
        var smuggled = parked with { State = WorkItemState.Delegating };
        await tp.Store.UpdateAsync(smuggled);
        await tp.Pipeline.RunAsync(smuggled, CancellationToken.None);

        var stillParked = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, stillParked!.State);
        Assert.Contains("explicit new trigger", stillParked.LastError);
        Assert.Equal(1, stillParked.DelegationAttempts);
        Assert.Equal(promptsAfterFailure, tp.Agent.WorkPrompts.Count);
        Assert.Single(await tp.DelegationEvents!.ListByWorkItemAsync(item.Id));
    }

    [Fact]
    public async Task WithoutTrigger_DelegatingEntry_ParksInsteadOfRunning()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed, enableDelegation: true);
        var item = NewDelegatingItem("feature/delegate-untriggered") with { DelegationRequested = false };
        await EnsureRepoWithPriorWorkAsync(tp, item, seed);
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, final!.State);
        Assert.Contains("explicit new trigger", final.LastError);
        Assert.Equal(0, final.DelegationAttempts);
        Assert.Empty(tp.Agent.WorkPrompts);
        Assert.Empty(await tp.DelegationEvents!.ListByWorkItemAsync(item.Id));
    }

    [Fact]
    public async Task ExplicitRetryFromDelegation_ReArmsTrigger_ForSecondAttempt()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            auditors: [new ScriptedAuditor([Passing(), Passing()])],
            maxAuditIterations: 3);
        var item = NewDelegatingItem("feature/delegate-retry");
        await EnsureRepoWithPriorWorkAsync(tp, item, seed);
        await tp.Store.CreateAsync(item);
        // First attempt fails and parks with the attempt counted.
        tp.Agent.WorkResults.Enqueue(new AgentResult(false, "first delegate exploded", null, null));
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        Assert.Equal(WorkItemState.NeedsOperatorInput, (await tp.Store.GetAsync(item.Id))!.State);

        // An explicit retry from delegation authorizes exactly one more turn.
        var retrier = new WorkItemRetrier(
            tp.Store, tp.Queue, tp.GitHost, NullLogger<WorkItemRetrier>.Instance);
        var parked = await tp.Store.GetAsync(item.Id);
        var retry = await retrier.RetryAsync(parked!, from: "delegation", trigger: "manual");
        Assert.True(retry.Success);
        Assert.Equal(WorkItemState.Delegating, retry.ResumeState);
        var rearmed = await tp.Store.GetAsync(item.Id);
        Assert.True(rearmed!.DelegationRequested);
        Assert.Contains("manual", rearmed.DelegationReason);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("second-try.txt", "second try\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework-fix.txt", "rework fix\n"));
        await tp.Pipeline.RunAsync(rearmed, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(2, final.DelegationAttempts);
        Assert.Equal(2, (await tp.DelegationEvents!.ListByWorkItemAsync(item.Id)).Count);
    }

    [Fact]
    public async Task BuildGateFailure_DoesNotMergeDelegateChange()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(
            _workspace, seed,
            enableDelegation: true,
            auditors: [new ScriptedAuditor([Passing(), Passing()])],
            maxAuditIterations: 1,
            requiredBuildVerifier: new TestRequiredBuildVerifier(
                RequiredBuildProbeResult.Applies,
                RequiredBuildVerificationResult.Failed(1, "build broke")));
        var item = NewDelegatingItem("feature/delegate-broken-build");
        var barePath = await EnsureRepoWithPriorWorkAsync(tp, item, seed);
        await tp.Store.CreateAsync(item);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("broken.txt", "broken\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework-fix.txt", "rework fix\n"));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.NotEqual(WorkItemState.Done, final!.State);
        var merged = await TestSupport.RunGitNoThrow(barePath, "show", "main:broken.txt");
        Assert.NotEqual(0, merged.code);
        var recorded = Assert.Single(await tp.DelegationEvents!.ListByWorkItemAsync(item.Id));
        Assert.Equal(DelegationOutcomes.Completed, recorded.Outcome);
    }

    private static WorkItem NewDelegatingItem(string workBranch) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "delegated work",
        Prompt = "finish the delegated thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        PushUpstream = false,
        State = WorkItemState.Delegating,
        DelegationRequested = true,
        DelegationReason = "test trigger",
    };

    private async Task<string> EnsureRepoWithPriorWorkAsync(TestPipeline tp, WorkItem item, string seed)
    {
        var repoId = await tp.GitHost.EnsureRepositoryAsync(item.Id, seed);
        var barePath = tp.GitHost.GetRepoPath(repoId);
        await CommitToBareBranchAsync(barePath, item.WorkBranch!, "prior.txt", "prior work\n", "prior work");
        return barePath;
    }

    private async Task CommitToBareBranchAsync(
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
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{branch}");
    }

    private static bool IsPhase(WebhookEvent evt, string phase) =>
        evt.Details is IterationStartedDetails started
        && string.Equals(started.Phase, phase, StringComparison.Ordinal);

    private static AuditOutcome Blocking() =>
        new(false, [new AuditFinding("Lint", AuditSeverity.Error, "needs fix", "x")]);

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

    private sealed class RecordingWebhookDispatcher : IWebhookDispatcher
    {
        private readonly List<WebhookEvent> _events = new();
        private readonly object _lock = new();
        public IReadOnlyList<WebhookEvent> Events
        {
            get { lock (_lock) return _events.ToArray(); }
        }
        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
        {
            lock (_lock) _events.Add(evt);
            return Task.CompletedTask;
        }
    }
}

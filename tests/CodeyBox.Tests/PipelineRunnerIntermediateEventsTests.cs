using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Audit.Presets;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;

namespace CodeyBox.Tests;

/// <summary>
/// Pins the intermediate webhook event surface added to the PipelineRunner
/// state machine (iteration.*, audit.*, merge.*). Trackers integrate
/// against this contract — every event must fire at the right transition
/// with the documented shape.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerIntermediateEventsTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerIntermediateEventsTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-intermediate-events-").FullName;

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
    }

    [Fact]
    public async Task SuccessfulRun_EmitsFullEventSequence()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks,
            auditors: [new AlwaysPassAuditor("pass-auditor")]);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("intermediate-events.txt", "x\n"));

        var item = MakeItem("feature/intermediate-events");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var names = webhooks.Events.Select(e => e.Event).ToList();

        // Acceptance: the full sequence across one work-item lifecycle.
        // No rework path: audit passes the first iteration.
        Assert.Equal("iteration.started", names.First(n => n.StartsWith("iteration.", StringComparison.Ordinal)));

        // Each new event type must appear at least once, in the right order.
        var iterStarted = IndexOf(names, "iteration.started");
        var iterCompleted = IndexOf(names, "iteration.completed");
        var auditStarted = IndexOf(names, "audit.started");
        var findingsEmitted = IndexOf(names, "audit.findings.emitted");
        var auditCompleted = IndexOf(names, "audit.completed");
        var mergeStarted = IndexOf(names, "merge.started");
        var mergeCompleted = IndexOf(names, "merge.completed");
        var done = IndexOf(names, "work_item.done");

        Assert.True(iterStarted < iterCompleted);
        Assert.True(iterCompleted < auditStarted);
        Assert.True(auditStarted < findingsEmitted);
        Assert.True(findingsEmitted < auditCompleted);
        Assert.True(auditCompleted < mergeStarted);
        Assert.True(mergeStarted < mergeCompleted);
        Assert.True(mergeCompleted < done);
    }

    [Fact]
    public async Task IterationStartedAndCompleted_WorkPhase_CarryExpectedFields()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("iter-shape.txt", "x\n"));

        var item = MakeItem("feature/iter-shape");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var started = webhooks.Events.First(e => e.Event == "iteration.started");
        var startedDetails = Assert.IsType<IterationStartedDetails>(started.Details);
        Assert.Equal(item.Id.ToString(), startedDetails.WorkItemId);
        Assert.Equal(1, startedDetails.Iteration);
        Assert.Equal(IterationPhase.Work, startedDetails.Phase);

        var completed = webhooks.Events.First(e => e.Event == "iteration.completed");
        var completedDetails = Assert.IsType<IterationCompletedDetails>(completed.Details);
        Assert.Equal(item.Id.ToString(), completedDetails.WorkItemId);
        Assert.Equal(1, completedDetails.Iteration);
        Assert.Equal(IterationPhase.Work, completedDetails.Phase);
        Assert.True(completedDetails.DurationMs >= 0);
        // commitSha is best-effort but the work phase committed to the work
        // branch — LocalGitHost.ResolveCommitAsync should resolve it.
        Assert.False(string.IsNullOrEmpty(completedDetails.CommitSha));
    }

    [Fact]
    public async Task IterationStarted_DispatchedAt_IsBoundedByRunWindow()
    {
        // DispatchedAt is the contract field receivers correlate iterations
        // by — if a regression sets it to default(DateTimeOffset) or item.CreatedAt,
        // the value will fall outside the [before, after] window.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("iter-dispatched.txt", "x\n"));

        var item = MakeItem("feature/iter-dispatched");
        await tp.Store.CreateAsync(item);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        var started = webhooks.Events.First(e => e.Event == "iteration.started");
        var details = Assert.IsType<IterationStartedDetails>(started.Details);
        Assert.InRange(details.DispatchedAt, before, after);
    }

    [Fact]
    public async Task AuditStarted_CarriesAuditorNames()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks,
            auditors: [new AlwaysPassAuditor("auditor-A"), new AlwaysPassAuditor("auditor-B")]);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-started.txt", "x\n"));

        var item = MakeItem("feature/audit-started");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var auditStarted = webhooks.Events.First(e => e.Event == "audit.started");
        var details = Assert.IsType<AuditStartedDetails>(auditStarted.Details);
        Assert.Equal(1, details.Iteration);
        Assert.Equal(item.Id.ToString(), details.WorkItemId);
        Assert.Contains("auditor-A", details.AuditorsScheduled);
        Assert.Contains("auditor-B", details.AuditorsScheduled);
    }

    [Fact]
    public async Task AuditStarted_ReportsBuildTestGateBeforeShortCircuitAuditors()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks,
            auditors:
            [
                new OrderedPassAuditor("audit:style"),
                new OrderedPassAuditor("audit:policy-gate", canShortCircuit: true),
                new OrderedPassAuditor("csharp:test-pass", role: AuditorRole.BuildTestGate),
            ]);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-started-order.txt", "x\n"));

        var item = MakeItem("feature/audit-started-order");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var auditStarted = webhooks.Events.First(e => e.Event == "audit.started");
        var details = Assert.IsType<AuditStartedDetails>(auditStarted.Details);
        Assert.Equal(
            ["csharp:test-pass", "audit:policy-gate", "audit:style"],
            details.AuditorsScheduled);
    }

    [Fact]
    public async Task AuditFindingsEmitted_PassingIteration_HasEmptyFindings()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks,
            auditors: [new AlwaysPassAuditor("pass-auditor")]);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("audit-pass.txt", "x\n"));

        var item = MakeItem("feature/audit-pass");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var findingsEvt = webhooks.Events.Single(e => e.Event == "audit.findings.emitted");
        var details = Assert.IsType<AuditFindingsEmittedDetails>(findingsEvt.Details);
        Assert.Equal(1, details.Iteration);
        Assert.Empty(details.Findings);
        Assert.Equal(0, details.Blocking);
        Assert.Equal(0, details.NonBlocking);

        var completed = webhooks.Events.Single(e => e.Event == "audit.completed");
        var completedDetails = Assert.IsType<AuditCompletedDetails>(completed.Details);
        Assert.Equal(AuditVerdict.Pass, completedDetails.Verdict);
        Assert.Equal(1, completedDetails.Iteration);
        Assert.True(completedDetails.DurationMs >= 0);
    }

    [Fact]
    public async Task ReworkLifecycle_EmitsAuditFailThenReworkIteration()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks,
            auditors: [new OnceFailingAuditor("force-rework")],
            maxAuditIterations: 2);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework-1.txt", "work\n"));
        tp.Agent.WorkPlan.Enqueue(new FileWrite("rework-2.txt", "rework\n"));

        var item = MakeItem("feature/rework-events");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var auditCompletedEvents = webhooks.Events.Where(e => e.Event == "audit.completed").ToList();
        Assert.Equal(2, auditCompletedEvents.Count);

        var firstAudit = Assert.IsType<AuditCompletedDetails>(auditCompletedEvents[0].Details);
        Assert.Equal(AuditVerdict.Fail, firstAudit.Verdict);
        Assert.Equal(1, firstAudit.Iteration);

        var secondAudit = Assert.IsType<AuditCompletedDetails>(auditCompletedEvents[1].Details);
        Assert.Equal(AuditVerdict.Pass, secondAudit.Verdict);
        Assert.Equal(2, secondAudit.Iteration);

        var findingsEvents = webhooks.Events.Where(e => e.Event == "audit.findings.emitted").ToList();
        Assert.Equal(2, findingsEvents.Count);
        var firstFindings = Assert.IsType<AuditFindingsEmittedDetails>(findingsEvents[0].Details);
        Assert.Equal(1, firstFindings.Blocking);
        Assert.Single(firstFindings.Findings);
        var f = firstFindings.Findings[0];
        Assert.Equal("force-rework", f.Auditor);
        // Wire severity is the explicit string from PipelineRunner.ToWireSeverity,
        // not f.Severity.ToString() — the contract is decoupled from the enum.
        Assert.Equal("Error", f.Severity);
        Assert.Equal("force rework", f.Title);
        // Round-trip Description and Location so a Title↔Description swap or a
        // dropped field would be caught.
        Assert.Equal("iteration 1 always fails", f.Description);
        Assert.Equal("src/foo.cs:42", f.Location);

        // Rework is the iteration that follows the failing audit. Numbered as
        // iteration+1 (next attempt) — audit 1 fails, then rework iter 2 runs.
        var reworkStarted = webhooks.Events.FirstOrDefault(e =>
            e.Event == "iteration.started"
            && e.Details is IterationStartedDetails d
            && d.Phase == IterationPhase.Rework);
        Assert.NotNull(reworkStarted);
        var reworkStartedDetails = Assert.IsType<IterationStartedDetails>(reworkStarted!.Details);
        Assert.Equal(2, reworkStartedDetails.Iteration);

        var reworkCompleted = webhooks.Events.FirstOrDefault(e =>
            e.Event == "iteration.completed"
            && e.Details is IterationCompletedDetails d
            && d.Phase == IterationPhase.Rework);
        Assert.NotNull(reworkCompleted);
        var reworkCompletedDetails = Assert.IsType<IterationCompletedDetails>(reworkCompleted!.Details);
        Assert.Equal(2, reworkCompletedDetails.Iteration);

        // Ordering: rework starts only after audit.completed for iter=1 fail.
        var names = webhooks.Events.Select(e => e.Event).ToList();
        var firstAuditCompletedIdx = IndexOf(names, "audit.completed");
        var reworkStartedIdx = names.IndexOf("iteration.started",
            IndexOf(names, "iteration.completed") + 1);
        Assert.True(firstAuditCompletedIdx < reworkStartedIdx);
    }

    [Fact]
    public async Task MergeEvents_CarryMergeShaAndBaseBranch()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("merge-shape.txt", "x\n"));

        var item = MakeItem("feature/merge-shape");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var mergeStarted = webhooks.Events.Single(e => e.Event == "merge.started");
        var startedDetails = Assert.IsType<MergeStartedDetails>(mergeStarted.Details);
        Assert.Equal("main", startedDetails.BaseBranch);
        Assert.Equal("feature/merge-shape", startedDetails.WorkBranch);
        Assert.Equal(item.Id.ToString(), startedDetails.WorkItemId);

        var mergeCompleted = webhooks.Events.Single(e => e.Event == "merge.completed");
        var completedDetails = Assert.IsType<MergeCompletedDetails>(mergeCompleted.Details);
        Assert.Equal("main", completedDetails.BaseBranch);
        Assert.Equal("feature/merge-shape", completedDetails.WorkBranch);
        Assert.False(string.IsNullOrEmpty(completedDetails.MergeSha));
    }

    [Fact]
    public async Task ResumeFromPreemptRework_EmitsReworkIterationEvents()
    {
        // Exercises the resume-preempt-rework branch (PipelineRunner.cs:377-412):
        // a WorkItem pre-seeded in Reworking state with a PreemptCheckpoint must
        // still emit iteration.started/completed (Phase=Rework) when picked up.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks);

        // Non-default work-branch name keeps the pickup rebase out of the way
        // (IsPickupRebaseOwnedWorkBranch matches only "codeybox/{id8}").
        var workBranch = "feature/preempt-rework-events";
        var item = MakeItem(workBranch) with { State = WorkItemState.Reworking };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await tp.Store.CreateAsync(item);
        await PushPreemptCheckpointAsync(tp.GitHost, item, seed);

        // One file-write covers the resumed rework run; one for the real merge.
        tp.Agent.WorkPlan.Enqueue(new FileWrite("resumed-rework.txt", "resumed\n"));

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var reworkStarted = webhooks.Events.FirstOrDefault(e =>
            e.Event == "iteration.started"
            && e.Details is IterationStartedDetails d
            && d.Phase == IterationPhase.Rework);
        Assert.NotNull(reworkStarted);
        var startedDetails = Assert.IsType<IterationStartedDetails>(reworkStarted!.Details);
        Assert.Equal(item.Id.ToString(), startedDetails.WorkItemId);
        Assert.Equal(IterationPhase.Rework, startedDetails.Phase);
        // Resume re-bases the rework numbering to 1 — see docs/webhooks.md
        // "Resumed-after-preempt caveat".
        Assert.Equal(1, startedDetails.Iteration);

        var reworkCompleted = webhooks.Events.FirstOrDefault(e =>
            e.Event == "iteration.completed"
            && e.Details is IterationCompletedDetails d
            && d.Phase == IterationPhase.Rework);
        Assert.NotNull(reworkCompleted);
        var completedDetails = Assert.IsType<IterationCompletedDetails>(reworkCompleted!.Details);
        Assert.Equal(item.Id.ToString(), completedDetails.WorkItemId);
        Assert.Equal(IterationPhase.Rework, completedDetails.Phase);
        Assert.Equal(1, completedDetails.Iteration);

        // No work-phase iteration event must fire — the resume branch is taken
        // because work is skipped (entry == Reworking, PreemptCheckpoint set).
        Assert.DoesNotContain(webhooks.Events, e =>
            e.Event == "iteration.started"
            && e.Details is IterationStartedDetails d
            && d.Phase == IterationPhase.Work);

        // Ordering: started precedes completed; both precede merge.completed.
        var events = webhooks.Events.ToList();
        var names = events.Select(e => e.Event).ToList();
        var startedIdx = events.IndexOf(reworkStarted);
        var completedIdx = events.IndexOf(reworkCompleted);
        Assert.True(startedIdx < completedIdx);
        Assert.True(completedIdx < IndexOf(names, "merge.completed"));
    }

    [Fact]
    public async Task ResumeFromPreemptRework_EmitsReworkPhaseSpanAndDuration()
    {
        // Companion to PipelineRunnerQuotaFallbackTests.AuditDrivenRework_*: that
        // test covers the audit-loop rework scope; this one covers the distinct
        // resume-preempt branch (entry == Reworking && PreemptCheckpoint set),
        // which opens its own BeginPhaseScope(item, "rework"). A regression that
        // dropped or mis-tagged that scope would leave the resumed rework with no
        // phase span / duration sample and slip past the webhook-only assertions.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks);

        var workBranch = "feature/preempt-rework-telemetry";
        var item = MakeItem(workBranch) with { State = WorkItemState.Reworking };
        item = item with { PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}" };
        await tp.Store.CreateAsync(item);
        await PushPreemptCheckpointAsync(tp.GitHost, item, seed);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("resumed-rework.txt", "resumed\n"));

        using var spans = new SpanCapture("CodeyBox.Pipeline");
        using var metrics = new MetricCapture("codeybox.phase.duration_ms");

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var finalItem = await tp.Store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, finalItem!.State);

        Assert.True(spans.Any("phase.rework", ("codeybox.phase", "rework")),
            "expected a phase.rework span on the resume-preempt rework path");
        Assert.True(metrics.Any("codeybox.phase.duration_ms", ("phase", "rework")),
            "expected a codeybox.phase.duration_ms{phase=rework} measurement on resume-preempt rework");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task PushPreemptCheckpointAsync(LocalGitHost gitHost, WorkItem item, string seed)
    {
        // Mirrors HostShutdownCancellationTests.CreatePreemptCheckpointAsync —
        // seeds the host bare repo with a checkpoint ref at item.PreemptCheckpoint
        // so the resume path can `git fetch origin {checkpoint}` and check out
        // the work branch from it.
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed);
        var clone = Path.Combine(_workspace, "checkpoint-" + Guid.NewGuid().ToString("N")[..8]);
        var bare = gitHost.GetRepoPath(repoId);
        await TestSupport.RunGit(_workspace, "clone", bare, clone);
        await TestSupport.RunGit(clone, "config", "user.email", "test@example.invalid");
        await TestSupport.RunGit(clone, "config", "user.name", "Test");
        await TestSupport.RunGit(clone, "checkout", "-B", item.WorkBranch!);
        await File.WriteAllTextAsync(Path.Combine(clone, "partial-rework.txt"), "partial\n");
        await TestSupport.RunGit(clone, "add", "-A");
        await TestSupport.RunGit(clone, "commit", "-m", "checkpoint");
        await TestSupport.RunGit(clone, "push", "origin", $"HEAD:{item.PreemptCheckpoint}");
    }

    private static int IndexOf(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], name, StringComparison.Ordinal))
                return i;
        throw new Xunit.Sdk.XunitException($"event '{name}' was not emitted; observed: {string.Join(", ", names)}");
    }

    private static WorkItem MakeItem(string branch) => new()
    {
        Id = new WorkItemId(Guid.NewGuid()),
        ProjectId = new ProjectId("test-project"),
        Title = "Intermediate events test",
        Prompt = "write a file",
        State = WorkItemState.Queued,
        WorkBranch = branch,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        WorkTimeout = TimeSpan.FromMinutes(5),
        MergeTimeout = TimeSpan.FromMinutes(5),
    };

    private static TestPipeline BuildPipeline(
        string workspace,
        string seedRepoUrl,
        IWebhookDispatcher webhooks,
        IReadOnlyList<IAuditor>? auditors = null,
        int maxAuditIterations = 1)
    {
        var gitRoot = Path.Combine(workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new ScriptedAgent([MergeStrategy.RealMerge]);
        var registry = new AgentRegistry([agent]);

        var auditorList = auditors ?? [];
        var auditTypes = auditorList.Count > 0 ? new[] { "scripted" } : Array.Empty<string>();

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { MaxIterations = maxAuditIterations, AuditTypes = auditTypes },
        });

        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog(auditorList));
        var upstreamFactory = new TestUpstreamFactory();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new TestPipeline(pipeline, store, agent, gitHost, gitRoot);
    }

    private sealed class AlwaysPassAuditor : IAuditor
    {
        public AlwaysPassAuditor(string name) => Name = name;
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class OrderedPassAuditor : IAuditor
    {
        public OrderedPassAuditor(
            string name,
            AuditorRole role = AuditorRole.None,
            bool canShortCircuit = false)
        {
            Name = name;
            Role = role;
            CanShortCircuitOnBlockingFinding = canShortCircuit;
        }

        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public AuditorRole Role { get; }
        public bool CanShortCircuitOnBlockingFinding { get; }
        public BuildTestGateEvidence BuildTestGateEvidence => Role == AuditorRole.BuildTestGate
            ? BuildTestGateEvidence.BuildAndTest
            : BuildTestGateEvidence.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
            => Task.FromResult(new AuditResult(true, []));
    }

    private sealed class OnceFailingAuditor : IAuditor
    {
        private int _calls;
        public OnceFailingAuditor(string name) => Name = name;
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
        {
            _calls++;
            if (_calls == 1)
                return Task.FromResult(new AuditResult(false, [
                    new AuditFinding(Name, AuditSeverity.Error, "force rework", "iteration 1 always fails", "src/foo.cs:42"),
                ]));
            return Task.FromResult(new AuditResult(true, []));
        }
    }

    private sealed class MixedSeverityAuditor : IAuditor
    {
        public MixedSeverityAuditor(string name) => Name = name;
        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct)
            => Task.FromResult(new AuditResult(true, [
                new AuditFinding(Name, AuditSeverity.Error, "blocker", "blocking issue"),
                new AuditFinding(Name, AuditSeverity.Info, "nit", "non-blocking nit"),
            ]));
    }

    private sealed class ThrowingWebhookDispatcher : IWebhookDispatcher
    {
        public List<string> Captured { get; } = new();

        public Task PublishAsync(WebhookEvent evt, CancellationToken ct)
        {
            Captured.Add(evt.Event);
            // Mimic a real dispatcher that breaks during the intermediate phase.
            if (evt.Event.StartsWith("iteration.", StringComparison.Ordinal)
                || evt.Event.StartsWith("audit.", StringComparison.Ordinal)
                || evt.Event.StartsWith("merge.", StringComparison.Ordinal))
                throw new InvalidOperationException("dispatcher boom");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FindingsCounters_SplitBlockingAndNonBlocking_BySeverity()
    {
        // Catches the swap-the-counters or hard-coded-zero regression: with one
        // Error and one Info finding the counters must come out 1/1 and the
        // wire payload must include both severities.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new CapturingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks,
            auditors: [new MixedSeverityAuditor("mixed")]);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("mixed-findings.txt", "x\n"));

        var item = MakeItem("feature/mixed-findings");
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var findingsEvt = webhooks.Events.First(e => e.Event == "audit.findings.emitted");
        var details = Assert.IsType<AuditFindingsEmittedDetails>(findingsEvt.Details);
        Assert.Equal(1, details.Blocking);
        Assert.Equal(1, details.NonBlocking);
        Assert.Equal(2, details.Findings.Count);
        Assert.Contains(details.Findings, f => f.Severity == "Error");
        Assert.Contains(details.Findings, f => f.Severity == "Info");
    }

    [Fact]
    public async Task DispatcherThrows_PipelineStillCompletes()
    {
        // Pin the fire-and-forget contract: PipelineRunner.TryPublishEventAsync
        // must swallow dispatcher exceptions for non-cancellation paths. A
        // regression that removed the try/catch would surface as random
        // pipeline failures whenever the dispatcher misbehaves.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var webhooks = new ThrowingWebhookDispatcher();
        using var tp = BuildPipeline(_workspace, seed, webhooks);

        tp.Agent.WorkPlan.Enqueue(new FileWrite("throwing-dispatcher.txt", "x\n"));

        var item = MakeItem("feature/throwing-dispatcher");
        await tp.Store.CreateAsync(item);

        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var finalState = (await tp.Store.GetAsync(item.Id, CancellationToken.None))!.State;
        Assert.Equal(WorkItemState.Done, finalState);
        Assert.Contains("iteration.started", webhooks.Captured);
        Assert.Contains("merge.completed", webhooks.Captured);
    }
}

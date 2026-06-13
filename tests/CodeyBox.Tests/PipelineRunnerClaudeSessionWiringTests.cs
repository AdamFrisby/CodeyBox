using System.Collections.Concurrent;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the per-item dispatch gate and the non-session default path
/// invariants of the resumable Claude session worker wire-up (item 3).
///
/// <para>The full end-to-end work→audit→rework arc with session-id
/// continuity is covered by <see cref="ClaudeSessionLifecycleTests"/>
/// (lifecycle behaviour against a fake runner) and by the
/// <see cref="AuditPipelineIntegrationTests"/> default-path tests
/// (non-session items unchanged when the worker is registered with
/// <c>Enabled=false</c>). This file focuses on the dispatch decision
/// matrix and the no-regression guarantees around session-mode being OFF
/// by default.</para>
/// </summary>
[Collection("GlobalSerilog")]
public sealed class PipelineRunnerClaudeSessionWiringTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerClaudeSessionWiringTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-claude-session-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task DefaultPipeline_NoSessionWorker_BehavesExactlyAsBefore_AuditPassesItemReachesDone()
    {
        // Acceptance: "a non-session item behaves exactly as today". This
        // exercises the full work→audit→merge arc with the session worker
        // UNREGISTERED, so the pipeline takes the legacy fresh-sandbox path
        // and the item reaches Done normally. The other AuditPipeline tests
        // already cover this exhaustively; this test exists to pin the
        // invariant against future regressions of the session wiring.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = TestSupport.BuildPipeline(_workspace, seed);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task DefaultPipeline_SessionWorkerRegisteredButOptionsDisabled_StillUsesLegacyPath()
    {
        // Acceptance: even when the session worker is registered, items
        // remain on the legacy fresh-sandbox path unless the OPTIONS flag
        // is on. Registration alone must not change pipeline behaviour
        // (this is the "config-gated + opt-in" guarantee).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var worker = BuildClaudeSessionWorker();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            claudeSessionWorker: worker,
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = false });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task DefaultPipeline_OptionsEnabledButProjectNotOptedIn_StillUsesLegacyPath()
    {
        // The global flag alone isn't enough — per-project opt-in is the
        // second gate. A project that has not set ClaudeSession.Enabled=true
        // keeps the legacy path even when the global flag is on.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var worker = BuildClaudeSessionWorker();
        // Default Project from TestSupport.BuildPipeline does NOT set
        // ClaudeSession.Enabled, so the project flag stays false.
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            claudeSessionWorker: worker,
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true });
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
    }

    [Fact]
    public async Task RestartResumeFromWorkComplete_DegradesToOneShotPath_DoesNotStrand()
    {
        // Acceptance: "a session-mode item interrupted by an orchestrator
        // restart resumes its worker VM+session at the correct phase, or
        // degrades to the one-shot path for the remainder — never strands."
        //
        // The session lifecycle in production is only opened when
        // RunAsync sees a fresh-pickup item (entry state is Queued, so
        // both skipWork AND skipAudit are false). A restart-resumed item
        // picked up at WorkComplete/AuditPassed/Merged/etc. takes the
        // legacy independent-phase path because there is no live worker
        // VM/session left from the prior process to attach to. This test
        // pins that degrade contract by directly driving RunAsync on a
        // seeded WorkComplete item and asserting:
        //   (a) the session lifecycle is NEVER opened (no OpenSessionAsync
        //       call on the runner), so we don't try to attach to a
        //       phantom VM that no longer exists.
        //   (b) the item still advances (does not strand) — the audit +
        //       merge phases run via the legacy fresh-sandbox path and
        //       the item reaches a terminal state instead of remaining
        //       at WorkComplete.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed);

        // The session runner gets one turn-file for the seed run (so the
        // first item walks through work→audit→merge cleanly), and zero
        // for the resume run (so any unexpected SendTurnAsync would throw
        // for lack of a queued file write — the real assertion is that
        // SendTurnAsync is never called on the WorkComplete pickup).
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("seed.txt", "v1"),
        ]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner);

        // Seed: a fresh item walks the full session-mode arc so the
        // work-branch carries a real commit in the bare repo. This gives
        // the resume-from-WorkComplete pickup a real branch to operate
        // on without us having to handcraft git refs.
        var seedItem = NewItem();
        await tp.Store.CreateAsync(seedItem);
        await tp.Pipeline.RunAsync(seedItem, CancellationToken.None);
        Assert.Equal(WorkItemState.Done, (await tp.Store.GetAsync(seedItem.Id))!.State);
        var openedAfterSeed = sessionRunner.OpenedSessions;
        var sendsAfterSeed = sessionRunner.SendTurns;
        var closedAfterSeed = sessionRunner.CloseCalls;
        Assert.True(openedAfterSeed >= 1, "the seed run should have opened a session");

        // Now the WorkComplete-seeded item: same project (session-enabled)
        // but the prior process crashed mid-arc — work was committed and
        // the state stored as WorkComplete before the crash. The pickup
        // gate (`!skipWork && !skipAudit`) must short-circuit lifecycle
        // open, because the prior worker VM/session is gone and a brand-
        // new session against a brand-new VM would not have the prior
        // conversation context.
        var resumeItem = NewItem() with
        {
            State = WorkItemState.WorkComplete,
            WorkBranch = seedItem.WorkBranch,
        };
        await tp.Store.CreateAsync(resumeItem);
        await tp.Pipeline.RunAsync(resumeItem, CancellationToken.None);

        var final = await tp.Store.GetAsync(resumeItem.Id);
        Assert.NotNull(final);
        // No stranding: the item progressed past WorkComplete via the
        // legacy fresh-sandbox audit/merge path.
        Assert.NotEqual(WorkItemState.WorkComplete, final!.State);

        // Lifecycle was NEVER touched after the WorkComplete pickup:
        // neither OpenSessionAsync nor SendTurnAsync. Production opens
        // the lifecycle at the top of RunAsync under `!skipWork &&
        // !skipAudit`, so a WorkComplete pickup never reaches it.
        Assert.Equal(openedAfterSeed, sessionRunner.OpenedSessions);
        Assert.Equal(sendsAfterSeed, sessionRunner.SendTurns);
        // CloseCalls did NOT increment either: no lifecycle was opened
        // for the resume pickup, so there's no lifecycle to dispose.
        Assert.Equal(closedAfterSeed, sessionRunner.CloseCalls);
    }

    [Fact]
    public async Task SessionGate_RefusesWhenWorkAndReworkNetworkProfilesDiffer()
    {
        // Operators may configure distinct Work and Rework network
        // profiles as a containment boundary (e.g. broader egress during
        // initial work, restricted rework after auditor-controlled
        // findings are fed back). The session worker opens ONE VM with
        // the work-phase target and reuses it across every rework turn;
        // keeping the work-phase policy on rework would silently weaken
        // the operator's boundary. The gate must refuse session mode in
        // that configuration and fall back to the legacy per-phase path.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var divergentProject = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            ClaudeSession = new ProjectClaudeSessionConfig { Enabled = true },
            NetworkProfiles = new ProjectNetworkProfiles
            {
                Work = "open-egress",
                Rework = "restricted-egress",
            },
            Audit = new ProjectAudit(),
        };
        var worker = BuildClaudeSessionWorker();
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(divergentProject),
            claudeSessionWorker: worker,
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true });

        var claudeItem = NewItem() with { Agent = AgentKind.Claude };
        var runner = new StubAgentRunner(AgentKind.Claude);

        // Every other gate condition is satisfied (worker registered,
        // global flag on, project flag on, Claude agent, Normal job).
        // The mismatched profiles are the sole reason to refuse — pin
        // that here so a regression that drops the check would fail.
        Assert.False(tp.Pipeline.ShouldEnterClaudeSessionMode(claudeItem, divergentProject, runner));
    }

    [Fact]
    public async Task ShouldEnterClaudeSessionMode_DecisionMatrix()
    {
        // The dispatch gate composes:
        //   worker registered ∧ global Enabled ∧ project Enabled ∧ runner.Kind=Claude
        //   ∧ JobType ∉ {CheckAndAct, AgentControl}
        // This test enumerates the matrix so a future config addition can't
        // silently widen or narrow the gate.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var workerRegistered = BuildClaudeSessionWorker();
        var enabledOptions = new ClaudeSessionWorkerOptions { Enabled = true };
        var disabledOptions = new ClaudeSessionWorkerOptions { Enabled = false };

        // Project opted in.
        var optedInProject = ProjectWithSessionEnabled(seed);
        // Project not opted in (default).
        var optedOutProject = ProjectWithSessionEnabled(seed, enabled: false);

        var claudeItem = NewItem() with { Agent = AgentKind.Claude };
        var codexItem = NewItem() with { Agent = AgentKind.Codex };
        var checkItem = NewItem() with { Agent = AgentKind.Claude, JobType = JobType.CheckAndAct };
        var controlItem = NewItem() with { Agent = AgentKind.Claude, JobType = JobType.AgentControl };
        var preemptItem = NewItem() with
        {
            Agent = AgentKind.Claude,
            State = WorkItemState.Working,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{WorkItemId.New()}",
        };

        // Helper to spin up a PipelineRunner with the given knobs and ask it.
        bool Gate(ClaudeSessionWorker? worker, ClaudeSessionWorkerOptions options, Project project, WorkItem item, AgentKind runnerKind)
        {
            using var tp = TestSupport.BuildPipeline(
                _workspace,
                seed,
                projectRepository: new InMemoryProjectRepository(project),
                claudeSessionWorker: worker,
                claudeSessionOptions: options);
            var runner = new StubAgentRunner(runnerKind);
            return tp.Pipeline.ShouldEnterClaudeSessionMode(item, project, runner);
        }

        // Gate ON: every condition met.
        Assert.True(Gate(workerRegistered, enabledOptions, optedInProject, claudeItem, AgentKind.Claude));

        // Gate OFF when the worker isn't registered.
        Assert.False(Gate(null, enabledOptions, optedInProject, claudeItem, AgentKind.Claude));
        // Gate OFF when global options.Enabled is false.
        Assert.False(Gate(workerRegistered, disabledOptions, optedInProject, claudeItem, AgentKind.Claude));
        // Gate OFF when the project hasn't opted in.
        Assert.False(Gate(workerRegistered, enabledOptions, optedOutProject, claudeItem, AgentKind.Claude));
        // Gate OFF for non-Claude agents.
        Assert.False(Gate(workerRegistered, enabledOptions, optedInProject, codexItem, AgentKind.Codex));
        // Gate OFF for CheckAndAct items even when everything else is on.
        Assert.False(Gate(workerRegistered, enabledOptions, optedInProject, checkItem, AgentKind.Claude));
        // Gate OFF for AgentControl items even when everything else is on —
        // operator control-plane items (pause/resume) have no rework loop, so
        // the session-share benefit doesn't apply and dropping this bypass
        // would route them through the worker VM unnecessarily.
        Assert.False(Gate(workerRegistered, enabledOptions, optedInProject, controlItem, AgentKind.Claude));
        // Gate OFF for preempt recovery. Restarted Working/Reworking items with
        // a checkpoint intentionally degrade to the legacy one-shot resume path.
        Assert.False(Gate(workerRegistered, enabledOptions, optedInProject, preemptItem, AgentKind.Claude));
    }

    [Fact]
    public async Task ShouldEnterClaudeSessionMode_ClassRoutedItemsRequireClassOrMemberOptIn()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var worker = BuildClaudeSessionWorker();
        var options = new ClaudeSessionWorkerOptions { Enabled = true };
        var project = ProjectWithSessionEnabled(seed) with { DefaultAgentClass = "frontier" };
        var item = NewItem() with
        {
            Agent = AgentKind.Claude,
            AgentClassId = "frontier",
            ModelId = "claude-opus-4-7",
        };
        var runner = new StubAgentRunner(AgentKind.Claude);

        bool Gate(AgentClass agentClass)
        {
            var router = new AgentClassRouter(
                [agentClass],
                [],
                new QuotaRouterOptions(),
                NullLogger<AgentClassRouter>.Instance);
            using var tp = TestSupport.BuildPipeline(
                _workspace,
                seed,
                projectRepository: new InMemoryProjectRepository(project),
                claudeSessionWorker: worker,
                claudeSessionOptions: options,
                classRouter: router);
            return tp.Pipeline.ShouldEnterClaudeSessionMode(item, project, runner);
        }

        AgentClass Class(AgentClassClaudeSessionConfig? classSession, AgentClassClaudeSessionConfig? memberSession) => new()
        {
            Id = "frontier",
            DisplayName = "Frontier",
            ClaudeSession = classSession,
            Members =
            [
                new AgentMembership
                {
                    Agent = AgentKind.Claude,
                    Billing = AgentBilling.Subscription,
                    ModelId = "claude-opus-4-7",
                    QualityScore = 100,
                    ClaudeSession = memberSession,
                },
            ],
        };

        Assert.False(Gate(Class(classSession: null, memberSession: null)));
        Assert.True(Gate(Class(
            classSession: new AgentClassClaudeSessionConfig { Enabled = true },
            memberSession: null)));
        Assert.False(Gate(Class(
            classSession: new AgentClassClaudeSessionConfig { Enabled = true },
            memberSession: new AgentClassClaudeSessionConfig { Enabled = false })));
        Assert.True(Gate(Class(
            classSession: new AgentClassClaudeSessionConfig { Enabled = false },
            memberSession: new AgentClassClaudeSessionConfig { Enabled = true })));
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private static Project ProjectWithSessionEnabled(
        string repoUrl,
        bool enabled = true,
        bool withScriptedAuditors = false) => new()
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = repoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            ClaudeSession = new ProjectClaudeSessionConfig { Enabled = enabled },
            // When the test fixture registers scripted auditors, the project's
            // audit profile must reference the "scripted" audit type so the
            // ScriptedAuditorCatalog returns them. Tests without auditors leave
            // AuditTypes empty so the project doesn't claim a non-existent type.
            Audit = withScriptedAuditors
            ? new ProjectAudit { MaxIterations = 10, AuditTypes = ["scripted"] }
            : new ProjectAudit(),
        };

    private static ClaudeSessionWorker BuildClaudeSessionWorker()
    {
        var defaults = new AgentDefaultsSnapshot(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = "claude-opus-4-7",
        });
        var runner = new ClaudeAgentRunner(defaults);
        // Production wires sandboxResumeHook to multipass start; tests don't
        // care because the gate decision tests never actually invoke the
        // worker.
        return new ClaudeSessionWorker(runner);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "session wiring test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/x",
        PushUpstream = false,
        Agent = AgentKind.Claude,
    };

    private sealed class StubAgentRunner : CodeyBox.Core.IAgentRunner
    {
        public StubAgentRunner(AgentKind kind) { Kind = kind; }
        public AgentKind Kind { get; }
        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
        public AgentFailureClassification ClassifyFailure(AgentResult result)
            => new(AgentFailureKind.Normal);
    }

    [Fact]
    public async Task SessionMode_FullArc_WorkPlusReworkRunOnOneSession_AuditOnSeparate()
    {
        // Acceptance criterion (item 3): a session-enabled item completes a
        // full work → audit → rework → audit → AuditPassed arc where every
        // worker turn ran on ONE session (verified via session-id
        // continuity), the worker VM was STOPPED between turns (Suspend
        // called) and RESUMED for the next rework, and each audit ran in a
        // SEPARATE fresh sandbox.
        //
        // This is the regression test for the AsyncLocal-propagation bug
        // (assigning _ambientSessionLifecycle inside an awaited child method
        // does NOT flow back to the parent's ExecutionContext, so the
        // session-mode branch in RunAgentPhaseAsync was dead code despite
        // every gate being on).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);

        // Worker writes a file per turn so each worker turn produces a real
        // commit; the audit sees the change, fails the first iteration, then
        // passes the second. Two worker turns ⇒ work + rework on ONE session.
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
            new RecordingFileWrite("a.txt", "v2-after-rework"),
        ]);
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "needs fix", "x")]),
            new AuditOutcome(true, []),
        ]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Exactly ONE session was opened — the lifecycle is shared across
        // work + rework, not a fresh session per phase.
        Assert.Equal(1, sessionRunner.OpenedSessions);

        // Exactly TWO worker turns ran on it (work + one rework).
        Assert.Equal(2, sessionRunner.SendTurns);

        // ONE session id was reused across every worker turn — the brief's
        // "verified via session-id continuity" acceptance criterion.
        var handleIdsObserved = sessionRunner.HandleIdsObserved.ToArray();
        Assert.Equal(2, handleIdsObserved.Length);
        Assert.All(handleIdsObserved, id => Assert.Equal(sessionRunner.OpenedHandleId, id));

        // VM was SUSPENDED before each audit (after work and after rework)
        // and RESUMED exactly once for the rework turn.
        Assert.Equal(2, sessionRunner.SuspendCalls);
        Assert.Equal(1, sessionRunner.ResumeCalls);

        var prompts = sessionRunner.PromptsSent.ToArray();
        Assert.Equal(2, prompts.Length);
        Assert.All(prompts, prompt =>
            Assert.Contains("CodeyBox-Prompt-Revision` trailer value for this turn MUST be the literal integer **1**", prompt));

        // CloseSessionAsync runs at the end (lifecycle disposal): the
        // worker VM must be disposed on terminal so no idle VM leaks.
        Assert.Equal(1, sessionRunner.CloseCalls);

        // Auditor isolation: the audit never reached the session runner.
        // The audit ran in CollectFindingsAsync's fresh sandbox path with
        // the registered work runner (ScriptedAgent) — sessionRunner.RunAsync
        // (the one-shot path) is never invoked, and the SendTurnAsync count
        // matches the worker turns only.
        Assert.Equal(0, sessionRunner.OneShotRunAsyncCalls);
        Assert.Equal(2, sessionRunner.SendTurns); // unchanged by the audit phase
    }

    [Fact]
    public async Task SessionMode_AuditorNeverSharesWorkerSessionOrVm()
    {
        // The brief calls auditor isolation non-negotiable. Even when the
        // worker's session is live across work+rework, the auditor must run
        // on a separate sandbox / never receive the worker's session handle.
        // A future refactor that accidentally threaded the ambient lifecycle
        // into the audit path would let an auditor share the worker's
        // session and rubber-stamp its own work; this test fails loudly the
        // moment that happens.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);

        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ]);

        // The auditor records which sandbox it was given so we can assert
        // it isn't the worker's sandbox (the session lifecycle's sandbox is
        // captured on the first SendTurnAsync call).
        var auditor = new SandboxRecordingAuditor([new AuditOutcome(true, [])]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // The worker captured at least one sandbox id (from the work turn).
        Assert.NotEmpty(sessionRunner.SandboxIdsObservedOnTurns);
        var workerSandboxIds = sessionRunner.SandboxIdsObservedOnTurns.ToHashSet(StringComparer.Ordinal);

        // The auditor saw at least one sandbox.
        Assert.NotEmpty(auditor.SandboxIdsObserved);

        // None of the worker sandbox ids match the auditor sandbox ids: the
        // worker and auditor ran on SEPARATE VMs.
        foreach (var auditorSandboxId in auditor.SandboxIdsObserved)
            Assert.False(workerSandboxIds.Contains(auditorSandboxId),
                $"auditor reused the worker sandbox '{auditorSandboxId}' — session leakage!");
    }

    [Fact]
    public async Task SessionMode_ClassRoutedOpenPassesProjectAndMemberScope()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed) with { DefaultAgentClass = "frontier" };
        var classRouter = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "frontier",
                    DisplayName = "Frontier",
                    ClaudeSession = new AgentClassClaudeSessionConfig { Enabled = true },
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Claude,
                            Billing = AgentBilling.Subscription,
                            ModelId = "claude-opus-4-7",
                            QualityScore = 100,
                        },
                    ],
                },
            ],
            [],
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner,
            classRouter: classRouter);

        var item = NewItem() with
        {
            AgentClassId = "frontier",
            ModelId = "claude-opus-4-7",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal("test-project", sessionRunner.OpenedProjectId);
        Assert.Equal("claude", sessionRunner.OpenedAgentClassMember);
        Assert.Equal("claude-opus-4-7", sessionRunner.OpenedModelId);
    }

    [Fact]
    public async Task SessionMode_SuspendFailureClosesSession_AndReworkFallsBackToLegacySandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ])
        {
            FailNextSuspend = true,
        };
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "needs fix", "x")]),
            new AuditOutcome(true, []),
        ]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-legacy-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, sessionRunner.SendTurns);
        Assert.Equal(1, sessionRunner.SuspendCalls);
        Assert.Equal(1, sessionRunner.CloseCalls);
        Assert.Equal(0, sessionRunner.ResumeCalls);
    }

    [Fact]
    public async Task SessionMode_ResumeDegradeFallback_ContinuesReworkInLegacySandbox()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed, withScriptedAuditors: true);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ])
        {
            MarkFallbackOnResume = true,
        };
        var auditor = new ScriptedAuditor(
        [
            new AuditOutcome(false, [new AuditFinding("Lint", AuditSeverity.Error, "needs fix", "x")]),
            new AuditOutcome(true, []),
        ]);

        AgentSessionHandle Snapshot(AgentSessionHandle handle)
        {
            if (!sessionRunner.FallbackMarked)
                return handle;

            var metadata = handle.Metadata is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(handle.Metadata, StringComparer.Ordinal);
            metadata[AgentSessionMetadataKeys.FallbackToOneShot] = "true";
            return handle with { Metadata = metadata };
        }

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            auditors: [auditor],
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner,
            sessionHandleSnapshotOverride: Snapshot);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v2-legacy-rework"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, sessionRunner.SendTurns);
        Assert.Equal(1, sessionRunner.SuspendCalls);
        Assert.Equal(1, sessionRunner.ResumeCalls);
        Assert.Equal(1, sessionRunner.CloseCalls);
        Assert.Empty(tp.Agent.WorkPlan);
    }

    [Fact]
    public async Task SessionMode_QuotaFailureClosesSessionBeforeSameKindFallback()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed) with { DefaultAgentClass = "frontier" };
        var classRouter = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "frontier",
                    DisplayName = "Frontier",
                    ClaudeSession = new AgentClassClaudeSessionConfig { Enabled = true },
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Claude,
                            Billing = AgentBilling.Subscription,
                            ModelId = "primary-model",
                            QualityScore = 100,
                        },
                        new AgentMembership
                        {
                            Agent = AgentKind.Claude,
                            Billing = AgentBilling.PayPerApi,
                            ModelId = "fallback-model",
                            QualityScore = 99,
                        },
                    ],
                },
            ],
            [],
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance);
        var sessionRunner = new RecordingSessionRunner(turnFiles: []);
        sessionRunner.EnqueueTurnResult(new AgentResult(
            Success: false,
            Summary: "quota",
            Stdout: null,
            Stderr: "rate_limit_exceeded"));

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner,
            classRouter: classRouter);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "fallback wrote this"));

        var item = NewItem() with
        {
            AgentClassId = "frontier",
            ModelId = "primary-model",
        };
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);
        Assert.Equal(1, sessionRunner.SendTurns);
        Assert.Equal(1, sessionRunner.CloseCalls);
        Assert.Equal(0, sessionRunner.SuspendCalls);
        Assert.Equal(0, sessionRunner.ResumeCalls);
    }

    [Fact]
    public async Task SessionMode_TerminalDisposal_ClosesSessionAndDisposesWorkerVm()
    {
        // Acceptance: worker VM is disposed on terminal/cancel. The lifecycle
        // owns the worker VM; when the pipeline reaches a terminal state
        // (Done / Failed / Cancelled), RunAsync's outer finally must run
        // DisposeAsync which calls the underlying CloseSessionAsync and tears
        // down the VM.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed);

        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ]);

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Outer-finally disposal ran exactly once: CloseSessionAsync fires
        // on the way out of RunAsync regardless of how the pipeline exits.
        Assert.Equal(1, sessionRunner.CloseCalls);
    }

    [Fact]
    public async Task SessionMode_TerminalCleanupFailure_MarksItemFailedAndRethrows()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed);
        var sessionRunner = new RecordingSessionRunner(turnFiles:
        [
            new RecordingFileWrite("a.txt", "v1"),
        ])
        {
            CloseFailuresRemaining = 1,
        };

        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner);

        var item = NewItem();
        await tp.Store.CreateAsync(item);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tp.Pipeline.RunAsync(item, CancellationToken.None));

        Assert.Contains("close failed", ex.Message, StringComparison.Ordinal);
        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("Claude session terminal cleanup failed", final.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionMode_OptedOutProject_DoesNotOpenSession()
    {
        // The non-session default path must be unchanged when a project does
        // not opt in. A regression that flipped the gate's polarity (or
        // dropped the project flag from the gate) would silently route every
        // item through the session worker.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var sessionRunner = new RecordingSessionRunner(turnFiles: []);

        // Default Project from TestSupport.BuildPipeline doesn't set
        // ClaudeSession.Enabled, so the project flag stays false.
        using var tp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner);
        tp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, final!.State);

        // Session runner was never engaged — the legacy independent-phase
        // path drove the ScriptedAgent directly.
        Assert.Equal(0, sessionRunner.OpenedSessions);
        Assert.Equal(0, sessionRunner.SendTurns);
        Assert.Equal(0, sessionRunner.CloseCalls);
    }

    // ─── test doubles ─────────────────────────────────────────────────────

    private sealed record RecordingFileWrite(string FileName, string Contents);

    /// <summary>
    /// In-memory <see cref="ISessionAgentRunner"/> that records every
    /// open / send-turn / suspend / resume / close call. On each
    /// SendTurnAsync, it writes the next configured file to the supplied
    /// sandbox so the pipeline observes a real commit. The same handle id
    /// is reused for every turn — tests assert session-id continuity by
    /// inspecting <see cref="HandleIdsObserved"/>.
    /// </summary>
    private sealed class RecordingSessionRunner : IScopedSessionAgentRunner
    {
        private readonly Queue<RecordingFileWrite> _turnFiles;
        private readonly Queue<AgentResult> _turnResults = new();
        private ISandbox? _capturedSandbox;
        private string? _workingDirectory;

        public RecordingSessionRunner(IEnumerable<RecordingFileWrite> turnFiles)
        {
            _turnFiles = new Queue<RecordingFileWrite>(turnFiles);
        }

        public AgentKind Kind => AgentKind.Claude;
        public int OpenedSessions;
        public int SendTurns;
        public int SuspendCalls;
        public int ResumeCalls;
        public int CloseCalls;
        public int OneShotRunAsyncCalls;
        public string? OpenedHandleId;
        public string? OpenedProjectId;
        public string? OpenedAgentClassMember;
        public string? OpenedModelId;
        public string? OpenedReasoningMode;
        public bool FailNextSuspend { get; set; }
        public int CloseFailuresRemaining { get; set; }
        public bool MarkFallbackOnResume { get; set; }
        public bool FallbackMarked { get; private set; }
        public ConcurrentQueue<string> HandleIdsObserved { get; } = new();
        public ConcurrentQueue<string> SandboxIdsObservedOnTurns { get; } = new();
        public ConcurrentQueue<string> PromptsSent { get; } = new();

        public void EnqueueTurnResult(AgentResult result) => _turnResults.Enqueue(result);

        public Task<AgentResult> RunAsync(
            ISandbox sandbox, string workingDirectory, string prompt, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            // The one-shot path; the brief routes session-mode items through
            // SendTurnAsync. Auditors that fall through to a non-session
            // runner could land here — recorded so the worker/auditor
            // separation test can assert the count stays zero.
            Interlocked.Increment(ref OneShotRunAsyncCalls);
            return Task.FromResult(new AgentResult(true, "ok", null, null));
        }

        public AgentFailureClassification ClassifyFailure(AgentResult result)
            => new(AgentFailureKind.Normal);

        public Task<AgentSessionHandle> OpenSessionAsync(
            ISandbox sandbox, string workingDirectory, AgentCredential? credential,
            string? modelId = null, string? reasoningMode = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref OpenedSessions);
            _capturedSandbox = sandbox;
            _workingDirectory = workingDirectory;
            OpenedModelId = modelId;
            OpenedReasoningMode = reasoningMode;
            var handleId = $"claude-session-test-{OpenedSessions}";
            OpenedHandleId = handleId;
            return Task.FromResult(new AgentSessionHandle(
                Kind,
                handleId,
                new AgentSessionSandboxRef(sandbox.Id),
                workingDirectory,
                modelId,
                reasoningMode));
        }

        public Task<AgentSessionHandle> OpenSessionAsync(AgentSessionOpenRequest request, CancellationToken ct = default)
        {
            OpenedProjectId = request.ProjectId;
            OpenedAgentClassMember = request.AgentClassMember;
            return OpenSessionAsync(
                request.Sandbox,
                request.WorkingDirectory,
                request.Credential,
                request.ModelId,
                request.ReasoningMode,
                ct);
        }

        public async Task<AgentResult> SendTurnAsync(
            AgentSessionHandle sessionHandle, string prompt,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            Interlocked.Increment(ref SendTurns);
            HandleIdsObserved.Enqueue(sessionHandle.SessionId);
            PromptsSent.Enqueue(prompt);
            if (_capturedSandbox is not null)
                SandboxIdsObservedOnTurns.Enqueue(_capturedSandbox.Id);

            if (_turnResults.Count > 0)
                return _turnResults.Dequeue();

            if (_turnFiles.Count == 0)
                return new AgentResult(true, "ok", null, null);

            var file = _turnFiles.Dequeue();
            var path = $"{_workingDirectory}/{file.FileName}";
            var result = await _capturedSandbox!.ExecAsync(new SandboxExec
            {
                Argv = ["sh", "-c", "cat > \"$0\"", path],
                Stdin = file.Contents,
            }, ct);
            return result.Success
                ? new AgentResult(true, "ok", null, null)
                : new AgentResult(false, "fail", result.Stdout, result.Stderr);
        }

        public Task SuspendSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SuspendCalls);
            if (FailNextSuspend)
            {
                FailNextSuspend = false;
                throw new InvalidOperationException("suspend failed");
            }
            return Task.CompletedTask;
        }

        public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ResumeCalls);
            if (MarkFallbackOnResume)
                FallbackMarked = true;
            return Task.CompletedTask;
        }

        public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CloseCalls);
            if (CloseFailuresRemaining > 0)
            {
                CloseFailuresRemaining--;
                throw new InvalidOperationException("close failed");
            }
            if (_capturedSandbox is not null)
                await _capturedSandbox.DisposeAsync();
        }
    }

    /// <summary>
    /// Auditor that records the sandbox id it saw on each audit invocation
    /// so worker/auditor session+VM separation can be asserted.
    /// </summary>
    private sealed class SandboxRecordingAuditor : IAuditor
    {
        private readonly Queue<AuditOutcome> _outcomes;

        public SandboxRecordingAuditor(IEnumerable<AuditOutcome> outcomes)
        {
            _outcomes = new Queue<AuditOutcome>(outcomes);
            Name = "SandboxRecording";
        }

        public string Name { get; }
        public string Kind => "tool";
        public AuditCapabilities Required => AuditCapabilities.None;
        public ConcurrentQueue<string> SandboxIdsObserved { get; } = new();

        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            SandboxIdsObserved.Enqueue(sandbox.Id);
            var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : new AuditOutcome(true, []);
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
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
        public Task<AuditResult> RunAsync(ISandbox sandbox, string workingDirectory, AuditContext context, CancellationToken ct = default)
        {
            if (_plan.Count == 0) throw new InvalidOperationException("no plan entries left");
            var outcome = _plan.Dequeue();
            return Task.FromResult(new AuditResult(outcome.Passed, outcome.Findings));
        }
    }
}

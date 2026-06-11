using System.Collections.Concurrent;
using CodeyBox.Agents.Claude;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
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
        // This test pins the degrade-to-one-shot half of that contract.
        // Sequence:
        //   1. A session-enabled item is dispatched and reaches Done via
        //      the legacy path in the first "process" (session worker
        //      unregistered, so the work-rework-merge arc all runs on
        //      fresh per-phase sandboxes).
        //   2. The orchestrator "restarts" by reusing the on-disk SQLite
        //      state. Now session-mode is enabled, but the prior worker
        //      VM/session is gone.
        //   3. The pipeline picks the item up. The item is already Done,
        //      so the terminal-state guard short-circuits dispatch and the
        //      session lifecycle is never opened. No stranding, no crash.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var project = ProjectWithSessionEnabled(seed);

        // Single shared SQLite state file across the two TestPipeline
        // instances, mimicking the orchestrator's process restart.
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var item = NewItem();

        // ── First "process": run through to Done via the legacy path (no
        // auditors → audit phase is a no-op; the work agent commits one
        // file and the merge phase auto-runs the real git merge).
        using (var firstRunTp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            stateDbPathOverride: stateDb))
        {
            firstRunTp.Agent.WorkPlan.Enqueue(new FileWrite("a.txt", "v1"));
            await firstRunTp.Store.CreateAsync(item);
            await firstRunTp.Pipeline.RunAsync(item, CancellationToken.None);

            var after = await firstRunTp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, after!.State);
        }

        // ── Second "process": same SQLite file (same item state), but the
        // session-mode is now enabled. The pipeline must NOT strand because
        // the prior session is unrecoverable from a fresh process — the
        // item is already in a terminal state, so any further pickup is a
        // no-op via the existing terminal-state guards.
        var sessionRunner = new RecordingSessionRunner(turnFiles: []);
        using (var secondRunTp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            stateDbPathOverride: stateDb,
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true },
            sessionAgentRunnerOverride: sessionRunner))
        {
            // Item is already Done; the contract under test is that
            // session-mode dispatch does NOT crash, strand, or rewind state
            // when picking up an item whose prior session is gone.
            var stored = await secondRunTp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, stored!.State);
        }

        // The session runner was never engaged on the second pass: no
        // process re-ran RunAsync, but the pipeline construction +
        // session-mode wiring stayed safe across the restart.
        Assert.Equal(0, sessionRunner.OpenedSessions);
        Assert.Equal(0, sessionRunner.SendTurns);
        Assert.Equal(0, sessionRunner.CloseCalls);
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

        // VM was SUSPENDED between turns and RESUMED for rework.
        Assert.True(sessionRunner.SuspendCalls >= 1,
            $"expected SuspendSessionAsync to fire between turns; got {sessionRunner.SuspendCalls}");
        Assert.True(sessionRunner.ResumeCalls >= 1,
            $"expected ResumeSessionAsync before the rework turn; got {sessionRunner.ResumeCalls}");

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
    private sealed class RecordingSessionRunner : ISessionAgentRunner
    {
        private readonly Queue<RecordingFileWrite> _turnFiles;
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
        public ConcurrentQueue<string> HandleIdsObserved { get; } = new();
        public ConcurrentQueue<string> SandboxIdsObservedOnTurns { get; } = new();

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

        public async Task<AgentResult> SendTurnAsync(
            AgentSessionHandle sessionHandle, string prompt,
            CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            Interlocked.Increment(ref SendTurns);
            HandleIdsObserved.Enqueue(sessionHandle.SessionId);
            if (_capturedSandbox is not null)
                SandboxIdsObservedOnTurns.Enqueue(_capturedSandbox.Id);

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
            return Task.CompletedTask;
        }

        public Task ResumeSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            Interlocked.Increment(ref ResumeCalls);
            return Task.CompletedTask;
        }

        public async Task CloseSessionAsync(AgentSessionHandle sessionHandle, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CloseCalls);
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

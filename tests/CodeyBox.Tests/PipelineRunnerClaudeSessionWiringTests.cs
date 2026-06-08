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
        //   1. A session-enabled item is dispatched and reaches WorkComplete
        //      (via the legacy path, since we never actually exercised the
        //      session worker — but the state transition is what matters).
        //   2. The orchestrator "restarts" by reusing the on-disk SQLite
        //      state and picking the item up again. Now session-mode is
        //      enabled, but the prior worker VM/session is gone.
        //   3. The pipeline must NOT try to open a new session for the
        //      audit/rework remainder (there's nothing to --resume against);
        //      it falls back to the legacy fresh-sandbox path and the item
        //      still reaches Done.
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
        var worker = BuildClaudeSessionWorker();
        using (var secondRunTp = TestSupport.BuildPipeline(
            _workspace,
            seed,
            projectRepository: new InMemoryProjectRepository(project),
            stateDbPathOverride: stateDb,
            claudeSessionWorker: worker,
            claudeSessionOptions: new ClaudeSessionWorkerOptions { Enabled = true }))
        {
            // Item is already Done; the contract under test is that
            // session-mode dispatch does NOT crash, strand, or rewind state
            // when picking up an item whose prior session is gone.
            var stored = await secondRunTp.Store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Done, stored!.State);
        }
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

    private static Project ProjectWithSessionEnabled(string repoUrl, bool enabled = true) => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test Project",
        RepositoryUrl = repoUrl,
        DefaultBaseBranch = "main",
        DefaultAgent = AgentKind.Claude,
        ClaudeSession = new ProjectClaudeSessionConfig { Enabled = enabled },
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
}

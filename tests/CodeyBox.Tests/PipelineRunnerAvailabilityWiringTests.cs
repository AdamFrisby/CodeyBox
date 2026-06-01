using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Cursor;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Pipeline-level wiring tests for the fast-fail circuit breaker. The unit
/// tests in <see cref="AgentAvailabilityRegistryTests"/> only cover the
/// registry math; they do NOT exercise the two PipelineRunner call sites that
/// actually feed the registry (work-phase finish at PipelineRunner.cs:1542
/// and merge-phase finish at PipelineRunner.cs:3385). The cb-216a2230 bug
/// report's acceptance criterion 4 explicitly asks for an end-to-end test
/// ("stub a runner that succeeds on smoke but fails on every real call. After
/// 3 fast-fails the agent is excluded"), so this file dispatches work items
/// through <see cref="PipelineRunner.RunAsync"/> with a real registry wired
/// in. A regression that drops either RecordRunOutcome call site (or wires
/// the wrong stopwatch into the duration argument) would silently bring the
/// cascade back; these tests are the trap for that.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PipelineRunnerAvailabilityWiringTests : IDisposable
{
    private readonly string _workspace;

    public PipelineRunnerAvailabilityWiringTests() =>
        _workspace = Directory.CreateTempSubdirectory("codeybox-availwiring-").FullName;

    public void Dispose() { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    [Fact]
    public async Task ThreeConsecutiveFastFailWorkRuns_ExcludeAgent_AndPublishOneTransitionWebhook()
    {
        // Stand up an availability registry with the production defaults and
        // wire it into the pipeline. The work item runs through and the agent
        // returns an exit-127-shaped failure (non-quota, sub-threshold). After
        // 3 separate work items the circuit breaker must have flipped — and
        // exactly one `agent.smoke_failed` webhook event must have been
        // published, on the transition.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        // Three items, each yields a fast-fail (sub-10s, non-quota stderr so
        // the quota classifier does NOT swap agents) on Codex.
        for (var i = 0; i < 3; i++)
        {
            fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
                Success: false,
                Summary: "agent exited 127",
                Stdout: null,
                Stderr: "env: 'agent': No such file or directory"));
        }

        for (var i = 0; i < 3; i++)
        {
            var item = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(item);
            await fix.Pipeline.RunAsync(item, CancellationToken.None);
            // Each item terminates in Failed (non-quota failure on the single
            // configured agent — useClassRouter=false so there is no fallback
            // target to consume the slot).
            var final = await fix.Store.GetAsync(item.Id, CancellationToken.None);
            Assert.Equal(WorkItemState.Failed, final!.State);
        }

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("fast-fail circuit breaker", availability.Reason);

        // Exactly one transition webhook — steady-state failures past the
        // initial transition must NOT re-publish.
        var transitionEvents = fix.Webhooks.Events
            .Where(e => e.Event == "agent.smoke_failed")
            .ToList();
        Assert.Single(transitionEvents);
        var details = Assert.IsType<AgentSmokeFailedDetails>(transitionEvents[0].Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Contains("fast-fail circuit breaker", details.Reason);
        // The work-phase fast-fail call site hard-codes Category=Persistent
        // (PipelineRunner.cs ~2185): the binary launched, exited non-zero fast,
        // and did so repeatedly — retrying without operator intervention will
        // keep failing. A regression that forgot to set Category, or copied
        // the wrong constant, would silently default back to Unknown and the
        // operator-alert routing on persistent failures would not fire.
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
    }

    [Fact]
    public async Task SingleFastFail_DoesNotExclude_AndPublishesNoTransitionWebhook()
    {
        // Inverse-contrast: one fast-fail must NOT exclude the agent (the
        // breaker threshold is 3). Without this guard a regression that
        // shortens the threshold would silently page the operator on every
        // transient agent error.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "agent exited 127",
            Stdout: null,
            Stderr: "env: 'agent': No such file or directory"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
        Assert.DoesNotContain(fix.Webhooks.Events, e => e.Event == "agent.smoke_failed");

        // The registry must still have RECORDED the fast-fail (counter=1) —
        // we infer that via the snapshot, since a regression that disabled
        // RecordRunOutcome entirely would leave the counter at 0 and the
        // breaker would never trip on item 3.
        var snap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(1, snap.ConsecutiveFastFails);
        Assert.NotNull(snap.LastFastFailAt);
    }

    [Fact]
    public async Task SuccessfulWorkRun_ResetsFastFailCounterFromPipeline()
    {
        // Pin the contract that a SUCCESSFUL run also feeds the registry — a
        // future change that conditionally skipped RecordRunOutcome on success
        // would silently let slow-failure → fast-fail → fast-fail → fast-fail
        // exclude an agent that was actually recovering.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed);

        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(false, "exit 127", null,
            "env: 'agent': No such file or directory"));
        fix.Codex.ScriptedFailures.Enqueue(new AgentResult(false, "exit 127", null,
            "env: 'agent': No such file or directory"));
        fix.Codex.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));

        // Two items fail fast, third succeeds end-to-end.
        for (var i = 0; i < 2; i++)
        {
            var failing = NewItem(AgentKind.Codex);
            await fix.Store.CreateAsync(failing);
            await fix.Pipeline.RunAsync(failing, CancellationToken.None);
        }
        var succeeding = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(succeeding);
        await fix.Pipeline.RunAsync(succeeding, CancellationToken.None);

        // After the successful run the counter must be 0 — proving the
        // success-side wiring fires.
        var snap = fix.Registry.Snapshot().Single(s => s.Agent == AgentKind.Codex);
        Assert.Equal(0, snap.ConsecutiveFastFails);
        Assert.True(fix.Registry.GetAvailability(AgentKind.Codex).Available);
    }

    [Fact]
    public async Task MergePhaseFastFail_ExcludesAgent_AndPublishesWebhookWithMergeContext()
    {
        // Pin the second RecordRunOutcome call site in PipelineRunner —
        // RunAgentMergePhaseAsync, which fires *after* the work phase has
        // already reset the fast-fail counter to 0 on a successful work-phase
        // exit. A regression that deletes the merge-phase block (or wires it
        // to the wrong stopwatch/event name) would leave the counter at 0
        // because the work-phase reset masks the merge-phase fast-fail.
        //
        // Build a fixture with MaxConsecutiveFastFails=1 so a single merge-
        // phase fast-fail trips the breaker. The work phase succeeds (counter
        // ← 0); then the merge phase returns a non-quota, sub-threshold
        // failure (counter ← 1 = threshold, exclusion + webhook fire).
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var fix = BuildPipeline(seed, maxConsecutiveFastFails: 1);

        fix.Codex.WorkPlan.Enqueue(new FileWrite("ok.txt", "v1"));
        fix.Codex.MergeScriptedFailures.Enqueue(new AgentResult(
            Success: false,
            Summary: "merge agent exited 127",
            Stdout: null,
            Stderr: "env: 'agent': No such file or directory"));

        var item = NewItem(AgentKind.Codex);
        await fix.Store.CreateAsync(item);
        await fix.Pipeline.RunAsync(item, CancellationToken.None);

        var availability = fix.Registry.GetAvailability(AgentKind.Codex);
        Assert.False(availability.Available);
        Assert.Contains("fast-fail circuit breaker", availability.Reason);

        // The merge-phase block attaches both WorkItem and Project to the
        // event (the work-phase block does the same). Verify presence — a
        // regression that switched the call site to a payload-less variant
        // would silently drop these.
        var transitions = fix.Webhooks.Events
            .Where(e => e.Event == "agent.smoke_failed")
            .ToList();
        var transition = Assert.Single(transitions);
        var details = Assert.IsType<AgentSmokeFailedDetails>(transition.Details);
        Assert.Equal("codex", details.AgentKind);
        Assert.Contains("fast-fail circuit breaker", details.Reason);
        // Same Category=Persistent pin as the work-phase site above. The two
        // call sites (PipelineRunner.cs ~2185 work-phase, ~5105 merge-phase)
        // each hard-code the constant; drift between them or a copy-paste
        // omission would let only one path raise the persistent alert.
        Assert.Equal(SmokeFailureCategory.Persistent, details.Category);
        Assert.NotNull(transition.WorkItem);
        Assert.Equal(item.Id, transition.WorkItem.Id);
        Assert.NotNull(transition.Project);
        Assert.Equal("test-project", transition.Project.Id.Value);
    }

    [Fact]
    public async Task DirectAgentPickup_InVmGate_Exit127_FailsItemBeforeRunner_AndPublishesWebhook()
    {
        // The tests:meaningfulness-review Error: wire a REAL InVmSmokeProber into
        // PipelineRunner and prove the work-item gate (PipelineRunner.cs ~331)
        // actually short-circuits a direct-agent pickup (no AgentClass) when the
        // agent CLI exits 127 on `agent --version` inside the sandbox. A
        // regression that dropped this block, tied it back to
        // SkipCredentialSmokeTest, or wired it incorrectly would let the runner
        // be invoked and the exit-127 cascade reach first dispatch.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);

        var cursorAgent = new ScriptableAgent(AgentKind.Cursor);
        var registry = new AgentRegistry([cursorAgent]);

        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);

        // Scripted sandbox the prober clones: `agent --version` returns 127
        // (binary missing from PATH), everything else passes.
        var probeProvider = new ScriptedSandboxProvider(exec =>
            exec.Argv.Count >= 2 && exec.Argv[1] == "--version"
                ? new SandboxExecResult(127, "", "bash: agent: command not found")
                : new SandboxExecResult(0, "", ""));
        var cursorCred = new AgentCredential(
            AgentKind.Cursor,
            new Dictionary<string, string> { ["CODEYBOX_CURSOR_AUTH_JSON"] = "{\"token\":\"t\"}" },
            new Dictionary<string, string>());
        var prober = new InVmSmokeProber(
            probeProvider,
            new StubBaselineResolver("base-A"),
            new ConstantCredentialProvider(cursorCred),
            [new CursorInVmSmokeProbe()],
            availability,
            new InVmSmokeCache(TimeSpan.FromMinutes(60)),
            new NullWebhookDispatcher(),
            new InVmSmokeOptions { Enabled = true, ImageReference = "img", NetworkProfile = "work-profile", SweepIntervalSeconds = 0 },
            NullLogger<InVmSmokeProber>.Instance);

        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        using var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Cursor,
            // No DefaultAgentClass — this is the direct-agent path the gate must
            // still cover. SkipCredentialSmokeTest stays false; even if it were
            // true the in-VM gate is now decoupled from it.
            Audit = new ProjectAudit { MaxIterations = 1, AuditTypes = [] },
        };
        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            availability: availability,
            inVmSmokeGate: prober);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "in-vm gate",
            Prompt = "do thing",
            BaseBranch = "main",
            Agent = AgentKind.Cursor,
            PushUpstream = false,
        };
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        // The gate fired: the item failed without ever invoking the agent runner.
        var final = await store.GetAsync(item.Id, CancellationToken.None);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("in-VM smoke gate", final.LastError);
        Assert.Equal(0, cursorAgent.CallCount);

        // The prober benched cursor on the exit-127 version step.
        Assert.False(availability.GetAvailability(AgentKind.Cursor).Available);

        // PipelineRunner published the agent.smoke_failed transition for the item.
        var failed = Assert.Single(webhooks.Events, e => e.Event == "agent.smoke_failed");
        var details = Assert.IsType<AgentSmokeFailedDetails>(failed.Details);
        Assert.Equal("cursor", details.AgentKind);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private TestFixture BuildPipeline(string seedRepoUrl, int maxConsecutiveFastFails = 3)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(new LocalGitHostOptions { RootDirectory = gitRoot }, NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var webhooks = new CapturingWebhookDispatcher();

        // Single-agent setup: no class router, no fallback target. A
        // non-quota fast-fail terminates the item as Failed, and the registry
        // sees the outcome via PipelineRunner's RecordRunOutcome call.
        var codex = new ScriptableAgent(AgentKind.Codex);
        var registry = new AgentRegistry([codex]);

        var project = new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Codex,
            Audit = new ProjectAudit
            {
                MaxIterations = 1,
                AuditTypes = [],
            },
        };
        var projects = new InMemoryProjectRepository(project);
        var composer = new ProjectAuditorComposer(new ScriptedAuditorCatalog([]));

        var availability = new AgentAvailabilityRegistry(
            new AvailabilityOptions
            {
                FastFailThresholdSeconds = 10,
                MaxConsecutiveFastFails = maxConsecutiveFastFails,
            },
            TimeProvider.System,
            NullLogger<AgentAvailabilityRegistry>.Instance);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions
            {
                SandboxImageReference = "ignored",
                AgentAllowedHosts = [],
            },
            NullLogger<PipelineRunner>.Instance,
            quotaClassifier: new CompositeQuotaFailureClassifier(new IAgentQuotaFailureDetector[]
            {
                new ClaudeQuotaFailureDetector(),
                new CodexQuotaFailureDetector(),
                new GeminiQuotaFailureDetector(),
            }),
            availability: availability);

        return new TestFixture(pipeline, store, codex, webhooks, availability);
    }

    private static WorkItem NewItem(AgentKind initialAgent) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "availability wiring",
        Prompt = "do thing",
        BaseBranch = "main",
        Agent = initialAgent,
        PushUpstream = false,
    };

    private sealed class TestFixture : IDisposable
    {
        public PipelineRunner Pipeline { get; }
        public SqliteWorkItemStore Store { get; }
        public ScriptableAgent Codex { get; }
        public CapturingWebhookDispatcher Webhooks { get; }
        public AgentAvailabilityRegistry Registry { get; }

        public TestFixture(
            PipelineRunner pipeline,
            SqliteWorkItemStore store,
            ScriptableAgent codex,
            CapturingWebhookDispatcher webhooks,
            AgentAvailabilityRegistry registry)
        {
            Pipeline = pipeline;
            Store = store;
            Codex = codex;
            Webhooks = webhooks;
            Registry = registry;
        }

        public void Dispose() => Store.Dispose();
    }
}

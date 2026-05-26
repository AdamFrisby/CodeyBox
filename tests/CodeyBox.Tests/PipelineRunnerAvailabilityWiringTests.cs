using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Agents.Claude;
using CodeyBox.Agents.Codex;
using CodeyBox.Agents.Gemini;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;

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

    // ── Harness ──────────────────────────────────────────────────────────────

    private TestFixture BuildPipeline(string seedRepoUrl)
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
            new AvailabilityOptions { FastFailThresholdSeconds = 10, MaxConsecutiveFastFails = 3 },
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

using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox;
using CodeyBox.Sandbox.Process;
using CodeyBox.Upstream;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Integration tests for stuck-agent detection and recovery.
/// Injects a programmable activity source (zero-activity) and a very short
/// poll interval so stuck is detected within milliseconds, then verifies
/// the pipeline's recovery behaviour.
/// </summary>
[Collection("Pipeline integration")]
public sealed class StuckRecoveryTests : IDisposable
{
    private readonly string _workspace;

    public StuckRecoveryTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-stuck-").FullName;

    public void Dispose()
    { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Always reports zero CPU ticks and zero TCP connections, causing the
    /// probe to classify the agent as stuck after <c>thresholdSamples</c>
    /// consecutive samples.
    /// </summary>
    private sealed class ZeroActivitySource : IAgentActivitySource
    {
        public ActivitySample? TryRead() => new ActivitySample(0, 0);
    }

    /// <summary>
    /// Agent that blocks until its CancellationToken is cancelled. Used to
    /// simulate a stuck agent that will never return naturally.
    /// </summary>
    private sealed class BlockingAgent : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;

        public async Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory,
            string prompt, AgentCredential? credential, string? modelId = null,
            string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            // Block until cancelled — simulates a deadlocked agent
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentResult(true, "unreachable", null, null);
        }
    }

    private TestPipelineWithStuck BuildStuckPipeline(
        string seedRepoUrl,
        int thresholdSamples = 2,
        bool autoRetry = false,
        int maxRetries = 2)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new BlockingAgent();
        var registry = new AgentRegistry([agent]);
        var webhooks = new CapturingWebhookDispatcher();

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test Project",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit
            {
                MaxIterations = 1,
                StuckThresholdMinutes = 1, // active; resolved against poll interval below
                AutoRetryOnStuck = autoRetry,
                MaxStuckRetries = maxRetries,
            },
        });

        var presetCatalog = new ScriptedAuditorCatalog([]);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var upstreamFactory = new TestUpstreamFactory();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, upstreamFactory, composer,
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        // Inject fast probe: zero-activity source + instant poll so the
        // threshold is hit after thresholdSamples polls (each ~1ms).
        pipeline.ActivitySourceFactory = () => new ZeroActivitySource();
        pipeline.StuckProbePollInterval = TimeSpan.FromMilliseconds(1);

        return new TestPipelineWithStuck(pipeline, store, webhooks, gitRoot);
    }

    private static WorkItem NewItem(string workBranch = "feature/test") => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = workBranch,
        WorkTimeout = TimeSpan.FromSeconds(30), // generous; probe fires long before this
        PushUpstream = false,
    };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StuckDetected_WorkItemTransitionsToFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildStuckPipeline(seed, thresholdSamples: 2);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await tp.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Contains("stuck", final.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StuckDetected_WebhookEventFired_WithCorrectShape()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildStuckPipeline(seed, thresholdSamples: 2);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        var stuckEvent = tp.Webhooks.Events.FirstOrDefault(e => e.Event == "work_item.agent_stuck");
        Assert.NotNull(stuckEvent);
        Assert.Equal(item.Id, stuckEvent!.WorkItem!.Id);

        var details = stuckEvent.Details as AgentStuckDetails;
        Assert.NotNull(details);
        Assert.Equal("work", details!.Phase);
        Assert.Equal("claude", details.AgentKind);
        Assert.True(details.Killed);
    }

    [Fact]
    public async Task StuckDetected_WorkItemFailedWebhookAlsoFired()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        using var tp = BuildStuckPipeline(seed);

        var item = NewItem();
        await tp.Store.CreateAsync(item);
        await tp.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Contains(tp.Webhooks.Events, e => e.Event == "work_item.failed");
    }

    [Fact]
    public async Task ThresholdZero_ProbeDisabled_PipelineRunsNormally_AndFailsForAgentReasons()
    {
        // StuckThresholdMinutes=0 → probe disabled. Blocking agent still blocks
        // but the phase timeout fires instead of the probe.
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var gitRoot = Path.Combine(_workspace, "repos-noprobe-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-noprobe-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new BlockingAgent();
        var registry = new AgentRegistry([agent]);

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seed,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit { StuckThresholdMinutes = 0 },
        });

        var presetCatalog = new ScriptedAuditorCatalog([]);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var webhooks = new NullWebhookDispatcher();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            BaseBranch = "main",
            WorkBranch = "feature/noprobe",
            WorkTimeout = TimeSpan.FromMilliseconds(200), // times out quickly without probe
            PushUpstream = false,
        };
        await store.CreateAsync(item);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipeline.RunAsync(item, cts.Token);

        var final = await store.GetAsync(item.Id);
        // Phase timed out, not stuck-killed
        Assert.Equal(WorkItemState.Failed, final!.State);
        // The stuck webhook must NOT have fired
        store.Dispose();
    }
}

internal sealed class TestPipelineWithStuck : IDisposable
{
    public PipelineRunner Pipeline { get; }
    public SqliteWorkItemStore Store { get; }
    public CapturingWebhookDispatcher Webhooks { get; }
    public string GitRoot { get; }

    public TestPipelineWithStuck(
        PipelineRunner pipeline,
        SqliteWorkItemStore store,
        CapturingWebhookDispatcher webhooks,
        string gitRoot)
    {
        Pipeline = pipeline;
        Store = store;
        Webhooks = webhooks;
        GitRoot = gitRoot;
    }

    public void Dispose() => Store.Dispose();
}

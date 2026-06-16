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
/// Tests for the credential smoke gate in <see cref="PipelineRunner.RunAsync"/>.
/// Verifies that a failing gate rejects work items before the agent runs, that
/// the webhook/store transitions are correct, and that opting out via
/// <see cref="Project.SkipCredentialSmokeTest"/> bypasses the gate.
/// </summary>
[Collection("Pipeline integration")]
public sealed class PickupSmokeTests : IDisposable
{
    private readonly string _workspace;

    public PickupSmokeTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-smoke-pickup-").FullName;

    public void Dispose()
    { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Agent that increments a counter on each call, then blocks forever.
    /// Lets us verify whether the agent was ever reached.
    /// </summary>
    private sealed class TrackingBlockingAgent : IAgentRunner
    {
        public int CallCount { get; private set; }
        public AgentKind Kind => AgentKind.Claude;

        public async Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory,
            string prompt, AgentCredential? credential, string? modelId = null,
            string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            CallCount++;
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentResult(true, "unreachable", null, null);
        }
    }

    private sealed class TestResources : IDisposable
    {
        public PipelineRunner Pipeline { get; init; } = null!;
        public SqliteWorkItemStore Store { get; init; } = null!;
        public TrackingBlockingAgent Agent { get; init; } = null!;
        public CapturingWebhookDispatcher Webhooks { get; init; } = null!;
        public void Dispose() => Store.Dispose();
    }

    private TestResources BuildResources(
        string seedRepoUrl,
        CredentialSmokeGate? gate,
        bool skipSmoke = false)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new TrackingBlockingAgent();
        var registry = new AgentRegistry([agent]);
        var webhooks = new CapturingWebhookDispatcher();

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            SkipCredentialSmokeTest = skipSmoke,
            Audit = new ProjectAudit { StuckThresholdMinutes = 0 }, // disable stuck probe
        });

        var presetCatalog = new ScriptedAuditorCatalog([]);
        var composer = new ProjectAuditorComposer(presetCatalog);
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance,
            smokeGate: gate,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);

        return new TestResources
        {
            Pipeline = pipeline,
            Store = store,
            Agent = agent,
            Webhooks = webhooks,
        };
    }

    private static WorkItem NewItem(TimeSpan? workTimeout = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/smoke-" + Guid.NewGuid().ToString("N")[..8],
        WorkTimeout = workTimeout ?? TimeSpan.FromSeconds(30),
        PushUpstream = false,
    };

    // ── Gate failure ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SmokeGateFail_WorkItemTransitionsToFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (gate, _) = SmokeGateFactory.Build(probePass: false);
        using var r = BuildResources(seed, gate);

        var item = NewItem();
        await r.Store.CreateAsync(item);
        await r.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await r.Store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
    }

    [Fact]
    public async Task SmokeGateFail_LastErrorContainsSmokeReason()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (gate, _) = SmokeGateFactory.Build(probePass: false);
        using var r = BuildResources(seed, gate);

        var item = NewItem();
        await r.Store.CreateAsync(item);
        await r.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await r.Store.GetAsync(item.Id);
        Assert.Contains("credential smoke test failed: auth", final!.LastError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SmokeGateFail_AgentNeverInvoked()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (gate, _) = SmokeGateFactory.Build(probePass: false);
        using var r = BuildResources(seed, gate);

        var item = NewItem();
        await r.Store.CreateAsync(item);
        await r.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Equal(0, r.Agent.CallCount);
    }

    [Fact]
    public async Task SmokeGateFail_WorkItemFailedWebhookFires()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (gate, _) = SmokeGateFactory.Build(probePass: false);
        using var r = BuildResources(seed, gate);

        var item = NewItem();
        await r.Store.CreateAsync(item);
        await r.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Contains(r.Webhooks.Events, e => e.Event == "work_item.failed");
    }

    [Fact]
    public async Task SmokeGateFail_AgentSmokeFailedWebhookFires()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (gate, _) = SmokeGateFactory.Build(probePass: false);
        using var r = BuildResources(seed, gate);

        var item = NewItem();
        await r.Store.CreateAsync(item);
        await r.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.Contains(r.Webhooks.Events, e => e.Event == "agent.smoke_failed");
    }

    // ── Gate bypass (project opts out) ────────────────────────────────────────

    [Fact]
    public async Task SmokeDisabledGlobally_BypassesCredentialPickupGate()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var smokeOptions = new SmokeOptionsSnapshot(new SmokeOptions { Enabled = true });
        var (gate, probe) = SmokeGateFactory.Build(probePass: false, smokeOptions: smokeOptions);
        smokeOptions.Replace(new SmokeOptions { Enabled = false });
        using var r = BuildResources(seed, gate);

        var item = NewItem(workTimeout: TimeSpan.FromMilliseconds(200));
        await r.Store.CreateAsync(item);
        await r.Pipeline.RunAsync(item, CancellationToken.None);

        Assert.True(r.Agent.CallCount > 0);
        Assert.Equal(0, probe.CallCount);
        var final = await r.Store.GetAsync(item.Id);
        Assert.DoesNotContain("credential smoke test failed", final!.LastError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProjectSkipsCredentialSmoke_AgentIsInvoked()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        // Gate would fail — but project opts out, so agent runs instead.
        var (gate, _) = SmokeGateFactory.Build(probePass: false);
        using var r = BuildResources(seed, gate, skipSmoke: true);

        var item = NewItem(workTimeout: TimeSpan.FromMilliseconds(200));
        await r.Store.CreateAsync(item);
        await r.Pipeline.RunAsync(item, CancellationToken.None);

        // Agent was reached (gate was bypassed).
        Assert.True(r.Agent.CallCount > 0);
    }

    [Fact]
    public async Task ProjectSkipsCredentialSmoke_LastErrorNotFromSmokeGate()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (gate, _) = SmokeGateFactory.Build(probePass: false);
        using var r = BuildResources(seed, gate, skipSmoke: true);

        var item = NewItem(workTimeout: TimeSpan.FromMilliseconds(200));
        await r.Store.CreateAsync(item);
        await r.Pipeline.RunAsync(item, CancellationToken.None);

        var final = await r.Store.GetAsync(item.Id);
        // Item fails (timeout), but not because of the smoke gate.
        Assert.NotNull(final!.LastError);
        Assert.DoesNotContain("credential smoke test failed", final.LastError,
            StringComparison.OrdinalIgnoreCase);
    }
}

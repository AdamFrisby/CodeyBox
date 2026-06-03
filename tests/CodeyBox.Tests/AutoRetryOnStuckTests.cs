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
/// Tests for the <c>AutoRetryOnStuck</c> / <c>MaxStuckRetries</c> behaviour.
/// Uses the same fast-probe injection as <see cref="StuckRecoveryTests"/>.
/// </summary>
[Collection("Pipeline integration")]
public sealed class AutoRetryOnStuckTests : IDisposable
{
    private readonly string _workspace;

    public AutoRetryOnStuckTests()
        => _workspace = Directory.CreateTempSubdirectory("codeybox-autoretry-").FullName;

    public void Dispose()
    { try { Directory.Delete(_workspace, recursive: true); } catch { } }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class ZeroActivity : IAgentActivitySource
    {
        public ActivitySample? TryRead() => new ActivitySample(0, 0);
    }

    private sealed class BlockingAgent2 : IAgentRunner
    {
        public AgentKind Kind => AgentKind.Claude;
        public async Task<AgentResult> RunAsync(ISandbox sandbox, string workingDirectory,
            string prompt, AgentCredential? credential, string? modelId = null,
            string? reasoningMode = null, CancellationToken ct = default, Action<string>? stdoutChunkCallback = null, bool captureStructuredStream = false)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new AgentResult(true, "unreachable", null, null);
        }
    }

    private (PipelineRunner pipeline, SqliteWorkItemStore store, CapturingWebhookDispatcher webhooks)
        BuildPipeline(string seedRepoUrl, bool autoRetry, int maxRetries = 2)
    {
        var gitRoot = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")[..8]);
        var stateDb = Path.Combine(_workspace, "state-" + Guid.NewGuid().ToString("N")[..8] + ".db");

        var store = new SqliteWorkItemStore(stateDb);
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var sandboxes = new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance);
        var prs = new InMemoryPullRequestService();
        var agent = new BlockingAgent2();
        var registry = new AgentRegistry([agent]);
        var webhooks = new CapturingWebhookDispatcher();

        var projects = new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("test-project"),
            DisplayName = "Test",
            RepositoryUrl = seedRepoUrl,
            DefaultBaseBranch = "main",
            DefaultAgent = AgentKind.Claude,
            Audit = new ProjectAudit
            {
                MaxIterations = 1,
                StuckThresholdMinutes = 1,
                AutoRetryOnStuck = autoRetry,
                MaxStuckRetries = maxRetries,
            },
        });

        var presetCatalog = new ScriptedAuditorCatalog([]);
        var composer = new ProjectAuditorComposer(presetCatalog);

        var pipeline = new PipelineRunner(
            sandboxes, gitHost, registry, new StaticCredentialProvider(), prs,
            projects, new TestUpstreamFactory(), composer,
            store, webhooks,
            new PipelineOptions { SandboxImageReference = "ignored" },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable);

        pipeline.ActivitySourceFactory = () => new ZeroActivity();
        pipeline.StuckProbePollInterval = TimeSpan.FromMilliseconds(1);

        return (pipeline, store, webhooks);
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "test",
        Prompt = "do thing",
        BaseBranch = "main",
        WorkBranch = "feature/auto",
        WorkTimeout = TimeSpan.FromSeconds(30),
        PushUpstream = false,
    };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AutoRetryDisabled_StuckDetected_WorkItemFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (pipeline, store, _) = BuildPipeline(seed, autoRetry: false);
        using var _ = store;

        var item = NewItem();
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        var final = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, final!.State);
        Assert.Equal(0, final.StuckRetries);
    }

    [Fact]
    public async Task AutoRetryEnabled_StuckDetected_WorkItemRequeued_WithIncrementedCounter()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (pipeline, store, webhooks) = BuildPipeline(seed, autoRetry: true, maxRetries: 2);
        using var _ = store;

        var item = NewItem();
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        var requeued = await store.GetAsync(item.Id);
        // Item should be back in Queued state (ready for next dispatch)
        Assert.Equal(WorkItemState.Queued, requeued!.State);
        Assert.Equal(1, requeued.StuckRetries);
        Assert.Null(requeued.LastError);

        // Webhook must still fire
        Assert.Contains(webhooks.Events, e => e.Event == "work_item.agent_stuck");
    }

    [Fact]
    public async Task AutoRetryEnabled_MaxRetriesExceeded_WorkItemFailed()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (pipeline, store, _) = BuildPipeline(seed, autoRetry: true, maxRetries: 1);
        using var _ = store;

        // First run: stuck → re-queued with StuckRetries=1
        var item = NewItem();
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        var afterFirst = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, afterFirst!.State);
        Assert.Equal(1, afterFirst.StuckRetries);

        // Second run: stuck again, but MaxStuckRetries=1 is now exhausted → Failed
        await pipeline.RunAsync(afterFirst, CancellationToken.None);

        var afterSecond = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, afterSecond!.State);
        Assert.Equal(1, afterSecond.StuckRetries); // counter is not incremented on final failure
    }

    [Fact]
    public async Task AutoRetryEnabled_WebhookEventFired_BothRuns()
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_workspace);
        var (pipeline, store, webhooks) = BuildPipeline(seed, autoRetry: true, maxRetries: 2);
        using var _ = store;

        var item = NewItem();
        await store.CreateAsync(item);
        await pipeline.RunAsync(item, CancellationToken.None);

        // One agent_stuck event per stuck-killed run
        var stuckEvents = webhooks.Events.Where(e => e.Event == "work_item.agent_stuck").ToList();
        Assert.Single(stuckEvents);
        var details = stuckEvents[0].Details as AgentStuckDetails;
        Assert.NotNull(details);
        Assert.True(details!.Killed);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Integration test for quota-aware routing through the full
/// OrchestratorService → AgentClassRouter → PipelineRunner path.
///
/// Scenario: a work item requests class "frontier-coding"; Claude is
/// exhausted (below threshold) and Codex is available. Verifies that the
/// item is dispatched to Codex, not Claude.
/// </summary>
public sealed class PipelineRunnerRoutingTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-routetest-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public PipelineRunnerRoutingTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task ClaudeExhausted_CodexAvailable_RoutesToCodex()
    {
        // Arrange: frontier-coding class — Claude sub (exhausted), Codex sub (available).
        var frontierClass = new AgentClass
        {
            Id = "frontier-coding",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex,  Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        // Claude returns 2 % (below 10 % threshold), Codex returns 80 %.
        var router = new AgentClassRouter(
            [frontierClass],
            [new FakeProbe(AgentKind.Claude, 2.0), new FakeProbe(AgentKind.Codex, 80.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromSeconds(5) },
            NullLogger<AgentClassRouter>.Instance);

        // Tracking pipeline that records the Agent value it receives.
        var tracking = new AgentTrackingPipeline(_store);

        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier-coding",
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var svc = new OrchestratorService(
            queue, _store, tracking, registry, opts,
            NullLogger<OrchestratorService>.Instance,
            router,
            projects: null);   // no project repo — AgentClassId is on the item directly

        await queue.EnqueueAsync(item.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        // Wait for the item to be processed.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && tracking.LastAgent is null)
            await Task.Delay(20);

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(AgentKind.Codex, tracking.LastAgent);
    }

    [Fact]
    public async Task NoAgentClassId_NoRouter_BehavesExactlyAsLegacy()
    {
        // Item with no AgentClassId and no DefaultAgentClass → behaves exactly
        // as before: direct agent pick, no probe call.
        var router = new AgentClassRouter(
            [], [],
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance);

        var tracking = new AgentTrackingPipeline(_store);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            // No AgentClassId — agent defaults to null (pipeline picks project default).
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var svc = new OrchestratorService(
            queue, _store, tracking, registry, opts,
            NullLogger<OrchestratorService>.Instance,
            router, projects: null);

        await queue.EnqueueAsync(item.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && tracking.LastAgent is null && !tracking.ReceivedNullAgent)
            await Task.Delay(20);

        await svc.StopAsync(CancellationToken.None);

        // Pipeline was called; agent was NOT overridden by the router
        // (item.Agent is still null, matching legacy behaviour).
        Assert.True(tracking.ReceivedNullAgent, "Pipeline should receive item with null Agent (legacy direct-pick path)");
    }

    [Fact]
    public async Task AllSubscriptionExhausted_ItemIsDeferred_NotRunImmediately()
    {
        // All members below threshold → item deferred. Pipeline should NOT be called.
        var frontierClass = new AgentClass
        {
            Id = "frontier-coding",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        var router = new AgentClassRouter(
            [frontierClass],
            [new FakeProbe(AgentKind.Claude, 1.0)],  // below 10 % threshold
            new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = TimeSpan.FromSeconds(60),  // long enough that requeue won't fire during test
            },
            NullLogger<AgentClassRouter>.Instance);

        var tracking = new AgentTrackingPipeline(_store);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier-coding",
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var svc = new OrchestratorService(
            queue, _store, tracking, registry, opts,
            NullLogger<OrchestratorService>.Instance,
            router, projects: null);

        await queue.EnqueueAsync(item.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        // Give the orchestrator time to process the item (it should defer, not run).
        await Task.Delay(300);
        await svc.StopAsync(CancellationToken.None);

        // Pipeline must NOT have been invoked.
        Assert.Null(tracking.LastAgent);
        Assert.False(tracking.ReceivedNullAgent);
    }
}

/// <summary>
/// Pipeline that records the agent that was set on the work item at dispatch
/// time, then marks the item Done.
/// </summary>
internal sealed class AgentTrackingPipeline : IPipelineRunner
{
    private readonly IWorkItemStore _store;

    public AgentKind? LastAgent { get; private set; }
    public bool ReceivedNullAgent { get; private set; }

    public AgentTrackingPipeline(IWorkItemStore store) => _store = store;

    public async Task RunAsync(WorkItem item, CancellationToken ct)
    {
        LastAgent = item.Agent;
        ReceivedNullAgent = item.Agent is null;
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}

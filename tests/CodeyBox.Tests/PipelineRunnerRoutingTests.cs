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
[Collection("Background service timing")]
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
    public async Task StoredAgentPreferenceOutscored_OrchestratorRewritesAgentToRouterChoice()
    {
        var frontierClass = new AgentClass
        {
            Id = "frontier-coding",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
                new AgentMembership { Agent = AgentKind.Codex,  Billing = AgentBilling.Subscription, QualityScore = 90 },
            ],
        };

        var router = new AgentClassRouter(
            [frontierClass],
            [new FakeProbe(AgentKind.Claude, 50.0), new FakeProbe(AgentKind.Codex, 50.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromSeconds(5) },
            NullLogger<AgentClassRouter>.Instance);

        var tracking = new AgentTrackingPipeline(_store);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            Agent = AgentKind.Codex,
            AgentClassId = "frontier-coding",
            MinModelScore = 80,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var svc = new OrchestratorService(
            queue, _store, tracking, registry, opts,
            NullLogger<OrchestratorService>.Instance,
            router,
            projects: null);

        await queue.EnqueueAsync(item.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && tracking.LastAgent is null)
            await Task.Delay(20);

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(AgentKind.Claude, tracking.LastAgent);

        var stored = await _store.GetAsync(item.Id);
        Assert.Equal(AgentKind.Claude, stored?.Agent);
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
    public async Task WorkCompleteContinuation_ResolvesWorkAgentClassAtPickup()
    {
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
            [new FakeProbe(AgentKind.Claude, 0.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0, QuotaRecheckInterval = TimeSpan.FromSeconds(60) },
            NullLogger<AgentClassRouter>.Instance);
        var tracking = new AgentTrackingPipeline(_store);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.WorkComplete,
            AgentClassId = "frontier-coding",
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue,
            _store,
            tracking,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router,
            projects: null);

        await queue.EnqueueAsync(item.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(tracking.Ran, Task.Delay(TimeSpan.FromMilliseconds(300)));
        await svc.StopAsync(CancellationToken.None);

        Assert.NotSame(tracking.Ran, completed);
        Assert.Null(tracking.LastAgent);
        Assert.False(tracking.ReceivedNullAgent);
        var stored = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, stored?.State);
    }

    [Fact]
    public async Task AllMembersBelowFloor_ItemMarkedFailed_PipelineNotCalled()
    {
        // All members have QualityScore=80 but item requires MinModelScore=95 → NoEligibleMembers.
        var frontierClass = new AgentClass
        {
            Id = "frontier-coding",
            DisplayName = "Frontier",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 80 },
            ],
        };

        var router = new AgentClassRouter(
            [frontierClass],
            [new FakeProbe(AgentKind.Claude, 80.0)],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance);

        var tracking = new AgentTrackingPipeline(_store);
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("proj"),
            Title = "t",
            Prompt = "p",
            AgentClassId = "frontier-coding",
            MinModelScore = 95,
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

        // Wait for the item to be marked Failed.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var stored = await _store.GetAsync(item.Id);
            if (stored?.State == WorkItemState.Failed) break;
            await Task.Delay(20);
        }

        await svc.StopAsync(CancellationToken.None);

        // Pipeline must NOT have been called.
        Assert.Null(tracking.LastAgent);
        Assert.False(tracking.ReceivedNullAgent);

        // Item must be marked Failed in the store with the ROUTING_NO_ELIGIBLE reason.
        var finalItem = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, finalItem?.State);
        Assert.Contains("ROUTING_NO_ELIGIBLE", finalItem?.LastError ?? "");
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
    private string? _lastAgent;
    private int _receivedNullAgent;
    private readonly TaskCompletionSource _ran =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AgentKind? LastAgent
    {
        get
        {
            var value = Volatile.Read(ref _lastAgent);
            return value is null ? null : new AgentKind(value);
        }
    }
    public bool ReceivedNullAgent => Volatile.Read(ref _receivedNullAgent) != 0;
    public Task Ran => _ran.Task;

    public AgentTrackingPipeline(IWorkItemStore store) => _store = store;

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        Volatile.Write(ref _lastAgent, item.Agent?.Value);
        if (item.Agent is null)
            Volatile.Write(ref _receivedNullAgent, 1);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
        _ran.TrySetResult();
    }
}

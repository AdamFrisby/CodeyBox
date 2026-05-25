using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;

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

        var finalItem = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, finalItem?.State);
        Assert.Equal("quota", finalItem?.FailureKind);
        Assert.NotNull(finalItem?.QuotaResetAt);
        Assert.NotNull(finalItem?.NextQuotaRetryAt);
    }

    [Fact]
    public async Task QuotaReservationReleasedAfterPipelineCompletion_AllowsNextItem()
    {
        var router = BuildReservationRouter(availablePct: 60.0, estimatedPctCost: 45.0);
        var pickupCount = 0;
        var pipeline = new CountingPipelineRunner(
            _store,
            onRun: () => Interlocked.Increment(ref pickupCount));

        var first = MakeRoutedItem();
        var second = MakeRoutedItem();
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router,
            projects: null);

        await queue.EnqueueAsync(first.Id);
        await queue.EnqueueAsync(second.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref pickupCount) < 2)
            await Task.Delay(20);

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref pickupCount));
        Assert.Equal(WorkItemState.Done, (await _store.GetAsync(first.Id))!.State);
        Assert.Equal(WorkItemState.Done, (await _store.GetAsync(second.Id))!.State);
    }

    [Fact]
    public async Task QuotaReservationRefreshesProbeBeforeRelease()
    {
        var probe = new RefreshingQuotaProbe(
            AgentKind.Claude,
            beforeRefreshAvailablePct: 60.0,
            afterRefreshAvailablePct: 15.0);
        var router = BuildReservationRouter(probe, estimatedPctCost: 45.0);
        var pickupCount = 0;
        var pipeline = new CountingPipelineRunner(
            _store,
            onRun: () => Interlocked.Increment(ref pickupCount));

        var first = MakeRoutedItem() with { Title = "first", CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        var second = MakeRoutedItem() with { Title = "second", CreatedAt = DateTimeOffset.UtcNow };
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router,
            projects: null);

        await queue.EnqueueAsync(first.Id);
        await queue.EnqueueAsync(second.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        WorkItem? secondState = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            secondState = await _store.GetAsync(second.Id);
            if (secondState?.State == WorkItemState.WaitingForQuotaReset
                || Volatile.Read(ref pickupCount) > 1)
            {
                break;
            }
            await Task.Delay(20);
        }

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref pickupCount));
        Assert.Equal(1, probe.RefreshCount);
        Assert.Equal(WorkItemState.Done, (await _store.GetAsync(first.Id))!.State);
        Assert.Equal(WorkItemState.WaitingForQuotaReset, secondState?.State);
    }

    [Fact]
    public async Task ConcurrentWorkers_ReserveQuotaAndParkSecondItemUntilFirstCompletes()
    {
        var router = BuildReservationRouter(availablePct: 60.0, estimatedPctCost: 45.0);
        var started = 0;
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeline = new BlockingPipelineRunner(
            _store,
            onStart: () =>
            {
                if (Interlocked.Increment(ref started) == 1)
                    firstStarted.TrySetResult();
            },
            proceed.Task,
            onComplete: () => { });

        var now = DateTimeOffset.UtcNow;
        var first = MakeRoutedItem() with { Title = "first", CreatedAt = now.AddSeconds(-1) };
        var second = MakeRoutedItem() with { Title = "second", CreatedAt = now };
        await _store.CreateAsync(first);
        await _store.CreateAsync(second);

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            router,
            projects: null);

        await queue.EnqueueAsync(first.Id);
        await queue.EnqueueAsync(second.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        try
        {
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            WorkItem? parked = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                parked = await _store.GetAsync(second.Id);
                if (parked?.State == WorkItemState.WaitingForQuotaReset
                    || Volatile.Read(ref started) > 1)
                {
                    break;
                }
                await Task.Delay(20);
            }

            Assert.Equal(1, Volatile.Read(ref started));
            Assert.Equal(WorkItemState.WaitingForQuotaReset, parked?.State);
            Assert.Equal("quota", parked?.FailureKind);
        }
        finally
        {
            proceed.TrySetResult();
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task QuotaReservationReleasedAfterProjectPauseGate_AllowsRetryAfterResume()
    {
        var router = BuildReservationRouter(availablePct: 60.0, estimatedPctCost: 45.0);
        var pickupCount = 0;
        var pipeline = new CountingPipelineRunner(
            _store,
            onRun: () => Interlocked.Increment(ref pickupCount));
        var queueController = new ToggleProjectPauseQueueController(paused: true);

        var item = MakeRoutedItem();
        await _store.CreateAsync(item);
        var projects = new InMemoryProjectRepository(new Project
        {
            Id = item.ProjectId,
            DisplayName = item.ProjectId.Value,
            RepositoryUrl = "http://fake",
            DefaultAgentClass = "quota-reservation",
        });

        var queue = new InMemoryTaskQueue();
        var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue,
            _store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router,
            projects: projects,
            queueController: queueController);

        await queue.EnqueueAsync(item.Id);
        using var _ = registry;
        await svc.StartAsync(CancellationToken.None);

        await queueController.ProjectStateChecked.WaitAsync(TimeSpan.FromSeconds(5));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await svc.GetStatusAsync();
            if (status.CurrentlyRunning == 0)
                break;
            await Task.Delay(20);
        }

        Assert.Equal(0, Volatile.Read(ref pickupCount));

        queueController.ResumeProject();
        await queue.EnqueueAsync(item.Id);

        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline && Volatile.Read(ref pickupCount) == 0)
            await Task.Delay(20);

        await svc.StopAsync(CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref pickupCount));
        Assert.Equal(WorkItemState.Done, (await _store.GetAsync(item.Id))!.State);
    }

    private static WorkItem MakeRoutedItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "t",
        Prompt = "p",
        AgentClassId = "quota-reservation",
    };

    private static AgentClassRouter BuildReservationRouter(double availablePct, double estimatedPctCost)
        => BuildReservationRouter(new FakeProbe(AgentKind.Claude, availablePct), estimatedPctCost);

    private static AgentClassRouter BuildReservationRouter(IAgentQuotaProbe probe, double estimatedPctCost)
    {
        var agentClass = new AgentClass
        {
            Id = "quota-reservation",
            DisplayName = "Quota reservation",
            Members =
            [
                new AgentMembership { Agent = AgentKind.Claude, Billing = AgentBilling.Subscription, QualityScore = 100 },
            ],
        };

        return new AgentClassRouter(
            [agentClass],
            [probe],
            new QuotaRouterOptions
            {
                MinQuotaPct = 10.0,
                QuotaRecheckInterval = TimeSpan.FromHours(1),
            },
            NullLogger<AgentClassRouter>.Instance,
            headroomEstimator: new FixedHeadroomEstimator(estimatedPctCost));
    }
}

internal sealed class RefreshingQuotaProbe : IAgentQuotaProbe
{
    private readonly object _gate = new();
    private readonly AgentQuotaSnapshot _afterRefresh;
    private AgentQuotaSnapshot _current;

    public RefreshingQuotaProbe(
        AgentKind kind,
        double beforeRefreshAvailablePct,
        double afterRefreshAvailablePct)
    {
        Kind = kind;
        _current = new AgentQuotaSnapshot { AvailablePct = beforeRefreshAvailablePct };
        _afterRefresh = new AgentQuotaSnapshot { AvailablePct = afterRefreshAvailablePct };
    }

    public AgentKind Kind { get; }

    public int RefreshCount { get; private set; }

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        lock (_gate)
            return Task.FromResult(_current);
    }

    public Task<AgentQuotaSnapshot> RefreshAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        lock (_gate)
        {
            RefreshCount++;
            _current = _afterRefresh;
            return Task.FromResult(_current);
        }
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

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        LastAgent = item.Agent;
        ReceivedNullAgent = item.Agent is null;
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }
}

internal sealed class ToggleProjectPauseQueueController : IQueueController
{
    private volatile bool _paused;
    private readonly TaskCompletionSource _projectStateChecked =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ToggleProjectPauseQueueController(bool paused) => _paused = paused;

    public QueueState State => QueueState.Running;
    public DateTimeOffset? PausedAt => null;
    public string? PausedReason => null;
    public Task ProjectStateChecked => _projectStateChecked.Task;

    public Task PauseAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct = default)
    {
        _paused = true;
        return Task.CompletedTask;
    }

    public Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        _paused = false;
        return Task.CompletedTask;
    }

    public void ResumeProject() => _paused = false;

    public Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct = default)
    {
        _projectStateChecked.TrySetResult();
        return Task.FromResult<ProjectQueueState?>(new ProjectQueueState(
            projectId,
            _paused,
            _paused ? DateTimeOffset.UtcNow : null,
            _paused ? "paused for test" : null));
    }
}

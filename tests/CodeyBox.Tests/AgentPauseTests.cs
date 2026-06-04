using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class AgentPauseTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"codeybox-agent-pause-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task PauseState_PersistsAcrossRestart()
    {
        using (var ctrl = MakeController())
        {
            await ctrl.PauseAsync(AgentKind.Claude, "maintenance", "test");
        }

        using var restarted = MakeController();
        var state = await restarted.GetAgentStateAsync(AgentKind.Claude);

        Assert.NotNull(state);
        Assert.Equal("maintenance", state!.PausedReason);
        Assert.Equal("test", state.PausedBy);
    }

    [Fact]
    public async Task PauseWithExpiry_AutoResumesOnRead()
    {
        var now = new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var ctrl = MakeController(time);

        await ctrl.PauseAsync(AgentKind.Gemini, "outage", "test", now.AddHours(1));
        Assert.NotNull(await ctrl.GetAgentStateAsync(AgentKind.Gemini));

        time.Advance(TimeSpan.FromHours(2));
        Assert.Null(await ctrl.GetAgentStateAsync(AgentKind.Gemini));
        Assert.Empty(await ctrl.ListPausedAsync());
    }

    [Fact]
    public async Task Router_ExcludesPausedAgent_AndDispatchesOtherEligibleAgent()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Codex, "reserve quota", "test");
        var router = BuildRouter(pauses,
            new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members =
                [
                    Member(AgentKind.Codex),
                    Member(AgentKind.Claude),
                ],
            });

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.False(decision.ShouldWait);
        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
    }

    [Fact]
    public async Task Router_OnlyEligiblePausedAgent_ReturnsPausedWaitDecision()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "operator reserve", "test");
        var router = BuildRouter(pauses,
            new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members = [Member(AgentKind.Claude)],
            });

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.True(decision.ShouldWait);
        Assert.True(decision.WaitingForPausedAgent);
        Assert.Contains("paused by operator", decision.Reason);
        Assert.Equal([AgentKind.Claude], decision.PausedAgents);
    }

    [Fact]
    public async Task Worker_OnlyEligiblePausedAgent_ParksThenSchedulerRequeuesOnResume()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "operator reserve", "test");

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipeline(store);
        var router = BuildRouter(pauses,
            new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members = [Member(AgentKind.Claude)],
            });
        var item = Item("frontier");
        await store.CreateAsync(item);

        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: ProjectRepo(),
            agentPauseController: pauses);

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        var parked = await WaitForStateAsync(store, item.Id, WorkItemState.WaitingForAgentResume);
        Assert.NotNull(parked);
        Assert.False(pipeline.Entered);
        Assert.Equal("work", parked!.QuotaRetryFrom);

        await svc.StopAsync(CancellationToken.None);
        await pauses.ResumeAsync(AgentKind.Claude, "test");

        var scheduler = new AgentPauseRetryScheduler(
            store,
            queue,
            pauses,
            NullLogger<AgentPauseRetryScheduler>.Instance);
        var retried = await scheduler.RetryWaitingItemsForTestAsync("test");

        Assert.Equal(1, retried);
        var resumed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, resumed!.State);
        Assert.Null(resumed.QuotaRetryFrom);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task PausingAgent_DoesNotKillInFlightRun()
    {
        using var pauses = MakeController();
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var pipeline = new BlockingPipeline(store);
        var item = Item(classId: null) with { Agent = AgentKind.Claude };
        await store.CreateAsync(item);

        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: ProjectRepo(),
            agentPauseController: pauses);

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(TimeSpan.FromSeconds(5)));
        await pauses.PauseAsync(AgentKind.Claude, "after start", "test");
        pipeline.Release();

        var done = await WaitForStateAsync(store, item.Id, WorkItemState.Done);
        Assert.NotNull(done);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_AgentControlResumeItem_RunsEvenWhenTargetAgentPaused()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "existing pause", "test");

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var pipeline = new AgentControlPipeline(store, pauses);
        var item = Item(classId: null) with
        {
            JobType = JobType.AgentControl,
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Resume,
                Agent = AgentKind.Claude.Value,
                Reason = "resume from queued control",
            },
        };
        await store.CreateAsync(item);

        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: ProjectRepo(),
            agentPauseController: pauses);

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        var done = await WaitForStateAsync(store, item.Id, WorkItemState.Done);
        Assert.NotNull(done);
        Assert.True(pipeline.Entered);
        Assert.Null(await pauses.GetAgentStateAsync(AgentKind.Claude));
        await svc.StopAsync(CancellationToken.None);
    }

    private SqliteAgentPauseController MakeController(TimeProvider? timeProvider = null) =>
        new(_dbPath, NullLogger<SqliteAgentPauseController>.Instance, timeProvider);

    private static AgentClassRouter BuildRouter(
        IAgentPauseController pauses,
        AgentClass agentClass) =>
        new(
            [agentClass],
            [
                new FakeProbe(AgentKind.Claude, 100.0),
                new FakeProbe(AgentKind.Codex, 100.0),
                new FakeProbe(AgentKind.Gemini, 100.0),
            ],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            agentPauses: pauses);

    private static AgentMembership Member(AgentKind agent) => new()
    {
        Agent = agent,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
    };

    private static WorkItem Item(string? classId) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("proj"),
        Title = "test",
        Prompt = "do work",
        AgentClassId = classId,
    };

    private static IProjectRepository ProjectRepo() =>
        new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Project",
            RepositoryUrl = "https://github.com/test/repo",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "frontier",
        });

    private static async Task<WorkItem?> WaitForStateAsync(
        IWorkItemStore store,
        WorkItemId id,
        WorkItemState state)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            var item = await store.GetAsync(id);
            if (item?.State == state)
                return item;
            await Task.Delay(25);
        }
        return await store.GetAsync(id);
    }

    private sealed class CapturingPipeline(IWorkItemStore store) : IPipelineRunner
    {
        public bool Entered { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            Entered = true;
            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }

    private sealed class BlockingPipeline(IWorkItemStore store) : IPipelineRunner
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);
            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }

        public Task<bool> WaitForEnteredAsync(TimeSpan timeout) =>
            Task.WhenAny(_entered.Task, Task.Delay(timeout))
                .ContinueWith(t => t.Result == _entered.Task);

        public void Release() => _release.TrySetResult();
    }

    private sealed class AgentControlPipeline(
        IWorkItemStore store,
        IAgentPauseController pauses) : IPipelineRunner
    {
        public bool Entered { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            Entered = true;
            var spec = item.AgentControl ?? throw new InvalidOperationException("missing agentControl");
            var agent = new AgentKind(spec.Agent);
            if (spec.Action == AgentControlAction.Resume)
            {
                await pauses.ResumeAsync(agent, "test", spec.Reason, ct);
            }
            else
            {
                await pauses.PauseAsync(agent, spec.Reason ?? "test", "test", spec.ExpiresAt, ct);
            }

            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}

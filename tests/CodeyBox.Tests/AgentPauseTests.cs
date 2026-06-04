using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Sandbox.Process;
using CodeyBox.Webhooks;

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
    public async Task Router_PayPerApiFallback_SkipsPausedMemberAndDispatchesLowerEligibleMember()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Codex, "operator reserve", "test");
        var claude = PayPerApiMember(AgentKind.Claude, score: 100);
        var router = BuildRouter(pauses,
            new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members =
                [
                    PayPerApiMember(AgentKind.Codex, score: 150),
                    claude,
                ],
            });
        router.MarkExhausted(claude, TimeSpan.FromMinutes(5));

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.NotNull(decision.Chosen);
        Assert.Equal(AgentKind.Claude, decision.Chosen!.Agent);
        Assert.False(decision.ShouldWait);
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
    public async Task Worker_DirectAgentAlreadyPaused_ParksWithoutEnteringPipeline()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "maintenance", "test");

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipeline(store);
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

        var parked = await WaitForStateAsync(store, item.Id, WorkItemState.WaitingForAgentResume);
        Assert.NotNull(parked);
        Assert.False(pipeline.Entered);
        Assert.Equal("work", parked!.QuotaRetryFrom);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_AgentControlResumeItem_RunsRealPipelineEvenWhenTargetAgentPaused()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "existing pause", "test");

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var webhooks = new CapturingWebhookDispatcher();
        var pipeline = BuildRealAgentControlPipeline(store, pauses, webhooks);
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
        Assert.Null(await pauses.GetAgentStateAsync(AgentKind.Claude));
        Assert.Contains(webhooks.Events, e => e.Event == "agent.resumed");
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_AgentControlPauseItem_RunsRealPipelineAndPublishesPause()
    {
        using var pauses = MakeController();
        using var store = new SqliteWorkItemStore(_dbPath);
        var webhooks = new CapturingWebhookDispatcher();
        var pipeline = BuildRealAgentControlPipeline(store, pauses, webhooks);
        var item = Item(classId: null) with
        {
            JobType = JobType.AgentControl,
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Pause,
                Agent = AgentKind.Claude.Value,
                Reason = "reserve quota",
                DurationSeconds = 3600,
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var done = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, done!.State);
        var paused = await pauses.GetAgentStateAsync(AgentKind.Claude);
        Assert.NotNull(paused);
        Assert.Equal("reserve quota", paused!.PausedReason);
        Assert.Equal($"work-item:{item.Id}", paused.PausedBy);
        Assert.NotNull(paused.ExpiresAt);
        Assert.Contains(webhooks.Events, e => e.Event == "agent.paused");
    }

    [Fact]
    public async Task AgentPauseRetryScheduler_StartsAndWakesWaitingItemsOnResumeSignal()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "operator reserve", "test");
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        using var scheduler = new AgentPauseRetryScheduler(
            store,
            queue,
            pauses,
            NullLogger<AgentPauseRetryScheduler>.Instance,
            signal: pauses);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            var item = Item(classId: null) with
            {
                State = WorkItemState.WaitingForAgentResume,
                LastError = "waiting: agent paused: paused by operator: operator reserve",
                QuotaRetryFrom = "work",
            };
            await store.CreateAsync(item);

            await pauses.ResumeAsync(AgentKind.Claude, "test", "operator ready");

            var resumed = await WaitForStateAsync(store, item.Id, WorkItemState.Queued);
            Assert.NotNull(resumed);
            Assert.Null(resumed!.QuotaRetryFrom);
            Assert.True(await WaitForQueueCountAsync(queue, 1));
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(WorkItemState.WorkComplete, "audit", WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.AuditPassed, "merge", WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merged, "upstream", WorkItemState.Merged)]
    public void PauseResumeMapper_PreservesDurablePhaseBoundaries(
        WorkItemState parkedFrom,
        string retryFrom,
        WorkItemState resumeState)
    {
        Assert.Equal(retryFrom, AgentPauseResumeMapper.RetryFromForState(parkedFrom));
        Assert.Equal(resumeState, AgentPauseResumeMapper.ResumeStateForRetryFrom(retryFrom));
    }

    private static PipelineRunner BuildRealAgentControlPipeline(
        IWorkItemStore store,
        IAgentPauseController pauses,
        IWebhookDispatcher webhooks)
    {
        var gitRoot = Path.Combine(Path.GetTempPath(), $"codeybox-agent-control-git-{Guid.NewGuid():N}");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        return new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new AgentRegistry([new ScriptedAgent([MergeStrategy.RealMerge])]),
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            ProjectRepo(),
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            agentPauseController: pauses);
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
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

    private static AgentMembership Member(AgentKind agent) => new()
    {
        Agent = agent,
        Billing = AgentBilling.Subscription,
        QualityScore = 100,
    };

    private static AgentMembership PayPerApiMember(AgentKind agent, int score) => new()
    {
        Agent = agent,
        Billing = AgentBilling.PayPerApi,
        QualityScore = score,
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

    private static async Task<bool> WaitForQueueCountAsync(InMemoryTaskQueue queue, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            if (queue.Count == count)
                return true;
            await Task.Delay(25);
        }
        return queue.Count == count;
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

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }
}

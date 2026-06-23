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
    public async Task PauseWithExpiredExpiry_IsNotLoadedAfterRestart()
    {
        var now = new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using (var ctrl = MakeController(time))
        {
            await ctrl.PauseAsync(AgentKind.Gemini, "outage", "test", now.AddHours(1));
        }

        time.Advance(TimeSpan.FromHours(2));
        using var restarted = MakeController(time);

        Assert.Null(await restarted.GetAgentStateAsync(AgentKind.Gemini));
        Assert.Empty(await restarted.ListPausedAsync());
    }

    [Fact]
    public async Task ExpiredInstancePause_FallsBackToKindWidePause()
    {
        var now = new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var ctrl = MakeController(time);

        await ctrl.PauseAsync(AgentKind.Claude, "provider outage", "test");
        await ctrl.PauseAsync(
            AgentKind.Claude,
            "account flagged",
            "test",
            now.AddMinutes(1),
            agentInstanceId: "claude/acct-a");

        time.Advance(TimeSpan.FromMinutes(2));
        var state = await ctrl.GetAgentStateAsync(AgentKind.Claude, agentInstanceId: "claude/acct-a");

        Assert.NotNull(state);
        Assert.Null(state!.AgentInstanceId);
        Assert.Equal("provider outage", state.PausedReason);
        var paused = Assert.Single(await ctrl.ListPausedAsync());
        Assert.Null(paused.AgentInstanceId);
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
    public async Task Router_ExcludesPausedInstance_AndDispatchesSameKindSibling()
    {
        using var pauses = MakeController();
        var acctA = Member(AgentKind.Claude) with { InstanceId = "acct-a", QualityScore = 100 };
        var acctB = Member(AgentKind.Claude) with { InstanceId = "acct-b", QualityScore = 99 };
        await pauses.PauseAsync(AgentKind.Claude, "account flagged", "test", agentInstanceId: acctA.RouteKey);
        var router = BuildRouter(pauses,
            new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members = [acctA, acctB],
            });

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.False(decision.ShouldWait);
        Assert.NotNull(decision.Chosen);
        Assert.Equal(acctB.RouteKey, decision.Chosen!.RouteKey);
        var paused = Assert.Single(await pauses.ListPausedAsync());
        Assert.Equal(acctA.RouteKey, paused.AgentInstanceId);
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
    public async Task Router_PauseTakesPrecedenceOverInProcessExhaustionCache()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "operator reserve", "test");
        var claude = Member(AgentKind.Claude);
        var router = BuildRouter(pauses,
            new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members = [claude],
            });
        router.MarkExhausted(claude, TimeSpan.FromMinutes(5));

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.True(decision.ShouldWait);
        Assert.True(decision.WaitingForPausedAgent);
        Assert.Contains("paused by operator", decision.Reason);
        Assert.Equal([AgentKind.Claude], decision.PausedAgents);
    }

    [Fact]
    public async Task Router_MixedPausedAndQuotaBlockedMembers_ParksOnQuotaNotPausedAgent()
    {
        // Paused codex + quota-blocked claude: park on the quota-recovery
        // channel so QuotaRetryScheduler wakes the item when claude's quota
        // refills, instead of stranding it behind the paused codex until the
        // operator resumes. Paused agents stay visible in the decision so
        // dashboards/logs still surface the pause.
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Codex, "operator reserve", "test");
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
            },
            [
                new FakeProbe(AgentKind.Codex, 100.0),
                new FakeProbe(AgentKind.Claude, 0.0),
            ]);

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.True(decision.ShouldWait);
        Assert.False(decision.WaitingForPausedAgent);
        Assert.Contains("below the effective quota floor", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("paused", decision.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([AgentKind.Codex], decision.PausedAgents);
    }

    [Fact]
    public async Task DispatchAvailability_EnsureAvailableAsync_UsesRealPauseController()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Codex, "provider outage", "test");
        var gate = new CountingSmokeGate();
        var dispatch = new AgentDispatchAvailability(inVmSmokeGate: gate, pauses: pauses);

        var availability = await dispatch.EnsureAvailableAsync(
            AgentKind.Codex,
            new InVmSmokeSandboxTarget(null, SandboxProfileFlavor.Headless),
            CancellationToken.None);

        Assert.True(AgentDispatchAvailability.IsPausedVerdict(availability));
        Assert.Contains("provider outage", availability!.Reason);
        Assert.Equal(0, gate.EnsureCalls);
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
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        var parked = await WaitForStateAsync(store, item.Id, WorkItemState.WaitingForAgentResume);
        Assert.NotNull(parked);
        Assert.False(pipeline.Entered);
        Assert.Equal("work", parked!.AgentPauseRetryFrom);
        Assert.Null(parked.QuotaRetryFrom);

        await svc.StopAsync(CancellationToken.None);
        await pauses.ResumeAsync(AgentKind.Claude, "test");

        var scheduler = NewPauseRetryScheduler(store, queue, pauses);
        var retried = await scheduler.RetryWaitingItemsForTestAsync("test");

        Assert.Equal(1, retried);
        var resumed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, resumed!.State);
        Assert.Null(resumed.AgentPauseRetryFrom);
        Assert.Null(resumed.QuotaRetryFrom);
        Assert.True(queue.Count >= 1);
    }

    [Fact]
    public async Task Worker_MultiplePausedEligibleAgents_RequeuesAfterOneAgentResumes()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "claude maintenance", "test");
        await pauses.PauseAsync(AgentKind.Codex, "codex maintenance", "test");

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipeline(store);
        var router = BuildRouter(pauses,
            new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members =
                [
                    Member(AgentKind.Claude),
                    Member(AgentKind.Codex),
                ],
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
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        var parked = await WaitForStateAsync(store, item.Id, WorkItemState.WaitingForAgentResume);
        Assert.NotNull(parked);
        Assert.Null(parked!.Agent);
        Assert.False(pipeline.Entered);

        await svc.StopAsync(CancellationToken.None);
        await pauses.ResumeAsync(AgentKind.Codex, "test");

        var scheduler = NewPauseRetryScheduler(store, queue, pauses);
        var retried = await scheduler.RetryWaitingItemsForTestAsync("test");

        Assert.Equal(1, retried);
        var resumed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, resumed!.State);
        Assert.Equal(AgentKind.Codex, await RouteResumedAgentAsync(router, resumed));
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
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(TimeSpan.FromSeconds(15)));
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
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        var parked = await WaitForStateAsync(store, item.Id, WorkItemState.WaitingForAgentResume);
        Assert.NotNull(parked);
        Assert.False(pipeline.Entered);
        Assert.Equal("work", parked!.AgentPauseRetryFrom);
        Assert.Null(parked.QuotaRetryFrom);
        await svc.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(WorkItemState.Planning, "work", WorkItemState.Queued, false)]
    [InlineData(WorkItemState.PlanApproved, "plan_approved", WorkItemState.PlanApproved, true)]
    public async Task Worker_PlanningResumeStateWithPausedDirectAgent_ParksWithoutEnteringPipeline(
        WorkItemState state,
        string retryFrom,
        WorkItemState expectedResumeState,
        bool preservesApprovedPlan)
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "maintenance", "test");

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipeline(store);
        var item = Item(classId: null) with
        {
            Agent = AgentKind.Claude,
            State = state,
            PlanArtifact = state is WorkItemState.PlanReview or WorkItemState.PlanApproved ? ValidPlan : null,
            PlanGeneratedAt = state is WorkItemState.PlanReview or WorkItemState.PlanApproved
                ? DateTimeOffset.UtcNow.AddMinutes(-2)
                : null,
            PlanReviewedAt = state == WorkItemState.PlanApproved
                ? DateTimeOffset.UtcNow.AddMinutes(-1)
                : null,
            PlanReviewSummary = state == WorkItemState.PlanApproved ? "approved" : null,
        };

        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: ProjectRepo(),
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        await svc.StartAsync(CancellationToken.None);
        await store.CreateAsync(item);
        Assert.Equal(state, (await store.GetAsync(item.Id))!.State);
        await queue.EnqueueAsync(item.Id);

        var parked = await WaitForStateAsync(store, item.Id, WorkItemState.WaitingForAgentResume);
        Assert.NotNull(parked);
        Assert.False(pipeline.Entered);
        Assert.Equal(retryFrom, parked!.AgentPauseRetryFrom);
        Assert.Equal(AgentKind.Claude, parked.AgentPauseTarget);
        await svc.StopAsync(CancellationToken.None);

        await pauses.ResumeAsync(AgentKind.Claude, "test");
        var scheduler = NewPauseRetryScheduler(store, queue, pauses);
        var retried = await scheduler.RetryWaitingItemsForTestAsync("test");
        Assert.Equal(1, retried);

        var resumed = await store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(expectedResumeState, resumed!.State);
        if (preservesApprovedPlan)
        {
            Assert.Equal(ValidPlan, resumed.PlanArtifact);
            Assert.NotNull(resumed.PlanGeneratedAt);
            Assert.NotNull(resumed.PlanReviewedAt);
            Assert.Equal("approved", resumed.PlanReviewSummary);
        }
        else
        {
            Assert.Null(resumed.PlanArtifact);
            Assert.Null(resumed.PlanGeneratedAt);
            Assert.Null(resumed.PlanReviewedAt);
            Assert.Null(resumed.PlanReviewSummary);
        }

        using var resumedRegistry = new CancellationRegistry(CancellationToken.None);
        using var resumedSvc = new OrchestratorService(
            queue,
            store,
            pipeline,
            resumedRegistry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: ProjectRepo(),
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        await resumedSvc.StartAsync(CancellationToken.None);
        var done = await WaitForStateAsync(store, item.Id, WorkItemState.Done);
        Assert.NotNull(done);
        await resumedSvc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_PlanReviewWithPausedDirectAgent_DoesNotParkBeforePipeline()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "maintenance", "test");

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipeline(store);
        var item = Item(classId: null) with
        {
            Agent = AgentKind.Claude,
            State = WorkItemState.PlanReview,
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };

        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue,
            store,
            pipeline,
            registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            projects: ProjectRepo(),
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        await svc.StartAsync(CancellationToken.None);
        await store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        var done = await WaitForStateAsync(store, item.Id, WorkItemState.Done);
        Assert.NotNull(done);
        Assert.True(pipeline.Entered);
        Assert.Null(done!.AgentPauseRetryFrom);
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Worker_AuditPassedDirectItemWithPausedWorkAgent_EntersPipeline()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "maintenance", "test");

        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipeline(store);
        var item = Item(classId: null) with
        {
            Agent = AgentKind.Claude,
            State = WorkItemState.AuditPassed,
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
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

        await svc.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(item.Id);

        var done = await WaitForStateAsync(store, item.Id, WorkItemState.Done);
        Assert.NotNull(done);
        Assert.True(pipeline.Entered);
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
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

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
    public async Task Worker_AgentControlPauseItem_WebhookFailure_StillCompletesCommittedPause()
    {
        using var pauses = MakeController();
        using var store = new SqliteWorkItemStore(_dbPath);
        var pipeline = BuildRealAgentControlPipeline(store, pauses, new ThrowingWebhookDispatcher());
        var item = Item(classId: null) with
        {
            JobType = JobType.AgentControl,
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Pause,
                Agent = AgentKind.Claude.Value,
                Reason = "reserve quota",
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var done = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Done, done!.State);
        Assert.Null(done.FailureKind);
        var paused = await pauses.GetAgentStateAsync(AgentKind.Claude);
        Assert.NotNull(paused);
        Assert.Equal("reserve quota", paused!.PausedReason);
    }

    [Fact]
    public async Task Worker_AgentControlPauseItem_WithoutPauseController_FailsConfiguration()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var webhooks = new CapturingWebhookDispatcher();
        var pipeline = BuildRealAgentControlPipeline(store, pauses: null, webhooks);
        var item = Item(classId: null) with
        {
            JobType = JobType.AgentControl,
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Pause,
                Agent = AgentKind.Claude.Value,
                Reason = "reserve quota",
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("agent pause controller is not configured", failed.LastError);
    }

    [Fact]
    public async Task Worker_AgentControlItem_MissingSpec_FailsConfiguration()
    {
        using var pauses = MakeController();
        using var store = new SqliteWorkItemStore(_dbPath);
        var webhooks = new CapturingWebhookDispatcher();
        var pipeline = BuildRealAgentControlPipeline(store, pauses, webhooks);
        var item = Item(classId: null) with
        {
            JobType = JobType.AgentControl,
            AgentControl = null,
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("agentControl spec is missing", failed.LastError);
    }

    [Fact]
    public async Task Worker_AgentControlPauseItem_InvalidPersistedReason_FailsConfiguration()
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
                Reason = "   ",
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("agentControl.reason is required for pause", failed.LastError);
    }

    [Fact]
    public async Task Worker_AgentControlItem_UnsupportedPersistedAction_FailsConfiguration()
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
                Action = (AgentControlAction)99,
                Agent = AgentKind.Claude.Value,
                Reason = "reserve quota",
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("unsupported agentControl action", failed.LastError);
    }

    [Fact]
    public async Task Worker_AgentControlPauseItem_BlankPersistedAgent_FailsConfiguration()
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
                Agent = "   ",
                Reason = "reserve quota",
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("agentControl.agent is required", failed.LastError);
    }

    [Fact]
    public async Task Worker_AgentControlResumeItem_PersistedControlCharacterReason_FailsConfiguration()
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
                Action = AgentControlAction.Resume,
                Agent = AgentKind.Claude.Value,
                Reason = "bad\x01reason",
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("agentControl.reason", failed.LastError);
    }

    [Fact]
    public async Task Worker_AgentControlPauseItem_NonPositivePersistedDuration_FailsConfiguration()
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
                DurationSeconds = -3600,
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("durationSeconds must be positive", failed.LastError);
    }

    [Fact]
    public async Task Worker_AgentControlPauseItem_DurationAndExpiresAtBothSet_FailsConfiguration()
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
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("provide either durationSeconds or expiresAt", failed.LastError);
    }

    [Fact]
    public async Task Worker_AgentControlPauseItem_PastExpiresAt_FailsConfiguration()
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
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            },
        };
        await store.CreateAsync(item);

        await pipeline.RunAsync(item, CancellationToken.None);

        var failed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Failed, failed!.State);
        Assert.Equal("configuration", failed.FailureKind);
        Assert.Contains("agentControl.expiresAt must be in the future", failed.LastError);
    }

    [Fact]
    public async Task SqliteStore_MalformedAgentControlJson_HydratesAsNullSpec()
    {
        using var store = new SqliteWorkItemStore(_dbPath);
        var item = Item(classId: null) with
        {
            JobType = JobType.AgentControl,
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Pause,
                Agent = AgentKind.Claude.Value,
                Reason = "reserve quota",
            },
        };
        await store.CreateAsync(item);

        // Simulate corruption — overwrite the agent_control_json column with
        // invalid JSON. ReadAgentControlSpec must swallow the JsonException and
        // return null rather than stranding the row.
        await CorruptAgentControlJsonAsync(_dbPath, item.Id, "{not json");

        var hydrated = await store.GetAsync(item.Id);
        Assert.NotNull(hydrated);
        Assert.Null(hydrated!.AgentControl);
    }

    private static async Task CorruptAgentControlJsonAsync(
        string dbPath, WorkItemId id, string raw)
    {
        await using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE work_items SET agent_control_json = $j WHERE id = $i;";
        cmd.Parameters.AddWithValue("$j", raw);
        cmd.Parameters.AddWithValue("$i", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AgentClassRouter_CheckReadinessAsync_PausedOnly_ReturnsUnavailable()
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

        var capacity = new AlwaysHasCapacitySnapshot();
        var readiness = await router.CheckReadinessAsync(
            Item("frontier"), null, capacity, CancellationToken.None);

        Assert.Equal(AgentRoutingReadinessState.Unavailable, readiness.State);
        Assert.NotNull(readiness.Reason);
        Assert.Contains("paused by operator", readiness.Reason!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AlwaysHasCapacitySnapshot : IAgentCapacitySnapshot
    {
        public bool HasCapacity(AgentKind agent) => true;
    }

    [Fact]
    public async Task Router_MixedPausedAndQuotaBlocked_DoesNotWaitForPausedAgent_AndPauseStillSurfaces()
    {
        // Acceptance: when one member is paused and another is quota-blocked,
        // the router must NOT park as WaitingForAgentResume — that would
        // strand the item behind the operator pause when the unpaused peer
        // could recover via the standard quota retry path. The paused agent
        // still surfaces in PausedAgents/Reason for dashboard visibility.
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Codex, "operator reserve", "test");
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
            },
            [
                new FakeProbe(AgentKind.Codex, 100.0),
                new FakeProbe(AgentKind.Claude, 0.0),
            ]);

        var decision = await router.ResolveAsync(Item("frontier"), null, CancellationToken.None);

        Assert.True(decision.ShouldWait);
        Assert.False(decision.WaitingForPausedAgent);
        Assert.Equal([AgentKind.Codex], decision.PausedAgents);
    }

    [Fact]
    public async Task AgentPauseRetryScheduler_StartsAndWakesWaitingItemsOnResumeSignal()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "operator reserve", "test");
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        using var scheduler = NewPauseRetryScheduler(store, queue, pauses, pauses);

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

    [Fact]
    public async Task AgentPauseRetryScheduler_DoesNotResumeWaitingItemUntilTargetAgentUnpaused()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "operator reserve", "test");
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var item = Item(classId: null) with
        {
            Agent = AgentKind.Claude,
            AgentPauseTarget = AgentKind.Claude,
            State = WorkItemState.WaitingForAgentResume,
            LastError = "waiting: agent paused: paused by operator: operator reserve",
            QuotaRetryFrom = "work",
        };
        await store.CreateAsync(item);
        var scheduler = NewPauseRetryScheduler(store, queue, pauses);

        Assert.Equal(0, await scheduler.RetryWaitingItemsForTestAsync("still-paused"));
        var stillParked = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForAgentResume, stillParked!.State);
        Assert.Equal(0, queue.Count);

        await pauses.ResumeAsync(AgentKind.Claude, "test", "operator ready");
        Assert.Equal(1, await scheduler.RetryWaitingItemsForTestAsync("resumed"));
        var resumed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, resumed!.State);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task AgentPauseRetryScheduler_RequeuesUnstampedWaitingItemEvenWhenAnotherAgentStillPaused()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Claude, "claude maintenance", "test");
        await pauses.PauseAsync(AgentKind.Codex, "codex maintenance", "test");
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var item = Item("frontier") with
        {
            Agent = null,
            State = WorkItemState.WaitingForAgentResume,
            LastError = "waiting: agent paused: multiple paused agents",
            QuotaRetryFrom = "work",
        };
        await store.CreateAsync(item);
        var scheduler = NewPauseRetryScheduler(store, queue, pauses);

        await pauses.ResumeAsync(AgentKind.Codex, "test", "operator ready");
        var retried = await scheduler.RetryWaitingItemsForTestAsync("resumed-one");

        Assert.Equal(1, retried);
        var resumed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, resumed!.State);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task AgentPauseRetryScheduler_ResumeClearsStaleTransientRetryFields()
    {
        using var pauses = MakeController();
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var firstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var item = Item(classId: null) with
        {
            State = WorkItemState.WaitingForAgentResume,
            LastError = "waiting: agent paused: paused by operator: maintenance",
            AgentPauseRetryFrom = "work",
            NextTransientRetryAt = firstFailedAt.AddMinutes(5),
            TransientRetryAttempts = 2,
            TransientRetryFirstFailedAt = firstFailedAt,
            TransientRetryFrom = "merge",
        };
        await store.CreateAsync(item);
        var scheduler = NewPauseRetryScheduler(store, queue, pauses);

        var retried = await scheduler.RetryWaitingItemsForTestAsync("agent-resumed");

        Assert.Equal(1, retried);
        var resumed = await store.GetAsync(item.Id);
        Assert.NotNull(resumed);
        Assert.Equal(WorkItemState.Queued, resumed!.State);
        Assert.Null(resumed.NextTransientRetryAt);
        Assert.Equal(0, resumed.TransientRetryAttempts);
        Assert.Null(resumed.TransientRetryFirstFailedAt);
        Assert.Null(resumed.TransientRetryFrom);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task AgentPauseRetryScheduler_PeriodicExpirySweep_RequeuesExpiredPause()
    {
        var now = new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var pauses = MakeController(time);
        await pauses.PauseAsync(AgentKind.Claude, "outage", "test", now.AddMinutes(30));
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var item = Item(classId: null) with
        {
            Agent = AgentKind.Claude,
            AgentPauseTarget = AgentKind.Claude,
            State = WorkItemState.WaitingForAgentResume,
            LastError = "waiting: agent paused: paused by operator: outage",
            QuotaRetryFrom = "work",
        };
        await store.CreateAsync(item);
        var scheduler = NewPauseRetryScheduler(store, queue, pauses);

        time.Advance(TimeSpan.FromHours(1));
        var retried = await scheduler.RunPeriodicExpirySweepForTestAsync();

        Assert.Equal(1, retried);
        var resumed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, resumed!.State);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task AgentPauseRetryScheduler_QueueKickFailure_RollsBackWaitingItem()
    {
        using var pauses = MakeController();
        using var store = new SqliteWorkItemStore(_dbPath);
        var item = Item(classId: null) with
        {
            Agent = AgentKind.Claude,
            AgentPauseTarget = AgentKind.Claude,
            State = WorkItemState.WaitingForAgentResume,
            LastError = "waiting: agent paused: paused by operator: maintenance",
            QuotaRetryFrom = "work",
        };
        await store.CreateAsync(item);
        var queue = new ThrowingForWorkItemQueue(item.Id);
        var scheduler = NewPauseRetryScheduler(store, queue, pauses);

        var retried = await scheduler.RetryWaitingItemsForTestAsync("queue-failure");

        Assert.Equal(0, retried);
        var after = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WaitingForAgentResume, after!.State);
        Assert.Equal("work", after.QuotaRetryFrom);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task AgentPauseRetryScheduler_UsesPauseTargetInsteadOfWorkAgent()
    {
        using var pauses = MakeController();
        await pauses.PauseAsync(AgentKind.Codex, "audit paused", "test");
        await pauses.PauseAsync(AgentKind.Claude, "work paused", "test");
        using var store = new SqliteWorkItemStore(_dbPath);
        var queue = new InMemoryTaskQueue();
        var item = Item(classId: null) with
        {
            Agent = AgentKind.Claude,
            AgentPauseTarget = AgentKind.Codex,
            State = WorkItemState.WaitingForAgentResume,
            LastError = "waiting: agent paused: paused by operator: audit paused",
            QuotaRetryFrom = "audit",
        };
        await store.CreateAsync(item);
        var scheduler = NewPauseRetryScheduler(store, queue, pauses);

        Assert.Equal(0, await scheduler.RetryWaitingItemsForTestAsync("still-target-paused"));

        await pauses.ResumeAsync(AgentKind.Codex, "test", "audit ready");
        var retried = await scheduler.RetryWaitingItemsForTestAsync("target-resumed");

        Assert.Equal(1, retried);
        var resumed = await store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, resumed!.State);
        Assert.Null(resumed.AgentPauseTarget);
        Assert.Equal(AgentKind.Claude, resumed.Agent);
        Assert.Equal(1, queue.Count);
    }

    [Theory]
    [InlineData(WorkItemState.Planning, "planning", WorkItemState.Queued)]
    [InlineData(WorkItemState.PlanReview, "plan_review", WorkItemState.PlanReview)]
    [InlineData(WorkItemState.PlanApproved, "plan_approved", WorkItemState.PlanApproved)]
    [InlineData(WorkItemState.WorkComplete, "audit", WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.ReworkingForConflict, "conflict_rework", WorkItemState.ReworkingForConflict)]
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
        IAgentPauseController? pauses,
        IWebhookDispatcher webhooks)
    {
        var gitRoot = Path.Combine(Path.GetTempPath(), $"codeybox-agent-control-git-{Guid.NewGuid():N}");
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = gitRoot },
            NullLogger<LocalGitHost>.Instance);
        var projects = ProjectRepo();
        var terminalTransitions = TestSupport.CreateTerminalTransition(store, webhooks, projects);
        return new PipelineRunner(
            new ProcessSandboxProvider(NullLogger<ProcessSandboxProvider>.Instance),
            gitHost,
            new AgentRegistry([new ScriptedAgent([MergeStrategy.RealMerge])]),
            new StaticCredentialProvider(),
            new InMemoryPullRequestService(),
            projects,
            new TestUpstreamFactory(),
            new ProjectAuditorComposer(new ScriptedAuditorCatalog([])),
            store,
            webhooks,
            new PipelineOptions { SandboxImageReference = "ignored", AgentAllowedHosts = [] },
            NullLogger<PipelineRunner>.Instance,
            requiredBuildVerifier: TestRequiredBuildVerifier.NotApplicable,
            agentPauseController: pauses,
            terminalTransitions: terminalTransitions,
            terminalRevisionBuilder: terminalTransitions);
    }

    private SqliteAgentPauseController MakeController(TimeProvider? timeProvider = null) =>
        new(_dbPath, NullLogger<SqliteAgentPauseController>.Instance, timeProvider);

    private static AgentPauseRetryScheduler NewPauseRetryScheduler(
        IWorkItemStore store,
        ITaskQueue queue,
        IAgentPauseController pauses,
        IAgentPauseSignal? signal = null)
    {
        var retrier = new WorkItemRetrier(
            store,
            queue,
            new NullGitHost(),
            NullLogger<WorkItemRetrier>.Instance);
        return new AgentPauseRetryScheduler(
            store,
            retrier,
            pauses,
            NullLogger<AgentPauseRetryScheduler>.Instance,
            signal);
    }

    private static AgentClassRouter BuildRouter(
        IAgentPauseController pauses,
        AgentClass agentClass,
        IEnumerable<IAgentQuotaProbe>? probes = null) =>
        new(
            [agentClass],
            probes ??
            [
                new FakeProbe(AgentKind.Claude, 100.0),
                new FakeProbe(AgentKind.Codex, 100.0),
                new FakeProbe(AgentKind.Gemini, 100.0),
            ],
            new QuotaRouterOptions { MinQuotaPct = 10.0 },
            NullLogger<AgentClassRouter>.Instance,
            dispatchAvailability: new AgentDispatchAvailability(pauses: pauses));

    private static async Task<AgentKind?> RouteResumedAgentAsync(
        AgentClassRouter router,
        WorkItem item)
    {
        var decision = await router.ResolveAsync(item, null, CancellationToken.None);
        return decision.Chosen?.Agent;
    }

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

    private const string ValidPlan = """
        {
          "approach": "pause routing",
          "files": ["output.txt"],
          "testStrategy": ["run tests"],
          "risks": ["none"],
          "satisfiesTask": "routes through planning state"
        }
        """;

    private sealed class ThrowingForWorkItemQueue : ITaskQueue
    {
        private readonly WorkItemId _throwFor;
        private readonly InMemoryTaskQueue _inner = new();

        public ThrowingForWorkItemQueue(WorkItemId throwFor) => _throwFor = throwFor;

        public int Count => _inner.Count;

        public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default)
        {
            if (id == _throwFor)
                throw new InvalidOperationException("queue enqueue failed");

            return _inner.EnqueueAsync(id, ct);
        }

        public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default) =>
            _inner.EnqueueDispatchWakeAsync(ct);

        public ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default) =>
            _inner.DequeueAsync(ct);

        public ValueTask<bool> DequeueDispatchSignalAsync(CancellationToken ct = default) =>
            _inner.DequeueDispatchSignalAsync(ct);
    }

    private static IProjectRepository ProjectRepo() =>
        new InMemoryProjectRepository(new Project
        {
            Id = new ProjectId("proj"),
            DisplayName = "Project",
            RepositoryUrl = "https://github.com/test/repo",
            DefaultAgent = AgentKind.Claude,
            DefaultAgentClass = "frontier",
        });

    // The state/queue transitions these helpers wait on are driven by the
    // background dispatch loop, whose scheduling latency balloons to ~8-10s
    // when the capped (6-core) full-suite suite starves the ThreadPool — the
    // target state is reached deterministically, just slowly. The previous 5s
    // budget was below that observed latency, so the wait abandoned early and
    // returned a not-yet-transitioned item, intermittently failing the callers'
    // state assertions. The 30s budget is a starvation backstop consistent with
    // this suite's other background-service waits (e.g. WaitForEnteredAsync),
    // not the mechanism that makes the wait succeed.
    private static readonly TimeSpan StateWaitBudget = TimeSpan.FromSeconds(30);

    private static async Task<WorkItem?> WaitForStateAsync(
        IWorkItemStore store,
        WorkItemId id,
        WorkItemState state)
    {
        using var cts = new CancellationTokenSource(StateWaitBudget);
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
        using var cts = new CancellationTokenSource(StateWaitBudget);
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

        public async Task<bool> WaitForEnteredAsync(TimeSpan timeout)
        {
            try
            {
                await _entered.Task.WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class CountingSmokeGate : IInVmSmokeGate
    {
        public bool Enabled => true;

        public int EnsureCalls { get; private set; }

        public Task<AgentAvailability> EnsureAvailableAsync(
            AgentKind kind,
            InVmSmokeSandboxTarget target,
            CancellationToken ct = default)
        {
            EnsureCalls++;
            return Task.FromResult(new AgentAvailability(true, null, null));
        }

        public Task<AgentAvailability?> ForceProbeAsync(AgentKind kind, CancellationToken ct = default)
            => Task.FromResult<AgentAvailability?>(new AgentAvailability(true, null, null));

        public Task ProbeAllAsync(CancellationToken ct) => Task.CompletedTask;

        public Task ProbeAllAsync(InVmSmokeSandboxTarget target, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ThrowingWebhookDispatcher : IWebhookDispatcher
    {
        public Task PublishAsync(WebhookEvent evt, CancellationToken ct = default)
        {
            if (evt.Event.StartsWith("agent.", StringComparison.Ordinal))
                throw new InvalidOperationException("webhook unavailable");

            return Task.CompletedTask;
        }
    }
}

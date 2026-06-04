using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkerPoolHealthWatchdogTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-pool-health-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly InMemoryTaskQueue _queue;
    private readonly InMemoryProjectRepository _projects;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly ManualTimeProvider _time;
    private readonly OrchestratorService _orchestrator;

    public WorkerPoolHealthWatchdogTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _queue = new InMemoryTaskQueue();
        _projects = new InMemoryProjectRepository(Project());
        _webhooks = new CapturingWebhookDispatcher();
        _time = new ManualTimeProvider();
        _orchestrator = new OrchestratorService(
            _queue,
            _store,
            new NoopPipeline(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance);
    }

    public void Dispose()
    {
        _orchestrator.Dispose();
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task StuckUnderfilledPool_EmitsCriticalWebhookAndKicksDispatch()
    {
        var item = Item();
        await _store.CreateAsync(item);
        var watchdog = BuildWatchdog(new WorkerPoolHealthWatchdogOptions
        {
            StallTimeout = TimeSpan.FromMinutes(1),
            CheckInterval = TimeSpan.FromMinutes(1),
            MaxRecoveryAttempts = 2,
            RecoveryVerificationDelay = TimeSpan.Zero,
        });

        await watchdog.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_webhooks.Events);
        Assert.Equal(0, _queue.Count);

        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _queue.Count);
        var stalled = Assert.Single(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        Assert.Null(stalled.WorkItem);
        using var details = JsonDocument.Parse(JsonSerializer.Serialize(stalled.Details));
        var root = details.RootElement;
        Assert.Equal("critical", root.GetProperty("severity").GetString());
        Assert.Equal(0, root.GetProperty("currentlyRunning").GetInt32());
        Assert.Equal(2, root.GetProperty("maxConcurrent").GetInt32());
        Assert.Equal(2, root.GetProperty("freeSlots").GetInt32());
        Assert.Equal(1, root.GetProperty("recoveryAttempt").GetInt32());
        Assert.True(root.GetProperty("stuckForSeconds").GetInt64() >= 60);
        Assert.Contains(item.Id.ToString(), root.GetProperty("runnableWorkItemIds")
            .EnumerateArray()
            .Select(e => e.GetString()));
    }

    [Fact]
    public async Task RecoveryStillStuck_AfterBoundedAttempts_EmitsRestartRequired()
    {
        await _store.CreateAsync(Item());
        var watchdog = BuildWatchdog(new WorkerPoolHealthWatchdogOptions
        {
            StallTimeout = TimeSpan.FromMinutes(1),
            CheckInterval = TimeSpan.FromMinutes(1),
            MaxRecoveryAttempts = 1,
            RecoveryVerificationDelay = TimeSpan.Zero,
        });

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.restart_required");
    }

    [Fact]
    public async Task ActiveItem_IsNotTreatedAsRunnableCandidate()
    {
        var item = Item();
        await _store.CreateAsync(item);
        var blocking = new BlockingPipeline();
        var orchestrator = new OrchestratorService(
            _queue,
            _store,
            blocking,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions
            {
                MaxConcurrentWorkers = 2,
                ShutdownDrainTimeout = TimeSpan.FromSeconds(5),
            },
            NullLogger<OrchestratorService>.Instance);

        try
        {
            await _queue.EnqueueAsync(item.Id);
            await orchestrator.StartAsync(CancellationToken.None);
            await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var health = BuildHealthSource(orchestrator: orchestrator);
            var candidates = await health.ListRunnableCandidatesAsync(10, CancellationToken.None);
            Assert.DoesNotContain(candidates, c => c.Id == item.Id);

            var watchdog = BuildWatchdog(
                new WorkerPoolHealthWatchdogOptions
                {
                    StallTimeout = TimeSpan.FromMinutes(1),
                    CheckInterval = TimeSpan.FromMinutes(1),
                    RecoveryVerificationDelay = TimeSpan.Zero,
                },
                health);

            _time.Advance(TimeSpan.FromMinutes(2));
            await watchdog.RunOnceAsync(CancellationToken.None);

            Assert.DoesNotContain(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        }
        finally
        {
            blocking.Release.TrySetResult();
            try
            {
                await WaitUntilAsync(
                    () => orchestrator.CurrentlyRunningTotal == 0,
                    TimeSpan.FromSeconds(10));
                using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await orchestrator.StopAsync(stopCts.Token);
            }
            finally
            {
                orchestrator.Dispose();
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for condition.");
            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }
    }

    [Fact]
    public async Task StallTimeoutZero_DisablesRuntimeWatchdog()
    {
        await _store.CreateAsync(Item());
        var watchdog = BuildWatchdog(new WorkerPoolHealthWatchdogOptions
        {
            StallTimeout = TimeSpan.Zero,
            CheckInterval = TimeSpan.FromMinutes(1),
        });

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromHours(1));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Empty(_webhooks.Events);
        Assert.Equal(0, _queue.Count);
    }

    [Theory]
    [InlineData("dispatch-paused")]
    [InlineData("queue-paused")]
    [InlineData("pool-full")]
    [InlineData("project-paused")]
    [InlineData("missing-agent")]
    [InlineData("unavailable-agent")]
    [InlineData("agent-cap")]
    [InlineData("router-unavailable")]
    public async Task IntentionalBlockers_DoNotEmitFalseStallAlert(string scenario)
    {
        IWorkerPoolHealthSource source;
        FakePoolHealthSource? fakeSource = null;
        OrchestratorService? localOrchestrator = null;

        switch (scenario)
        {
            case "dispatch-paused":
                fakeSource = new FakePoolHealthSource
                {
                    DispatchPaused = true,
                    Status = new WorkerPoolStatus(2, 0, 1, null),
                    Candidates = [Candidate(Item())],
                };
                source = fakeSource;
                break;
            case "pool-full":
                fakeSource = new FakePoolHealthSource
                {
                    Status = new WorkerPoolStatus(2, 2, 1, null),
                    Candidates = [Candidate(Item())],
                };
                source = fakeSource;
                break;
            case "queue-paused":
                await _store.CreateAsync(Item());
                source = BuildHealthSource(queueController: new FakeQueueController(globalPaused: true));
                break;
            case "project-paused":
                await _store.CreateAsync(Item());
                source = BuildHealthSource(queueController: new FakeQueueController(projectPaused: true));
                break;
            case "missing-agent":
                await _store.CreateAsync(Item());
                source = BuildHealthSource(agents: new AgentRegistry([]));
                break;
            case "unavailable-agent":
                await _store.CreateAsync(Item());
                source = BuildHealthSource(availability: new FakeAvailabilityRegistry(available: false));
                break;
            case "agent-cap":
                await _store.CreateAsync(Item());
                localOrchestrator = new OrchestratorService(
                    _queue,
                    _store,
                    new NoopPipeline(_store),
                    new CancellationRegistry(CancellationToken.None),
                    new OrchestratorOptions { MaxConcurrentWorkers = 2 },
                    NullLogger<OrchestratorService>.Instance,
                    agentConcurrency: new AgentConcurrencyOptions
                    {
                        Members = { ["claude"] = new AgentConcurrencyEntry { MaxConcurrent = 1 } },
                    });
                Assert.True(localOrchestrator.TryReserveAgentSlotForTest(AgentKind.Claude));
                source = BuildHealthSource(orchestrator: localOrchestrator);
                break;
            case "router-unavailable":
                await _store.CreateAsync(Item());
                source = BuildHealthSource(
                    routingReadiness: new FixedRoutingReadiness(
                        AgentRoutingReadiness.Unavailable("should wait")));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var watchdog = BuildWatchdog(StandardOptions(), source);

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Empty(_webhooks.Events);
        Assert.Equal(0, fakeSource?.EnqueueCalls ?? _queue.Count);
        localOrchestrator?.Dispose();
    }

    [Fact]
    public async Task RoutingReadinessAvailable_MarksCandidateRunnable()
    {
        await _store.CreateAsync(Item() with { Agent = null });
        var watchdog = BuildWatchdog(
            StandardOptions(),
            BuildHealthSource(
                agents: new AgentRegistry([]),
                routingReadiness: new FixedRoutingReadiness(
                    AgentRoutingReadiness.Available(AgentKind.Codex))));

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task RoutingReadinessNotApplicable_FallsBackToProjectDefaultAgent()
    {
        await _store.CreateAsync(Item() with { Agent = null });
        var watchdog = BuildWatchdog(
            StandardOptions(),
            BuildHealthSource(
                routingReadiness: new FixedRoutingReadiness(
                    AgentRoutingReadiness.NotApplicable("direct agent"))));

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task RecoveryThrows_FinalAttemptEscalatesRestartRequired()
    {
        var source = new FakePoolHealthSource
        {
            Status = new WorkerPoolStatus(2, 0, 1, null),
            Candidates = [Candidate(Item())],
            ThrowOnRecovery = true,
        };
        var watchdog = BuildWatchdog(StandardOptions(maxAttempts: 1), source);

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.restart_required");
    }

    [Fact]
    public async Task DispatchProgressAfterRecovery_ResetsStallInsteadOfEscalating()
    {
        var source = new FakePoolHealthSource
        {
            Status = new WorkerPoolStatus(2, 0, 1, null),
            Candidates = [Candidate(Item())],
            AdvanceLastSpawnOnRecovery = true,
        };
        var watchdog = BuildWatchdog(StandardOptions(maxAttempts: 1), source);

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        Assert.DoesNotContain(_webhooks.Events, e => e.Event == "worker_pool.restart_required");
    }

    [Fact]
    public async Task WaitingForQuotaResetCandidate_WatchdogRecoveryRequeuesEvenWhenAutoRetryDisabled()
    {
        var parked = Item() with
        {
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            AgentClassId = "test-class",
            NextQuotaRetryAt = _time.GetUtcNow().AddHours(1),
        };
        await _store.CreateAsync(parked);
        var router = new AgentClassRouter(
            [
                new AgentClass
                {
                    Id = "test-class",
                    DisplayName = "Test Class",
                    Members =
                    [
                        new AgentMembership
                        {
                            Agent = AgentKind.Claude,
                            Billing = AgentBilling.PayPerApi,
                            QualityScore = 100,
                        },
                    ],
                },
            ],
            [new PayPerApiQuotaProbe()],
            new QuotaRouterOptions(),
            NullLogger<AgentClassRouter>.Instance,
            _time);
        var retrier = new WorkItemRetrier(
            _store,
            _queue,
            new NullGitHost(),
            NullLogger<WorkItemRetrier>.Instance);
        var quotaRecovery = new QuotaRetryScheduler(
            _store,
            retrier,
            new OrchestratorOptions
            {
                AutoRetryOnQuotaFailure = new AutoRetryOnQuotaFailureOptions
                {
                    Enabled = false,
                    MaxAutoRetriesPerWorkItem = 3,
                },
            },
            NullLogger<QuotaRetryScheduler>.Instance,
            router,
            _projects,
            timeProvider: _time);
        var watchdog = BuildWatchdog(
            StandardOptions(),
            BuildHealthSource(),
            quotaRecovery);

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        var refetched = Assert.IsType<WorkItem>(await _store.GetAsync(parked.Id));
        Assert.Equal(WorkItemState.Queued, refetched.State);
        Assert.Equal(1, refetched.QuotaRetryAttempts);
        Assert.Equal(1, _queue.Count);
        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
    }

    [Fact]
    public async Task HealthScanLimit_IsIndependentFromRecoveryEnqueueBatch()
    {
        var unroutable = Item() with { Agent = AgentKind.Codex, Priority = 100 };
        var routable = Item() with { Agent = AgentKind.Claude, Priority = 0 };
        await _store.CreateAsync(unroutable);
        await _store.CreateAsync(routable);
        var watchdog = BuildWatchdog(new WorkerPoolHealthWatchdogOptions
        {
            StallTimeout = TimeSpan.FromMinutes(1),
            CheckInterval = TimeSpan.FromMinutes(1),
            MaxRecoveryAttempts = 2,
            MaxRecoveryEnqueueBatchSize = 1,
            MaxHealthCheckCandidateScan = 4,
            RecoveryVerificationDelay = TimeSpan.Zero,
        });

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        Assert.Equal(1, _queue.Count);
        Assert.Equal(routable.Id, await _queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeferredCandidate_IsVisibleToWatchdogRecovery()
    {
        var item = Item();
        await _store.CreateAsync(item);
        _orchestrator.MarkDeferredForTest(item.Id);
        var watchdog = BuildWatchdog(StandardOptions());

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.False(_orchestrator.IsDeferredForTest(item.Id));
        Assert.Equal(1, _queue.Count);
        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
    }

    [Fact]
    public async Task WatchdogEvaluationFailure_EmitsCriticalRestartRequiredWebhook()
    {
        var source = new FakePoolHealthSource
        {
            ThrowOnStatus = true,
        };
        var watchdog = BuildWatchdog(StandardOptions(), source);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var evt = Assert.Single(_webhooks.Events, e => e.Event == "worker_pool.restart_required");
        using var details = JsonDocument.Parse(JsonSerializer.Serialize(evt.Details));
        Assert.Equal("critical", details.RootElement.GetProperty("severity").GetString());
        Assert.Equal(
            "worker-pool health watchdog evaluation failed",
            details.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task HealthCandidates_FilterUnsatisfiedDependencies()
    {
        var satisfiedParent = Item() with { State = WorkItemState.Done };
        var blockedParent = Item() with { State = WorkItemState.Failed };
        var satisfiedChild = Item() with { DependsOn = [satisfiedParent.Id], Priority = 100 };
        var blockedChild = Item() with { DependsOn = [blockedParent.Id], Priority = 200 };
        await _store.CreateAsync(satisfiedParent);
        await _store.CreateAsync(blockedParent);
        await _store.CreateAsync(satisfiedChild);
        await _store.CreateAsync(blockedChild);

        var candidates = await BuildHealthSource().ListRunnableCandidatesAsync(
            10,
            CancellationToken.None);

        Assert.Contains(candidates, i => i.Id == satisfiedChild.Id);
        Assert.DoesNotContain(candidates, i => i.Id == blockedChild.Id);
    }

    [Fact]
    public async Task HealthCandidates_FilterBudgetBlockedWork()
    {
        var projects = new InMemoryProjectRepository(Project() with
        {
            Budget = new ProjectBudget { MaxConcurrentForProject = 1 },
        });
        var orchestrator = new OrchestratorService(
            _queue,
            _store,
            new NoopPipeline(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            projects: projects);
        var inFlight = Item() with
        {
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var candidate = Item();
        await _store.CreateAsync(inFlight);
        await _store.CreateAsync(candidate);

        var health = BuildHealthSource(orchestrator: orchestrator, projects: projects);
        var candidates = await health.ListRunnableCandidatesAsync(
            10,
            CancellationToken.None);

        Assert.DoesNotContain(candidates, i => i.Id == candidate.Id);
        orchestrator.Dispose();
    }

    [Fact]
    public async Task HealthCandidates_WhenSmokeDisabled_IgnoresDirectAgentSmokeExclusion()
    {
        var item = Item() with { Agent = AgentKind.Claude };
        await _store.CreateAsync(item);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        registry.MarkSmokeResult(
            AgentKind.Claude,
            new AgentSmokeResult(false, "transient: try later", TimeSpan.Zero, SmokeFailureCategory.Transient));
        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);

        var health = BuildHealthSource(
            availability: registry,
            smokeOptions: new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false }));

        var candidates = await health.ListRunnableCandidatesAsync(
            10,
            CancellationToken.None);

        Assert.Contains(candidates, i => i.Id == item.Id);
    }

    [Fact]
    public async Task HealthCandidates_WhenSmokeDisabled_StillHonorDirectAgentFastFailExclusion()
    {
        var item = Item() with { Agent = AgentKind.Claude };
        await _store.CreateAsync(item);
        var registry = new AgentAvailabilityRegistry(
            new AvailabilityOptions(), TimeProvider.System, NullLogger<AgentAvailabilityRegistry>.Instance);
        for (var i = 0; i < 3; i++)
            registry.RecordRunOutcome(AgentKind.Claude, success: false, duration: TimeSpan.FromMilliseconds(500));
        Assert.False(registry.GetAvailability(AgentKind.Claude).Available);

        var health = BuildHealthSource(
            availability: registry,
            smokeOptions: new SmokeOptionsSnapshot(new SmokeOptions { Enabled = false }));

        var candidates = await health.ListRunnableCandidatesAsync(
            10,
            CancellationToken.None);

        Assert.DoesNotContain(candidates, i => i.Id == item.Id);
    }

    [Fact]
    public async Task AuditPassedCandidate_IsTreatedAsRunnableWork()
    {
        var item = Item() with { State = WorkItemState.AuditPassed };
        await _store.CreateAsync(item);
        var watchdog = BuildWatchdog(StandardOptions());

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, _queue.Count);
        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
    }

    private WorkerPoolHealthWatchdog BuildWatchdog(WorkerPoolHealthWatchdogOptions opts)
        => BuildWatchdog(opts, BuildHealthSource());

    private WorkerPoolHealthWatchdog BuildWatchdog(
        WorkerPoolHealthWatchdogOptions opts,
        IWorkerPoolHealthSource source,
        IWorkerPoolQuotaRecovery? quotaRecovery = null)
        => new(
            source,
            opts,
            NullLogger<WorkerPoolHealthWatchdog>.Instance,
            quotaRecovery: quotaRecovery,
            webhooks: _webhooks,
            timeProvider: _time);

    private WorkerPoolHealthCoordinator BuildHealthSource(
        OrchestratorService? orchestrator = null,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IAgentRegistry? agents = null,
        IAgentEffectiveAvailabilityReader? availability = null,
        IAgentRoutingReadiness? routingReadiness = null,
        SmokeOptionsSnapshot? smokeOptions = null)
        => new(
            orchestrator ?? _orchestrator,
            _store,
            _queue,
            NullLogger<WorkerPoolHealthCoordinator>.Instance,
            projects ?? _projects,
            queueController,
            agents ?? new AgentRegistry([new DummyAgentRunner(AgentKind.Claude)]),
            routingReadiness,
            availability is null ? null : new AgentDispatchAvailability(availability, smokeOptions: smokeOptions));

    private static WorkerPoolHealthWatchdogOptions StandardOptions(int maxAttempts = 2) => new()
    {
        StallTimeout = TimeSpan.FromMinutes(1),
        CheckInterval = TimeSpan.FromMinutes(1),
        MaxRecoveryAttempts = maxAttempts,
        RecoveryVerificationDelay = TimeSpan.Zero,
    };

    private static Project Project() => new()
    {
        Id = new ProjectId("test-project"),
        DisplayName = "Test",
        RepositoryUrl = "https://example.invalid/repo.git",
        DefaultAgent = AgentKind.Claude,
    };

    private static WorkItem Item() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    private static WorkerPoolHealthCandidate Candidate(WorkItem item) =>
        new(item.Id, item.State);

    private sealed class NoopPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;

        public NoopPipeline(IWorkItemStore store) => _store = store;

        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
            => _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }

    private sealed class BlockingPipeline : IPipelineRunner
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
    }

    private sealed class FakePoolHealthSource : IWorkerPoolHealthSource
    {
        public bool DispatchPaused { get; set; }
        public bool AdvanceLastSpawnOnRecovery { get; set; }
        public bool ThrowOnStatus { get; set; }
        public bool ThrowOnRecovery { get; set; }
        public WorkerPoolStatus Status { get; set; } = new(2, 0, 0, null);
        public IReadOnlyList<WorkerPoolHealthCandidate> Candidates { get; set; } = [];
        public int EnqueueCalls { get; private set; }

        public bool IsDispatchPaused => DispatchPaused;

        public Task<WorkerPoolStatus> GetStatusAsync(CancellationToken ct = default)
        {
            if (ThrowOnStatus)
                throw new InvalidOperationException("health source failed");
            return Task.FromResult(Status);
        }

        public Task<IReadOnlyList<WorkerPoolHealthCandidate>> ListRunnableCandidatesAsync(
            int scanLimit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkerPoolHealthCandidate>>(Candidates.Take(scanLimit).ToList());

        public Task<int> TriggerDispatchRecoveryAsync(
            IEnumerable<WorkItemId> candidateIds,
            CancellationToken ct)
        {
            if (ThrowOnRecovery)
                throw new InvalidOperationException("recovery failed");

            var ids = candidateIds.ToList();
            EnqueueCalls += ids.Count;
            if (AdvanceLastSpawnOnRecovery)
                Status = Status with { LastSpawnAt = DateTimeOffset.UnixEpoch.AddMinutes(1) };
            return Task.FromResult(ids.Count);
        }

    }

    private sealed class FixedRoutingReadiness : IAgentRoutingReadiness
    {
        private readonly AgentRoutingReadiness _readiness;

        public FixedRoutingReadiness(AgentRoutingReadiness readiness) => _readiness = readiness;

        public Task<AgentRoutingReadiness> CheckReadinessAsync(
            WorkItem item,
            Project? project,
            IAgentCapacitySnapshot capacity,
            CancellationToken ct) =>
            Task.FromResult(_readiness);
    }

    private sealed class FakeQueueController : IQueueController
    {
        private readonly bool _projectPaused;

        public FakeQueueController(bool globalPaused = false, bool projectPaused = false)
        {
            State = globalPaused ? QueueState.Paused : QueueState.Running;
            _projectPaused = projectPaused;
        }

        public QueueState State { get; }
        public DateTimeOffset? PausedAt => null;
        public string? PausedReason => null;
        public Task PauseAsync(string reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct = default) =>
            Task.FromResult<ProjectQueueState?>(_projectPaused
                ? new ProjectQueueState(projectId, true, DateTimeOffset.UnixEpoch, "paused")
                : null);
    }

    private sealed class FakeAvailabilityRegistry : IAgentAvailabilityRegistry, IAgentEffectiveAvailabilityReader
    {
        private readonly bool _available;

        public FakeAvailabilityRegistry(bool available) => _available = available;

        public AgentAvailability GetAvailability(AgentKind kind) =>
            new(_available, _available ? null : "unavailable", null);

        public AgentAvailability GetAvailabilityWithoutSmokeGateExclusions(AgentKind kind) =>
            GetAvailability(kind);

        public AvailabilityTransition RecordRunOutcome(AgentKind kind, bool success, TimeSpan duration) =>
            new(false, !_available, _available ? null : "unavailable");

        public IReadOnlyList<AgentAvailabilitySnapshot> Snapshot() => [];
    }

    private sealed class DummyAgentRunner : IAgentRunner
    {
        public DummyAgentRunner(AgentKind kind) => Kind = kind;

        public AgentKind Kind { get; }

        public Task<AgentResult> RunAsync(
            ISandbox sandbox,
            string workingDirectory,
            string prompt,
            AgentCredential? credential,
            string? modelId = null,
            string? reasoningMode = null,
            CancellationToken ct = default,
            Action<string>? stdoutChunkCallback = null,
            bool captureStructuredStream = false)
            => Task.FromResult(new AgentResult(true, "ok", null, null));
    }
}

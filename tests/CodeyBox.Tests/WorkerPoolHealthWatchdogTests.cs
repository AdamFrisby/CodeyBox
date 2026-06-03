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
        var blocking = new BlockingPipeline(_store);
        var orchestrator = new OrchestratorService(
            _queue,
            _store,
            blocking,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance);

        await _queue.EnqueueAsync(item.Id);
        await orchestrator.StartAsync(CancellationToken.None);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var watchdog = new WorkerPoolHealthWatchdog(
            orchestrator,
            orchestrator,
            new WorkerPoolHealthWatchdogOptions
            {
                StallTimeout = TimeSpan.FromMinutes(1),
                CheckInterval = TimeSpan.FromMinutes(1),
                RecoveryVerificationDelay = TimeSpan.Zero,
            },
            NullLogger<WorkerPoolHealthWatchdog>.Instance,
            _projects,
            agents: new AgentRegistry([new DummyAgentRunner(AgentKind.Claude)]),
            webhooks: _webhooks,
            timeProvider: _time);

        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.DoesNotContain(_webhooks.Events, e => e.Event == "worker_pool.stalled");

        blocking.Release.SetResult();
        await blocking.Exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await orchestrator.StopAsync(CancellationToken.None);
        orchestrator.Dispose();
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
        var source = new FakePoolHealthSource
        {
            Status = new WorkerPoolStatus(2, 0, 1, null),
            Candidates = [Item()],
        };
        IQueueController? queueController = null;
        IAgentRegistry? agents = new AgentRegistry([new DummyAgentRunner(AgentKind.Claude)]);
        IAgentAvailabilityRegistry? availability = null;
        IAgentRoutingReadiness? routingReadiness = null;

        switch (scenario)
        {
            case "dispatch-paused":
                source.DispatchPaused = true;
                break;
            case "queue-paused":
                queueController = new FakeQueueController(globalPaused: true);
                break;
            case "pool-full":
                source.Status = source.Status with { CurrentlyRunning = 2 };
                break;
            case "project-paused":
                queueController = new FakeQueueController(projectPaused: true);
                break;
            case "missing-agent":
                agents = new AgentRegistry([]);
                break;
            case "unavailable-agent":
                availability = new FakeAvailabilityRegistry(available: false);
                break;
            case "agent-cap":
                source.HasAgentCapacity = false;
                break;
            case "router-unavailable":
                routingReadiness = new FixedRoutingReadiness(AgentRoutingReadiness.Unavailable("should wait"));
                break;
        }

        var watchdog = new WorkerPoolHealthWatchdog(
            source,
            source,
            StandardOptions(),
            NullLogger<WorkerPoolHealthWatchdog>.Instance,
            _projects,
            queueController,
            agents,
            availability,
            routingReadiness,
            webhooks: _webhooks,
            timeProvider: _time);

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Empty(_webhooks.Events);
        Assert.Equal(0, source.EnqueueCalls);
    }

    [Fact]
    public async Task DispatchProgressAfterRecovery_ResetsStallInsteadOfEscalating()
    {
        var source = new FakePoolHealthSource
        {
            Status = new WorkerPoolStatus(2, 0, 1, null),
            Candidates = [Item()],
            AdvanceLastSpawnOnRecovery = true,
        };
        var watchdog = new WorkerPoolHealthWatchdog(
            source,
            source,
            StandardOptions(maxAttempts: 1),
            NullLogger<WorkerPoolHealthWatchdog>.Instance,
            _projects,
            agents: new AgentRegistry([new DummyAgentRunner(AgentKind.Claude)]),
            webhooks: _webhooks,
            timeProvider: _time);

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Contains(_webhooks.Events, e => e.Event == "worker_pool.stalled");
        Assert.DoesNotContain(_webhooks.Events, e => e.Event == "worker_pool.restart_required");
    }

    [Fact]
    public async Task WaitingForQuotaResetCandidate_TriggersQuotaRecoverySweep()
    {
        var source = new FakePoolHealthSource
        {
            Status = new WorkerPoolStatus(2, 0, 1, null),
            Candidates = [Item() with { State = WorkItemState.WaitingForQuotaReset }],
        };
        var quotaRecovery = new RecordingQuotaRecovery();
        var watchdog = new WorkerPoolHealthWatchdog(
            source,
            source,
            StandardOptions(),
            NullLogger<WorkerPoolHealthWatchdog>.Instance,
            _projects,
            agents: new AgentRegistry([new DummyAgentRunner(AgentKind.Claude)]),
            quotaRecovery: quotaRecovery,
            webhooks: _webhooks,
            timeProvider: _time);

        await watchdog.RunOnceAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, quotaRecovery.SweepCalls);
        Assert.Equal(0, source.EnqueueCalls);
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
        => new(
            _orchestrator,
            _orchestrator,
            opts,
            NullLogger<WorkerPoolHealthWatchdog>.Instance,
            _projects,
            agents: new AgentRegistry([new DummyAgentRunner(AgentKind.Claude)]),
            webhooks: _webhooks,
            timeProvider: _time);

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

    private sealed class NoopPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;

        public NoopPipeline(IWorkItemStore store) => _store = store;

        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
            => _store.UpdateAsync(item.With(WorkItemState.Done), ct);
    }

    private sealed class BlockingPipeline : IPipelineRunner
    {
        private readonly IWorkItemStore _store;

        public BlockingPipeline(IWorkItemStore store) => _store = store;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            await _store.UpdateAsync(item.With(WorkItemState.Working), CancellationToken.None);
            Started.SetResult();
            try
            {
                await Release.Task;
                var current = await _store.GetAsync(item.Id, CancellationToken.None) ?? item;
                await _store.UpdateAsync(current.With(WorkItemState.Done), CancellationToken.None);
            }
            finally
            {
                Exited.SetResult();
            }
        }
    }

    private sealed class FakePoolHealthSource : IWorkerPoolHealthSource, IAgentCapacitySnapshot
    {
        public bool DispatchPaused { get; set; }
        public bool HasAgentCapacity { get; set; } = true;
        public bool AdvanceLastSpawnOnRecovery { get; set; }
        public WorkerPoolStatus Status { get; set; } = new(2, 0, 0, null);
        public IReadOnlyList<WorkItem> Candidates { get; set; } = [];
        public int EnqueueCalls { get; private set; }

        public bool IsDispatchPaused => DispatchPaused;

        public Task<WorkerPoolStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(Status);

        public Task<IReadOnlyList<WorkItem>> ListPoolHealthCandidatesAsync(
            int scanLimit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WorkItem>>(Candidates.Take(scanLimit).ToList());

        public Task<int> TriggerDispatchRecoveryAsync(
            IEnumerable<WorkItemId> candidateIds,
            CancellationToken ct)
        {
            var ids = candidateIds.ToList();
            EnqueueCalls += ids.Count;
            if (AdvanceLastSpawnOnRecovery)
                Status = Status with { LastSpawnAt = DateTimeOffset.UnixEpoch.AddMinutes(1) };
            return Task.FromResult(ids.Count);
        }

        public bool HasCapacity(AgentKind agent) => HasAgentCapacity;
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

    private sealed class RecordingQuotaRecovery : IWorkerPoolQuotaRecovery
    {
        public int SweepCalls { get; private set; }

        public Task RunWatchdogRecoverySweepAsync(CancellationToken ct)
        {
            SweepCalls++;
            return Task.CompletedTask;
        }
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

    private sealed class FakeAvailabilityRegistry : IAgentAvailabilityRegistry
    {
        private readonly bool _available;

        public FakeAvailabilityRegistry(bool available) => _available = available;

        public AgentAvailability GetAvailability(AgentKind kind) =>
            new(_available, _available ? null : "unavailable", null);

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

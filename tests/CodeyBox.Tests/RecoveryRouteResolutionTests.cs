using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Projects;
using CodeyBox.Webhooks;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Recovery re-pickups must dispatch on the same class route as a first pickup.
/// <see cref="WorkItem.ModelId"/> and <see cref="WorkItem.ReasoningMode"/> are
/// runtime-only routing selections with no <c>work_items</c> columns, so any
/// pickup that skips class routing re-dispatches with a null model: a silent
/// downgrade to the agent default, or an outright failure where the agent has
/// no default-model fallback. These tests pin the incident shape (items with
/// <c>agent_class_id</c> NULL routing via <c>Project.DefaultAgentClass</c>)
/// across the dead-worker and restart recovery paths, plus the racy pickups
/// that observe a mid-flight state before recovery rewrites it.
/// </summary>
public sealed class RecoveryRouteResolutionTests : IDisposable
{
    private const string RoutedModelId = "claude-opus-4-7";
    private const string RoutedReasoningMode = "high";

    private static readonly ProjectId TestProjectId = new("recovery-route-project");
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromSeconds(30);

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"codeybox-recovery-route-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly TestSink _sink = new();
    private readonly Serilog.Core.Logger _auditLogger;
    private readonly IDisposable _auditScope;

    public RecoveryRouteResolutionTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _auditLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .CreateLogger();
        // Scoped (AsyncLocal) capture: immune to concurrent suites rebuilding
        // the process-global logger, flows into the pickup under test.
        _auditScope = AuditLog.PushScopedLogger(_auditLogger);
    }

    public void Dispose()
    {
        _auditScope.Dispose();
        _auditLogger.Dispose();
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    /// <summary>
    /// Single-member class: the only eligible member, so every (re-)resolution
    /// deterministically selects it. The member pins both a model and a
    /// reasoning mode, matching the incident's routed selection.
    /// </summary>
    private static AgentClassRouter BuildRouter() => new(
        [
            new AgentClass
            {
                Id = "frontier",
                DisplayName = "Frontier",
                Members =
                [
                    new AgentMembership
                    {
                        Agent = AgentKind.Claude,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 100,
                        ModelId = RoutedModelId,
                        ReasoningMode = RoutedReasoningMode,
                    },
                ],
            },
        ],
        [new StaticQuotaProbe(AgentKind.Claude, availablePct: 100)],
        new QuotaRouterOptions { MinQuotaPct = 10 },
        NullLogger<AgentClassRouter>.Instance);

    private static IProjectRepository BuildProjects() => new InMemoryProjectRepository(new Project
    {
        Id = TestProjectId,
        DisplayName = "Recovery route",
        RepositoryUrl = "https://example.invalid/repo.git",
        DefaultAgentClass = "frontier",
    });

    /// <summary>
    /// An item as the store holds it after a first pickup: the chosen agent is
    /// persisted, but the runtime-only ModelId/ReasoningMode were lost on the
    /// store round-trip. <c>AgentClassId</c> stays NULL so routing resolves via
    /// <see cref="Project.DefaultAgentClass"/>, the incident configuration.
    /// </summary>
    private WorkItem StoredAfterFirstPickup(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = TestProjectId,
        Title = "recovery route",
        Prompt = "p",
        State = state,
        Agent = AgentKind.Claude,
        AgentInstanceId = "claude",
        AgentClassId = null,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    private OrchestratorService BuildOrchestrator(
        InMemoryTaskQueue queue,
        CapturingPipelineRunner pipeline,
        AgentClassRouter router,
        RecoveryCompletion? completion = null) =>
        new(
            queue,
            _store,
            pipeline,
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            projects: BuildProjects(),
            startupRecoveryCompletion: completion);

    /// <summary>
    /// Starts the orchestrator empty and waits for its startup recovery pass
    /// to finish, so a mid-flight state seeded afterwards is observed by the
    /// pickup exactly as a pickup racing recovery would observe it.
    /// </summary>
    private static async Task StartPastRecoveryAsync(
        OrchestratorService svc, RecoveryCompletion completion)
    {
        await svc.StartAsync(CancellationToken.None);
        await completion.Completed.WaitAsync(SyncTimeout);
    }

    private void AssertDispatchedOnRoutedMember(WorkItemId id, CapturingPipelineRunner pipeline)
    {
        Assert.NotNull(pipeline.LastItem);
        Assert.Equal(AgentKind.Claude, pipeline.LastItem!.Agent);
        Assert.Equal(RoutedModelId, pipeline.LastItem.ModelId);
        Assert.Equal(RoutedReasoningMode, pipeline.LastItem.ReasoningMode);
        var evt = Assert.Single(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "EventName"), "quota_router.scored", StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "WorkItemId"), id.ToString(), StringComparison.Ordinal));
        Assert.Equal(RoutedModelId, GetScalar<string>(evt, "ModelId"));
    }

    [Fact]
    public async Task RecoveryPickup_InWorkingState_RoutesClassMember()
    {
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipelineRunner(_store);
        var completion = new RecoveryCompletion();
        var svc = BuildOrchestrator(queue, pipeline, BuildRouter(), completion);
        await StartPastRecoveryAsync(svc, completion);

        var item = StoredAfterFirstPickup(WorkItemState.Working);
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await pipeline.RunAttempted.Task.WaitAsync(SyncTimeout);
        await svc.StopAsync(CancellationToken.None);

        AssertDispatchedOnRoutedMember(item.Id, pipeline);
    }

    [Fact]
    public async Task RecoveryPickup_InAuditingState_RoutesClassMember()
    {
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipelineRunner(_store);
        var completion = new RecoveryCompletion();
        var svc = BuildOrchestrator(queue, pipeline, BuildRouter(), completion);
        await StartPastRecoveryAsync(svc, completion);

        var item = StoredAfterFirstPickup(WorkItemState.Auditing);
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await pipeline.RunAttempted.Task.WaitAsync(SyncTimeout);
        await svc.StopAsync(CancellationToken.None);

        AssertDispatchedOnRoutedMember(item.Id, pipeline);
    }

    [Fact]
    public async Task RecoveryPickup_InMergingState_RoutesClassMember()
    {
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipelineRunner(_store);
        var completion = new RecoveryCompletion();
        var svc = BuildOrchestrator(queue, pipeline, BuildRouter(), completion);
        await StartPastRecoveryAsync(svc, completion);

        var item = StoredAfterFirstPickup(WorkItemState.Merging);
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await pipeline.RunAttempted.Task.WaitAsync(SyncTimeout);
        await svc.StopAsync(CancellationToken.None);

        AssertDispatchedOnRoutedMember(item.Id, pipeline);
    }

    [Fact]
    public async Task DeadWorkerRecovery_ThenPickup_DispatchesRoutedModel()
    {
        var item = StoredAfterFirstPickup(WorkItemState.Auditing);
        await _store.CreateAsync(item);

        var registry = new SqliteWorkerRegistry(_dbPath);
        try
        {
            await registry.RegisterAsync(new WorkerRegistration
            {
                WorkerId = "dead-worker-1",
                HostName = "host",
                ProcessId = 1,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                LastHeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                CurrentWorkItemId = item.Id.ToString(),
            });

            var reaper = new DeadWorkerReaper(
                registry,
                _store,
                new InMemoryTaskQueue(),
                new DeadWorkerOptions
                {
                    HeartbeatInterval = TimeSpan.FromSeconds(5),
                    DeadWorkerThreshold = TimeSpan.FromSeconds(15),
                    CheckInterval = TimeSpan.FromMinutes(60),
                    MaxRecoveryAttempts = 2,
                },
                NullLogger<DeadWorkerReaper>.Instance,
                new CapturingWebhookDispatcher());

            await reaper.RunOnceAsync(CancellationToken.None);
        }
        finally
        {
            registry.Dispose();
        }

        var recovered = await _store.GetAsync(item.Id);
        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.WorkComplete, recovered!.State);

        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipelineRunner(_store);
        var svc = BuildOrchestrator(queue, pipeline, BuildRouter());
        await svc.StartAsync(CancellationToken.None);

        await pipeline.RunAttempted.Task.WaitAsync(SyncTimeout);
        await svc.StopAsync(CancellationToken.None);

        AssertDispatchedOnRoutedMember(item.Id, pipeline);
    }

    [Fact]
    public async Task RestartRecovery_ThenPickup_DispatchesRoutedModel()
    {
        var item = StoredAfterFirstPickup(WorkItemState.Auditing);
        await _store.CreateAsync(item);

        // Restart stranded-item sweep: no live worker row owns the item, so it
        // is requeued at its durable resume point, as on orchestrator restart.
        var registry = new SqliteWorkerRegistry(_dbPath);
        try
        {
            var reaper = new DeadWorkerReaper(
                registry,
                _store,
                new InMemoryTaskQueue(),
                new DeadWorkerOptions
                {
                    HeartbeatInterval = TimeSpan.FromSeconds(5),
                    DeadWorkerThreshold = TimeSpan.FromSeconds(15),
                    CheckInterval = TimeSpan.FromMinutes(60),
                    MaxRecoveryAttempts = 2,
                },
                NullLogger<DeadWorkerReaper>.Instance,
                new CapturingWebhookDispatcher());

            await reaper.SweepStrandedItemsAsync(CancellationToken.None);
        }
        finally
        {
            registry.Dispose();
        }

        var recovered = await _store.GetAsync(item.Id);
        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.WorkComplete, recovered!.State);

        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipelineRunner(_store);
        var svc = BuildOrchestrator(queue, pipeline, BuildRouter());
        await svc.StartAsync(CancellationToken.None);

        await pipeline.RunAttempted.Task.WaitAsync(SyncTimeout);
        await svc.StopAsync(CancellationToken.None);

        AssertDispatchedOnRoutedMember(item.Id, pipeline);
    }

    /// <summary>
    /// Checkpoint-bound resumes keep their exact route: pickup must NOT
    /// re-route (that would reserve one member while the pipeline restores
    /// another member's transcript), so no routing event fires here. The
    /// pipeline restores the checkpoint's own ModelId/ReasoningMode instead.
    /// </summary>
    [Fact]
    public async Task CheckpointBoundResume_SkipsPickupRouting()
    {
        var queue = new InMemoryTaskQueue();
        var pipeline = new CapturingPipelineRunner(_store);
        var completion = new RecoveryCompletion();
        var svc = BuildOrchestrator(queue, pipeline, BuildRouter(), completion);
        await StartPastRecoveryAsync(svc, completion);

        var item = StoredAfterFirstPickup(WorkItemState.Working) with
        {
            // Legacy Git-only checkpoint: a coherent recovery boundary without
            // typed agent-turn metadata.
            PreemptCheckpoint = new string('b', 40),
        };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        await pipeline.RunAttempted.Task.WaitAsync(SyncTimeout);
        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(pipeline.LastItem);
        Assert.Equal(AgentKind.Claude, pipeline.LastItem!.Agent);
        Assert.DoesNotContain(_sink.Events, e =>
            string.Equals(GetScalar<string>(e, "EventName"), "quota_router.scored", StringComparison.Ordinal)
            && string.Equals(GetScalar<string>(e, "WorkItemId"), item.Id.ToString(), StringComparison.Ordinal));
    }

    private sealed class StaticQuotaProbe(AgentKind kind, double availablePct) : IAgentQuotaProbe
    {
        public AgentKind Kind { get; } = kind;

        public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct) =>
            Task.FromResult(new AgentQuotaSnapshot { AvailablePct = availablePct });
    }

    private sealed class RecoveryCompletion : IStartupInitialRecoverySink
    {
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _tcs.Task;

        public void MarkInitialRecoveryCompleted() => _tcs.TrySetResult();
    }

    private sealed class CapturingPipelineRunner(IWorkItemStore store) : IPipelineRunner
    {
        public TaskCompletionSource RunAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkItem? LastItem { get; private set; }

        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            LastItem = item;
            await store.UpdateAsync(item.With(WorkItemState.Done), ct);
            RunAttempted.TrySetResult();
        }
    }

    private static T? GetScalar<T>(LogEvent evt, string key)
    {
        if (!evt.Properties.TryGetValue(key, out var value))
            return default;
        if (value is ScalarValue sv && sv.Value is T typed)
            return typed;
        if (typeof(T) == typeof(int) && value is ScalarValue { Value: long l })
            return (T)(object)(int)l;
        return default;
    }
}

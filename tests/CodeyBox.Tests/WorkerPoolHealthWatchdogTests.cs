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
        Assert.NotNull(stalled.Details);
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

    private WorkerPoolHealthWatchdog BuildWatchdog(WorkerPoolHealthWatchdogOptions opts)
        => new(
            _orchestrator,
            opts,
            NullLogger<WorkerPoolHealthWatchdog>.Instance,
            _projects,
            agents: new AgentRegistry([new DummyAgentRunner(AgentKind.Claude)]),
            webhooks: _webhooks,
            timeProvider: _time);

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

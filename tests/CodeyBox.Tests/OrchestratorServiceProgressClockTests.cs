using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;

namespace CodeyBox.Tests;

public sealed class OrchestratorServiceProgressClockTests : IDisposable
{
    // Positive event-driven poll-deadlines below use this as a backstop only:
    // the awaited state (clock stamp) WILL be reached on a correct run, so the
    // timeout just needs headroom for a correct-but-starved dispatch on the
    // co-resident capped full-suite host. A real regression still fails because
    // the clock never gets stamped. Same class as commits 0df5ee7 / 47661bd.
    private static readonly TimeSpan StarvationBackstopTimeout = TimeSpan.FromSeconds(60);

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-orch-clock-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;

    public OrchestratorServiceProgressClockTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem NewItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
    };

    [Fact]
    public async Task WorkerCompletion_StampsProgressClock()
    {
        var item = NewItem();
        await _store.CreateAsync(item);

        var clock = new OrchestratorProgressClock();
        var queue = new InMemoryTaskQueue();
        await queue.EnqueueAsync(item.Id);

        using var service = new OrchestratorService(
            queue,
            _store,
            new NoOpPipelineRunner(),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            progressClock: clock);

        using var stopCts = new CancellationTokenSource(StarvationBackstopTimeout);
        await service.StartAsync(CancellationToken.None);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StarvationBackstopTimeout && clock.LastTransition == DateTimeOffset.MinValue)
            await Task.Delay(50);

        Assert.True(clock.LastTransition > DateTimeOffset.MinValue,
            "Progress clock should have been stamped after work item completion");

        await service.StopAsync(stopCts.Token);
    }

    [Fact]
    public async Task StartupReaperInit_StampsProgressClock()
    {
        var clock = new OrchestratorProgressClock();
        var queue = new InMemoryTaskQueue();

        var reaper = new DeadWorkerReaper(
            _registry, _store, queue,
            new DeadWorkerOptions
            {
                HeartbeatInterval = TimeSpan.FromSeconds(5),
                DeadWorkerThreshold = TimeSpan.FromSeconds(15),
            },
            NullLogger<DeadWorkerReaper>.Instance);

        using var service = new OrchestratorService(
            queue,
            _store,
            new NoOpPipelineRunner(),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            progressClock: clock,
            reaper: reaper);

        using var stopCts = new CancellationTokenSource(StarvationBackstopTimeout);
        await service.StartAsync(CancellationToken.None);

        // Reaper-init stamp happens synchronously inside ExecuteAsync before
        // the worker loop blocks on DequeueAsync. Wait briefly to let it through.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < StarvationBackstopTimeout && clock.LastTransition == DateTimeOffset.MinValue)
            await Task.Delay(20);

        Assert.True(clock.LastTransition > DateTimeOffset.MinValue,
            "Progress clock should be stamped during startup reaper init");

        await service.StopAsync(stopCts.Token);
    }

    private sealed class NoOpPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
            => Task.CompletedTask;
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Verifies that the orchestrator runs the dead-worker reaper synchronously
/// at startup (before the worker pool begins picking up items), so that items
/// orphaned by a previous crash are recovered and re-queued before new work
/// is dispatched.
/// </summary>
[Collection("Pipeline integration")]
public sealed class StartupReaperTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-startreaper-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;

    public StartupReaperTests()
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

    [Fact]
    public async Task StartupReaper_RecoversMidFlightItem_BeforeWorkerPickup()
    {
        // Arrange: an item left in Working state from a previous crash, with a
        // corresponding stale worker row.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Working,
        };
        await _store.CreateAsync(item);

        var staleReg = new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "crashed-host",
            ProcessId = 9999,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1),
            CurrentWorkItemId = item.Id.ToString(),
        };
        await _registry.RegisterAsync(staleReg);

        // Build a pipeline that marks items Done immediately.
        var queue = new InMemoryTaskQueue();
        var pipeline = new ImmediateDonePipeline(_store);
        var cancellations = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var deadWorkerOpts = new DeadWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            DeadWorkerThreshold = TimeSpan.FromSeconds(15),
            MaxRecoveryAttempts = 2,
        };
        var reaper = new DeadWorkerReaper(
            _registry, _store, queue, deadWorkerOpts,
            NullLogger<DeadWorkerReaper>.Instance);

        using var svc = new OrchestratorService(
            queue, _store, pipeline, cancellations, opts,
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: _registry,
            deadWorkerOpts: deadWorkerOpts,
            reaper: reaper);

        await svc.StartAsync(CancellationToken.None);

        // Poll until the item is Done (the reaper recovered it → Queued, then
        // the orchestrator ran it through the pipeline).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        WorkItem? final = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            final = await _store.GetAsync(item.Id);
            if (final?.State == WorkItemState.Done) break;
            await Task.Delay(30);
        }

        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final.State);
        // RecoveryAttempts == 1 proves the startup reaper ran and incremented the counter.
        Assert.Equal(1, final.RecoveryAttempts);
    }

    [Fact]
    public async Task StartupReaper_WithNoStaleWorkers_OrchestratesNormally()
    {
        // No stale workers — just a regular Queued item. Should run cleanly.
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(item);

        var queue = new InMemoryTaskQueue();
        await queue.EnqueueAsync(item.Id);

        var pipeline = new ImmediateDonePipeline(_store);
        var cancellations = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var deadWorkerOpts = new DeadWorkerOptions();
        var reaper = new DeadWorkerReaper(
            _registry, _store, queue, deadWorkerOpts,
            NullLogger<DeadWorkerReaper>.Instance);

        using var svc = new OrchestratorService(
            queue, _store, pipeline, cancellations, opts,
            NullLogger<OrchestratorService>.Instance,
            reaper: reaper);

        await svc.StartAsync(CancellationToken.None);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        WorkItem? final = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            final = await _store.GetAsync(item.Id);
            if (final?.State == WorkItemState.Done) break;
            await Task.Delay(30);
        }

        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Done, final.State);
        Assert.Equal(0, final.RecoveryAttempts);
    }
}

/// <summary>Pipeline that immediately transitions every item to Done.</summary>
internal sealed class ImmediateDonePipeline : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    public ImmediateDonePipeline(IWorkItemStore store) => _store = store;

    public async Task RunAsync(WorkItem item, CancellationToken ct)
        => await _store.UpdateAsync(item.With(WorkItemState.Done), ct);
}

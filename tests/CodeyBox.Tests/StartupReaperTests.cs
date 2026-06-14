using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
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
        try { File.Delete(_dbPath); } catch { /* best-effort temp file cleanup */ }
    }

    [Fact]
    public async Task StartupReaper_FailsCrashedWorkingItem_BeforeWorkerPickup()
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

        // Build a pipeline that would mark items Done immediately if the
        // crashed item were incorrectly requeued.
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

        // Poll until the startup reaper marks the non-preempted Working item
        // Failed. It must not enter the worker pool again.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        WorkItem? final = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            final = await _store.GetAsync(item.Id);
            if (final?.State == WorkItemState.Failed) break;
            await Task.Delay(30);
        }

        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.Equal(WorkItemState.Failed, final.State);
        Assert.Contains("without a preempt checkpoint", final.LastError);
        Assert.Empty(pipeline.Executed);
        // RecoveryAttempts == 1 proves the startup reaper ran and incremented the counter.
        Assert.Equal(1, final.RecoveryAttempts);
    }

    [Fact]
    public async Task StartupReplay_QueuedItemInsertedBeforeBoot_IsPickedUpByWorker()
    {
        // No producer has touched the in-memory queue: this row simulates an
        // out-of-band DB update or a Queued item left behind before process boot.
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
        Assert.Equal(0, queue.Count);

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

    [Fact]
    public async Task StartupReaper_AuditPassedWorkerRow_RedispatchesAndDoesNotLeakWorkerSlot()
    {
        var auditPassed = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "audit passed",
            Prompt = "p",
            State = WorkItemState.AuditPassed,
        };
        var queued = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "queued",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(auditPassed);
        await _store.CreateAsync(queued);
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "crashed-host",
            ProcessId = 9999,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1),
            CurrentWorkItemId = auditPassed.Id.ToString(),
        });

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

        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        WorkerPoolStatus? status = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var auditPassedAfter = await _store.GetAsync(auditPassed.Id);
            var queuedAfter = await _store.GetAsync(queued.Id);
            status = await svc.GetStatusAsync(CancellationToken.None);
            if (auditPassedAfter?.State == WorkItemState.Done
                && queuedAfter?.State == WorkItemState.Done
                && status.CurrentlyRunning == 0)
                break;
            await Task.Delay(30);
        }

        status ??= await svc.GetStatusAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(auditPassed.Id);
        var nextItem = await _store.GetAsync(queued.Id);
        Assert.Equal(WorkItemState.Done, recovered!.State);
        Assert.Equal(WorkItemState.Done, nextItem!.State);
        Assert.Equal(0, recovered.RecoveryAttempts);
        Assert.Equal(WorkItemState.AuditPassed, pipeline.EntryStates[auditPassed.Id]);
        Assert.Contains(queued.Id, pipeline.Executed);
        Assert.Equal(0, status.CurrentlyRunning);
    }

    [Fact]
    public async Task StartupReplay_DoesNotReenqueueItemAlreadyRecoveredByReaper()
    {
        var auditPassed = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "audit passed",
            Prompt = "p",
            State = WorkItemState.AuditPassed,
        };
        await _store.CreateAsync(auditPassed);
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = Guid.NewGuid().ToString(),
            HostName = "crashed-host",
            ProcessId = 9999,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1),
            CurrentWorkItemId = auditPassed.Id.ToString(),
        });

        var queue = new InMemoryTaskQueue();
        var deadWorkerOpts = new DeadWorkerOptions
        {
            DeadWorkerThreshold = TimeSpan.FromSeconds(15),
            MaxRecoveryAttempts = 2,
        };
        var reaper = new DeadWorkerReaper(
            _registry, _store, queue, deadWorkerOpts,
            NullLogger<DeadWorkerReaper>.Instance);
        using var svc = new OrchestratorService(
            queue, _store, new ImmediateDonePipeline(_store),
            new CancellationRegistry(CancellationToken.None),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            reaper: reaper);

        await reaper.RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, queue.Count);

        await svc.ReplayPendingForTestAsync(CancellationToken.None);

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task PeriodicReaper_NonRedispatchedCurrentWorker_ReleasesSlotAndDispatchContinues()
    {
        var terminalizing = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "terminalizing",
            Prompt = "p",
            State = WorkItemState.Queued,
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
        };
        var next = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("p"),
            Title = "next",
            Prompt = "p",
            State = WorkItemState.Queued,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await _store.CreateAsync(terminalizing);
        await _store.CreateAsync(next);

        var queue = new InMemoryTaskQueue();
        var pipeline = new BlockingTerminalPipeline(_store, terminalizing.Id);
        var cancellations = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1 };
        var deadWorkerOpts = new DeadWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromHours(1),
            DeadWorkerThreshold = TimeSpan.Zero,
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
        try
        {
            await pipeline.Blocked.WaitAsync(TimeSpan.FromSeconds(10));
            var before = await svc.GetStatusAsync(CancellationToken.None);
            Assert.Equal(1, before.CurrentlyRunning);

            await reaper.RunOnceAsync(CancellationToken.None);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            WorkerPoolStatus? status = null;
            WorkItem? nextAfter = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                status = await svc.GetStatusAsync(CancellationToken.None);
                nextAfter = await _store.GetAsync(next.Id);
                if (nextAfter?.State == WorkItemState.Done && status.CurrentlyRunning == 0)
                    break;
                await Task.Delay(30);
            }

            status ??= await svc.GetStatusAsync(CancellationToken.None);
            nextAfter ??= await _store.GetAsync(next.Id);
            Assert.Equal(WorkItemState.Done, nextAfter!.State);
            Assert.Equal(0, status.CurrentlyRunning);
            Assert.Contains(next.Id, pipeline.Executed);
        }
        finally
        {
            pipeline.ReleaseBlocked();
            await svc.StopAsync(CancellationToken.None);
        }
    }
}

/// <summary>Pipeline that immediately transitions every item to Done.</summary>
internal sealed class ImmediateDonePipeline : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly ConcurrentBag<WorkItemId> _executed = new();
    private readonly ConcurrentDictionary<WorkItemId, WorkItemState> _entryStates = new();
    public IReadOnlyCollection<WorkItemId> Executed => _executed;
    public IReadOnlyDictionary<WorkItemId, WorkItemState> EntryStates => _entryStates;

    public ImmediateDonePipeline(IWorkItemStore store) => _store = store;

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        _executed.Add(item.Id);
        _entryStates[item.Id] = item.State;
        await _store.UpdateAsync(item.With(WorkItemState.Done) with { RecoveryAttempts = 0 }, ct);
    }
}

internal sealed class BlockingTerminalPipeline : IPipelineRunner
{
    private readonly IWorkItemStore _store;
    private readonly WorkItemId _blockedId;
    private readonly ConcurrentBag<WorkItemId> _executed = new();
    private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BlockingTerminalPipeline(IWorkItemStore store, WorkItemId blockedId)
    {
        _store = store;
        _blockedId = blockedId;
    }

    public IReadOnlyCollection<WorkItemId> Executed => _executed;
    public Task Blocked => _blocked.Task;
    public void ReleaseBlocked() => _releaseBlocked.TrySetResult();

    public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
    {
        _executed.Add(item.Id);
        await _store.UpdateAsync(item.With(WorkItemState.Done), ct);

        if (item.Id == _blockedId)
        {
            _blocked.TrySetResult();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, hostShutdownToken);
            await _releaseBlocked.Task.WaitAsync(linked.Token);
        }
    }
}

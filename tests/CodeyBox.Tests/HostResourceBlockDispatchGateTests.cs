using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Dispatch-loop gate tests for <see cref="HostResourceBlockTracker"/>: while a
/// host-wide resource block (disk below the guard threshold) is active the
/// pickup loop must not select work — the 2026-11-21 incident churned
/// pick-up/defer cycles that fed every eligible agent's circuit breaker — and
/// must resume on its own once the sampler reports the mount recovered.
/// </summary>
[Collection("Background service timing")]
public sealed class HostResourceBlockDispatchGateTests : IDisposable
{
    private static readonly TimeSpan DispatchWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan NoDispatchQuietPeriod = TimeSpan.FromMilliseconds(500);

    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-hostblock-");
    private readonly string _dbPath;
    private readonly SqliteWorkItemStore _store;

    public HostResourceBlockDispatchGateTests()
    {
        _dbPath = _scratch.DbPath("hostblock.db");
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
        _scratch.Dispose();
    }

    [Fact]
    public async Task DiskBlock_HoldsPickup_AndResumesWhenSamplerReportsRecovery()
    {
        var time = new ControllableTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));
        var freeBytes = 100L;
        var tracker = new HostResourceBlockTracker(
            time,
            diskSampler: () => [new DiskGuardSample("/data", freeBytes, ThresholdBytes: 1_000)]);
        // Simulates the block a disk-guard deferral recorded on an earlier
        // pickup: host-wide, dispatch-blocking, recheck far in the future.
        tracker.RecordDiskBlock("/data", freeBytes: 100, thresholdBytes: 1_000, TimeSpan.FromHours(1));

        var queue = new SignalTaskQueue();
        var pipeline = new EnteredPipeline();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            hostResourceBlocks: tracker,
            timeProvider: time);

        await svc.StartAsync(CancellationToken.None);

        var item = MakeItem();
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        // While the block holds, the dispatcher must not pick the item up —
        // the hold sleeps on the injected clock, so real time cannot advance
        // it into a pickup.
        await Task.Delay(NoDispatchQuietPeriod);
        Assert.False(pipeline.HasEntered(item.Id),
            "Dispatch must not pick up work while a host resource block is active.");
        Assert.NotNull(tracker.GetBlockingBlock());

        // Space returns: the next re-probe (each capped hold sleep) releases
        // the block and the queued item dispatches without any operator action.
        freeBytes = 5_000;
        var entered = false;
        for (var i = 0; i < 8 && !entered; i++)
        {
            time.Advance(TimeSpan.FromSeconds(15));
            entered = await pipeline.WaitForEnteredAsync(item.Id, TimeSpan.FromMilliseconds(250));
        }

        Assert.True(entered, "Dispatch must resume automatically once the host resource recovers.");
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ProvisioningBlock_DoesNotHoldPickup()
    {
        // Item/project-scoped provisioning deferrals are surfaced for status
        // visibility but must never stall the whole queue.
        var tracker = new HostResourceBlockTracker(new ControllableTimeProvider(DateTimeOffset.UtcNow));
        tracker.RecordProvisioningBlock("incus", "create", "exclusive-share-held", "held", TimeSpan.FromMinutes(30));
        Assert.Null(tracker.GetBlockingBlock());

        var queue = new SignalTaskQueue();
        var pipeline = new EnteredPipeline();
        using var registry = new CancellationRegistry(CancellationToken.None);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance,
            hostResourceBlocks: tracker);

        await svc.StartAsync(CancellationToken.None);

        var item = MakeItem();
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);

        Assert.True(await pipeline.WaitForEnteredAsync(item.Id, DispatchWaitTimeout));
        await svc.StopAsync(CancellationToken.None);
    }

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Queued,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Minimal <see cref="ITaskQueue"/>: a channel of dispatch signals where a
    /// signal is either an item id or a generic wake. The orchestrator only
    /// uses signals as "re-check the store" kicks.
    /// </summary>
    private sealed class SignalTaskQueue : ITaskQueue
    {
        private readonly Channel<WorkItemId?> _signals = Channel.CreateUnbounded<WorkItemId?>();

        public int Count => _signals.Reader.Count;

        public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default) =>
            _signals.Writer.WriteAsync(id, ct);

        public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default) =>
            _signals.Writer.WriteAsync(null, ct);

        public async ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default)
        {
            try { return await _signals.Reader.ReadAsync(ct); }
            catch (ChannelClosedException) { return null; }
        }

        public async ValueTask<bool> DequeueDispatchSignalAsync(CancellationToken ct = default)
        {
            try { await _signals.Reader.ReadAsync(ct); return true; }
            catch (ChannelClosedException) { return false; }
        }
    }

    /// <summary>
    /// Minimal <see cref="IPipelineRunner"/> that records pickup and completes
    /// immediately — the gate under test is upstream of the pipeline.
    /// </summary>
    private sealed class EnteredPipeline : IPipelineRunner
    {
        private readonly ConcurrentDictionary<WorkItemId, byte> _entered = new();
        private readonly ConcurrentDictionary<WorkItemId, TaskCompletionSource> _signals = new();

        public bool HasEntered(WorkItemId id) => _entered.ContainsKey(id);

        public Task<bool> WaitForEnteredAsync(WorkItemId id, TimeSpan timeout) =>
            WaitForSignalAsync(_signals.GetOrAdd(id, static _ => NewSignal()), timeout);

        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            _entered.TryAdd(item.Id, 0);
            _signals.GetOrAdd(item.Id, static _ => NewSignal()).TrySetResult();
            return Task.CompletedTask;
        }

        private static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task<bool> WaitForSignalAsync(TaskCompletionSource signal, TimeSpan timeout)
        {
            var completed = await Task.WhenAny(signal.Task, Task.Delay(timeout));
            return completed == signal.Task;
        }
    }
}

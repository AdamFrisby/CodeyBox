using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Serilog;
using Serilog.Events;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the 2026-09-07 worker-pool runaway: a work item in
/// <see cref="WorkItemState.Working"/> was picked up ~500 times/second, each
/// worker deregistering as a 'clean shutdown' with nothing naming the cause,
/// growing the log ~6 MB/minute with no backoff and no self-correction.
/// </summary>
[Collection("Background service timing")]
public sealed class WorkerSpinGuardTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-spin-guard-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public WorkerSpinGuardTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeWorkingItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Working,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    /// <summary>
    /// A pickup that returns without advancing the item and without deferring
    /// must back off with escalating delays and then stop (Failed) instead of
    /// re-dispatching at CPU speed.
    /// </summary>
    [Fact]
    public async Task RepeatedNoProgressPickups_BackOffAndEventuallyStop()
    {
        // A sentinel Queued item proves startup recovery has finished before
        // the stuck Working item appears: startup recovery fails Working
        // items without a checkpoint as crash cases, so the spin scenario
        // (an item that becomes Working mid-process, as in the incident) is
        // seeded only after the dispatcher is demonstrably alive.
        var sentinel = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "sentinel",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(sentinel);

        var spawnCount = 0;
        var deferralDelays = new List<(WorkItemId Id, TimeSpan Delay)>();
        var deferralLock = new object();
        var pipeline = new SentinelCompletingPipelineRunner(sentinel.Id, _store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 1,
            NoProgressBackoffBase = TimeSpan.FromMilliseconds(10),
            NoProgressBackoffMax = TimeSpan.FromMilliseconds(50),
            MaxNoProgressRedispatches = 3,
            OnWorkerSpawned = () => Interlocked.Increment(ref spawnCount),
            OnDeferredForTest = (id, delay) =>
            {
                lock (deferralLock) { deferralDelays.Add((id, delay)); }
            },
        };
        using var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(
            queue, _store, pipeline, registry, opts,
            NullLogger<OrchestratorService>.Instance);
        // Requeue immediately so the test observes consecutive pickups without
        // waiting out wall-clock backoff delays; the scheduled delays are still
        // recorded via OnDeferredForTest for the escalation assertion.
        svc.DeferredRequeueDelayForTest = (_, _, _) => Task.CompletedTask;

        await svc.StartAsync(CancellationToken.None);
        WorkItemId itemId;
        try
        {
            var startupDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < startupDeadline)
            {
                var s = await _store.GetAsync(sentinel.Id);
                if (s?.State == WorkItemState.Done)
                    break;
                await Task.Delay(20);
            }
            Assert.Equal(WorkItemState.Done, (await _store.GetAsync(sentinel.Id))?.State);

            var item = MakeWorkingItem();
            itemId = item.Id;
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            WorkItem? current = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                current = await _store.GetAsync(item.Id);
                if (current?.State == WorkItemState.Failed)
                    break;
                await Task.Delay(20);
            }

            Assert.Equal(WorkItemState.Failed, current?.State);
            Assert.Contains("no progress", current!.LastError);
            Assert.Contains("pipeline-ran", current.LastError);

            // One sentinel pickup plus exactly MaxNoProgressRedispatches
            // spin pickups, then silence: the Failed write excludes the item
            // from pickup, so the loop stops.
            Assert.Equal(4, Volatile.Read(ref spawnCount));
            List<TimeSpan> delays;
            lock (deferralLock)
            {
                delays = deferralDelays
                    .Where(d => d.Id == itemId)
                    .Select(d => d.Delay)
                    .ToList();
            }
            Assert.Equal(
                [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20)],
                delays);

            var settled = Volatile.Read(ref spawnCount);
            await Task.Delay(500);
            Assert.Equal(settled, Volatile.Read(ref spawnCount));
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The immediate-exit condition must be logged with the item id and the
    /// reason — a 'clean shutdown' at 500Hz with no cause is not clean.
    /// </summary>
    [Fact]
    public async Task ImmediateExitCondition_IsLoggedWithItemIdAndReason()
    {
        var sentinel = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "sentinel",
            Prompt = "p",
            State = WorkItemState.Queued,
        };
        await _store.CreateAsync(sentinel);

        var log = new CaptureLogger<OrchestratorService>();
        var pipeline = new SentinelCompletingPipelineRunner(sentinel.Id, _store);
        var queue = new InMemoryTaskQueue();
        var opts = new OrchestratorOptions
        {
            MaxConcurrentWorkers = 1,
            NoProgressBackoffBase = TimeSpan.FromMilliseconds(10),
            NoProgressBackoffMax = TimeSpan.FromMilliseconds(50),
            MaxNoProgressRedispatches = 10,
        };
        using var registry = new CancellationRegistry(CancellationToken.None);
        var svc = new OrchestratorService(queue, _store, pipeline, registry, opts, log);
        svc.DeferredRequeueDelayForTest = (_, _, _) => Task.CompletedTask;

        await svc.StartAsync(CancellationToken.None);
        WorkItemId itemId;
        try
        {
            var startupDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < startupDeadline)
            {
                var s = await _store.GetAsync(sentinel.Id);
                if (s?.State == WorkItemState.Done)
                    break;
                await Task.Delay(20);
            }

            var item = MakeWorkingItem();
            itemId = item.Id;
            await _store.CreateAsync(item);
            await queue.EnqueueAsync(item.Id);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (log.Entries)
                {
                    if (log.Entries.Any(e =>
                        e.Level == LogLevel.Warning && e.Message.Contains("no-progress")))
                        break;
                }
                await Task.Delay(20);
            }
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }

        List<(LogLevel Level, string Message)> entries;
        lock (log.Entries) { entries = log.Entries.ToList(); }
        var warning = entries.FirstOrDefault(e =>
            e.Level == LogLevel.Warning && e.Message.Contains("no-progress"));
        Assert.True(warning.Message is not null,
            $"expected a no-progress Warning; got: {string.Join(" | ", entries.Select(e => $"{e.Level}:{e.Message}"))}");
        Assert.Contains(itemId.ToString(), warning.Message);
        Assert.Contains("pipeline-ran", warning.Message);
    }

    private sealed class NoProgressPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
            => Task.CompletedTask;
    }

    /// <summary>
    /// Completes one known sentinel item (so tests can observe that startup
    /// recovery has finished) and no-ops every other pickup without touching
    /// the store — the stuck-pickup shape under test.
    /// </summary>
    private sealed class SentinelCompletingPipelineRunner(WorkItemId sentinelId, IWorkItemStore store) : IPipelineRunner
    {
        public async Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default)
        {
            if (item.Id == sentinelId)
                await store.UpdateAsync(item.With(WorkItemState.Done), ct);
        }
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) { Entries.Add((logLevel, formatter(state, exception))); }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

/// <summary>
/// The supported no-restart escape hatch for a stuck in-flight item is
/// <c>POST /workitems/{id}/recover</c>, served by
/// <see cref="ItemStaleProgressWatchdog.RecoverItemAsync"/> — the same guarded
/// path the stale-progress sweep uses. These tests pin that contract: a
/// Working item with no live owner returns to Queued (runnable) in-process,
/// while a concurrent advance is refused rather than clobbered.
/// </summary>
public sealed class StuckWorkItemRecoveryTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-stuck-recover-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly ItemStaleProgressWatchdog _watchdog;

    public StuckWorkItemRecoveryTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _watchdog = new ItemStaleProgressWatchdog(
            _store, _queue, _registry,
            new WorkerProgressWatchdogOptions(),
            NullLogger<ItemStaleProgressWatchdog>.Instance);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task RecoverItemAsync_ReturnsStuckWorkingItemToRunnable_WithoutRestart()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        };
        await _store.CreateAsync(item);

        var fetched = await _store.GetAsync(item.Id);
        Assert.NotNull(fetched);

        var result = await _watchdog.RecoverItemAsync(
            fetched!, "operator-triggered recovery", CancellationToken.None);

        Assert.True(result.Recovered, result.Error);
        Assert.Equal(WorkItemState.Working, result.FromState);
        Assert.Equal(WorkItemState.Queued, result.NewState);

        var reread = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, reread?.State);
        Assert.True(_queue.Count >= 1, "recovered item must be re-enqueued for dispatch");
    }

    [Fact]
    public async Task RecoverItemAsync_RefusesWhenItemAdvancedConcurrently()
    {
        var item = new WorkItem
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Working,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        };
        await _store.CreateAsync(item);
        var staleSnapshot = await _store.GetAsync(item.Id);
        Assert.NotNull(staleSnapshot);

        // Another writer advances the row before recovery runs.
        await _store.UpdateAsync(staleSnapshot!.With(WorkItemState.WorkComplete), CancellationToken.None);

        var result = await _watchdog.RecoverItemAsync(
            staleSnapshot, "operator-triggered recovery", CancellationToken.None);

        Assert.False(result.Recovered);
        Assert.Contains("advanced", result.Error);
    }
}

/// <summary>
/// Worker-pool lifecycle chatter (per-pickup start/finish, registry
/// register/deregister) must stay below the Information file-sink threshold
/// so a pathological pickup loop cannot fill the disk before anyone sees it.
/// Per-item visibility is retained via <c>work_item.picked_up</c>.
/// </summary>
[Collection("GlobalSerilog")]
public sealed class WorkerPoolLogRateTests : IDisposable
{
    private readonly TestSink _sink = new();
    private readonly Serilog.ILogger _scopedLogger;
    private readonly IDisposable _auditScope;

    public WorkerPoolLogRateTests()
    {
        // Route this flow's audit events to the test sink via a scoped logger
        // rather than by replacing the process-global Log.Logger: other test
        // collections (notably WebApplicationFactory boots) re-create the
        // global logger concurrently, which both steals our events and leaks
        // foreign events (e.g. host-terminated Fatal) into our sink.
        _scopedLogger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(_sink)
            .CreateLogger();
        _auditScope = AuditLog.PushScopedLogger(_scopedLogger);
    }

    public void Dispose()
    {
        _auditScope.Dispose();
        (_scopedLogger as IDisposable)?.Dispose();
    }

    [Fact]
    public void PerPickupLifecycleLogging_StaysBelowInformation()
    {
        var id = WorkItemId.New();

        for (var i = 0; i < 500; i++)
        {
            AuditLog.WorkerPoolWorkerStarted(i, id);
            AuditLog.WorkerPoolWorkerFinished(i, id);
            AuditLog.WorkerRegistered($"worker-{i}", "host", 1234);
            AuditLog.WorkerDeregistered($"worker-{i}");
        }

        var lifecycleLevels = _sink.Events
            .Where(e => e.Properties.TryGetValue("EventName", out var name)
                && name.ToString().Trim('"') is "worker_pool.worker_started"
                    or "worker_pool.worker_finished"
                    or "worker.registered"
                    or "worker.deregistered")
            .Select(e => e.Level)
            .ToList();

        Assert.Empty(lifecycleLevels);

        AuditLog.WorkItemPickedUp(1, id);
        var pickedUp = Assert.Single(_sink.Events, e =>
            e.Properties.TryGetValue("EventName", out var name)
            && name.ToString().Trim('"') == "work_item.picked_up");
        Assert.True(pickedUp.Level >= LogEventLevel.Information);
    }
}

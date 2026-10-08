using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Regression tests for the re-pickup progress-clock bug: the watchdog judged
/// a freshly re-picked item on progress timestamps inherited from its previous
/// turn (item.UpdatedAt, stream mtime, item.StartedAt), so a re-picked item
/// was recovered ~60s after pickup and abandoned after MaxRecoveryAttempts
/// without ever getting a real chance to run. The fix measures progress from
/// the latest of the item signals and the current worker's binding time, and
/// never consumes the recovery budget for an attempt that ran less than a
/// full progress window.
/// </summary>
public sealed class WorkerProgressWatchdogRepickupTests : IDisposable
{
    private readonly TestScratchDirectory _scratch = TestScratchDirectory.Create("codeybox-watchdog-repickup-");
    private readonly string _dbPath;
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly StaleStreamStore _streams;
    private readonly WorkerProgressWatchdogOptions _opts;
    private readonly RecordingWorkerPoolRecoverySlotReleaser _slotReleaser;

    public WorkerProgressWatchdogRepickupTests()
    {
        _dbPath = _scratch.DbPath("repickup.db");
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _webhooks = new CapturingWebhookDispatcher();
        _streams = new StaleStreamStore();
        _opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            AutoRecover = true,
            PostAgentTransitionTimeout = TimeSpan.FromMinutes(10),
        };
        _slotReleaser = new RecordingWorkerPoolRecoverySlotReleaser();
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
        _scratch.Dispose();
    }

    [Fact]
    public async Task RepickedItemWithStaleSignals_GetsFullTimeoutBeforeJudgedStuck()
    {
        // Mirrors the incident: work finished hours ago (stale UpdatedAt,
        // stale stream mtime, stale StartedAt) and a worker just re-picked
        // the WorkComplete item. Nothing may happen before T + timeout.
        var time = new ManualTimeProvider();
        var watchdog = MakeWatchdog(time);
        var pickupAt = time.GetUtcNow();
        var stale = pickupAt - TimeSpan.FromHours(3);

        var item = MakeItem(WorkItemState.WorkComplete, updatedAt: stale, startedAt: stale);
        await _store.CreateAsync(item);
        _streams.StampActivity(item.Id, stale);
        await PlantWorkerAsync($"worker-{Guid.NewGuid()}", item.Id, boundAt: pickupAt, workerStartedAt: pickupAt);

        await watchdog.RunOnceAsync(CancellationToken.None);
        var before = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, before!.State);
        Assert.Equal(0, before.RecoveryAttempts);

        time.Advance(TimeSpan.FromMinutes(29));
        await watchdog.RunOnceAsync(CancellationToken.None);
        var mid = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, mid!.State);
        Assert.Equal(0, mid.RecoveryAttempts);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task RepickedItemIdlePastTimeout_IsStillRecovered()
    {
        // The other half of the contract: a re-picked item that then makes no
        // progress for a full window is still recovered (and counted).
        var time = new ManualTimeProvider();
        var watchdog = MakeWatchdog(time);
        var pickupAt = time.GetUtcNow();
        var stale = pickupAt - TimeSpan.FromHours(3);

        var item = MakeItem(WorkItemState.WorkComplete, updatedAt: stale, startedAt: stale);
        await _store.CreateAsync(item);
        _streams.StampActivity(item.Id, stale);
        await PlantWorkerAsync($"worker-{Guid.NewGuid()}", item.Id, boundAt: pickupAt, workerStartedAt: pickupAt);

        time.Advance(TimeSpan.FromMinutes(31));
        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.WorkComplete, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Single(_slotReleaser.Releases);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task TenQuickRedispatches_DoNotAbandonItem()
    {
        // External deferrals re-queue without touching progress timestamps, so
        // ten rapid pickup cycles must all stay inside their own fresh windows
        // and never consume the recovery budget.
        var time = new ManualTimeProvider();
        var watchdog = MakeWatchdog(time);
        var stale = time.GetUtcNow() - TimeSpan.FromHours(3);

        var item = MakeItem(WorkItemState.WorkComplete, updatedAt: stale, startedAt: stale);
        await _store.CreateAsync(item);
        _streams.StampActivity(item.Id, stale);

        string? previousWorkerId = null;
        for (var i = 0; i < 10; i++)
        {
            if (previousWorkerId is not null)
                await _registry.DeregisterAsync(previousWorkerId);
            var workerId = $"worker-redispatch-{i}-{Guid.NewGuid()}";
            var pickupAt = time.GetUtcNow();
            await PlantWorkerAsync(workerId, item.Id, boundAt: pickupAt, workerStartedAt: pickupAt);
            previousWorkerId = workerId;

            time.Advance(TimeSpan.FromMinutes(1));
            await watchdog.RunOnceAsync(CancellationToken.None);

            var current = await _store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.WorkComplete, current!.State);
            Assert.Equal(0, current.RecoveryAttempts);
        }

        var after = await _store.GetAsync(item.Id);
        Assert.NotEqual(WorkItemState.AbandonedAfterRecoveryAttempts, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task PrematureRecovery_DoesNotConsumeRecoveryBudget()
    {
        // Belt-and-braces for rows without a bind stamp (pre-upgrade data):
        // the item looks stuck by its stale signals, but the current attempt
        // only just started, so the recovery must not increment the budget.
        var time = new ManualTimeProvider();
        var watchdog = MakeWatchdog(time);
        var pickupAt = time.GetUtcNow();
        var stale = pickupAt - TimeSpan.FromHours(3);

        var item = MakeItem(WorkItemState.Working, updatedAt: stale, startedAt: stale);
        await _store.CreateAsync(item);
        await PlantWorkerAsync($"worker-{Guid.NewGuid()}", item.Id, boundAt: null, workerStartedAt: pickupAt);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Null(after.RecoveryAttemptSourceState);
        Assert.Single(_slotReleaser.Releases);
    }

    [Fact]
    public async Task PrematureRecovery_DoesNotAbandonItemAtBudgetCap()
    {
        // Same guard at the cap: an item already at MaxRecoveryAttempts that
        // is re-picked and immediately (prematurely) judged stuck is re-queued,
        // not abandoned.
        var time = new ManualTimeProvider();
        var watchdog = MakeWatchdog(time);
        var pickupAt = time.GetUtcNow();
        var stale = pickupAt - TimeSpan.FromHours(3);

        var item = MakeItem(WorkItemState.Working, updatedAt: stale, startedAt: stale) with
        {
            RecoveryAttempts = _opts.MaxRecoveryAttempts,
            RecoveryAttemptSourceState = WorkItemState.Working,
        };
        await _store.CreateAsync(item);
        await PlantWorkerAsync($"worker-{Guid.NewGuid()}", item.Id, boundAt: null, workerStartedAt: pickupAt);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(_opts.MaxRecoveryAttempts, after.RecoveryAttempts);
    }

    [Fact]
    public async Task FirstTurnStuckItem_StillRecoveredAndCounted()
    {
        // Regression anchor: ordinary first-turn stuck detection is unchanged —
        // an attempt that genuinely ran past the timeout is recovered and counts.
        var time = new ManualTimeProvider();
        var watchdog = MakeWatchdog(time);
        var now = time.GetUtcNow();
        var stale = now - TimeSpan.FromHours(2);

        var item = MakeItem(WorkItemState.Working, updatedAt: stale, startedAt: stale);
        await _store.CreateAsync(item);
        await PlantWorkerAsync($"worker-{Guid.NewGuid()}", item.Id, boundAt: null, workerStartedAt: stale);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Single(_slotReleaser.Releases);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task HeartbeatBindingNewItem_StampsBoundAtAndPreservesIt()
    {
        // The registry maintains the bind stamp across heartbeats: a newly
        // bound item gets a fresh stamp, same-item heartbeats preserve it,
        // and unbinding clears it.
        var workerId = $"worker-{Guid.NewGuid()}";
        var itemId = WorkItemId.New();
        var otherId = WorkItemId.New();
        var start = DateTimeOffset.UtcNow;
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = workerId,
            HostName = "host",
            ProcessId = 1,
            StartedAt = start,
            LastHeartbeatAt = start,
            CurrentWorkItemId = null,
        });

        var beforeBind = DateTimeOffset.UtcNow;
        await _registry.HeartbeatAsync(workerId, itemId.ToString());
        var afterBind = DateTimeOffset.UtcNow;
        var bound = await FindWorkerAsync(workerId);
        Assert.NotNull(bound);
        Assert.Equal(itemId.ToString(), bound!.CurrentWorkItemId);
        Assert.NotNull(bound.CurrentWorkItemBoundAt);
        Assert.True(beforeBind <= bound.CurrentWorkItemBoundAt && bound.CurrentWorkItemBoundAt <= afterBind);

        await _registry.HeartbeatAsync(workerId, itemId.ToString());
        var rebound = await FindWorkerAsync(workerId);
        Assert.Equal(bound.CurrentWorkItemBoundAt, rebound!.CurrentWorkItemBoundAt);

        await _registry.HeartbeatAsync(workerId, otherId.ToString());
        var switched = await FindWorkerAsync(workerId);
        Assert.Equal(otherId.ToString(), switched!.CurrentWorkItemId);
        Assert.NotNull(switched.CurrentWorkItemBoundAt);

        await _registry.HeartbeatAsync(workerId, null);
        var unbound = await FindWorkerAsync(workerId);
        Assert.Null(unbound!.CurrentWorkItemId);
        Assert.Null(unbound.CurrentWorkItemBoundAt);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private WorkerProgressWatchdog MakeWatchdog(ManualTimeProvider time) => new(
        _registry, _store, _queue, _opts,
        NullLogger<WorkerProgressWatchdog>.Instance,
        _streams, _webhooks, _slotReleaser,
        timeProvider: time);

    private static WorkItem MakeItem(WorkItemState state, DateTimeOffset updatedAt, DateTimeOffset? startedAt) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
        UpdatedAt = updatedAt,
        StartedAt = startedAt,
        DependsOn = [],
    };

    private async Task PlantWorkerAsync(
        string workerId, WorkItemId itemId, DateTimeOffset? boundAt, DateTimeOffset workerStartedAt)
    {
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = workerId,
            HostName = "host",
            ProcessId = 1,
            StartedAt = workerStartedAt,
            LastHeartbeatAt = workerStartedAt,
            CurrentWorkItemId = itemId.ToString(),
            CurrentWorkItemBoundAt = boundAt,
        });
    }

    private async Task<WorkerRegistration?> FindWorkerAsync(string workerId)
    {
        var rows = await _registry.ListAsync(CancellationToken.None);
        foreach (var row in rows)
        {
            if (string.Equals(row.WorkerId, workerId, StringComparison.Ordinal))
                return row;
        }
        return null;
    }

    private sealed class StaleStreamStore : IAgentStreamStore
    {
        private readonly Dictionary<WorkItemId, (DateTimeOffset CapturedAt, DateTimeOffset LastActivityAt)> _activity = [];

        public AgentStreamsOptions Options { get; } = new() { Enabled = true, Path = "/tmp/codeybox-test-streams" };

        public void StampActivity(WorkItemId id, DateTimeOffset at) => _activity[id] = (at, at);

        public Task<AgentStreamCapture?> BeginCaptureAsync(WorkItemId workItemId, string phase, int iteration, CancellationToken ct = default)
            => Task.FromResult<AgentStreamCapture?>(null);

        public Task<IReadOnlyList<AgentStreamFile>> ListAsync(
            WorkItemId workItemId, int limit = AgentStreamStore.DefaultListLimit,
            bool includeLineCount = false, CancellationToken ct = default)
        {
            if (!_activity.TryGetValue(workItemId, out var stamps))
                return Task.FromResult<IReadOnlyList<AgentStreamFile>>([]);
            IReadOnlyList<AgentStreamFile> files =
            [
                new AgentStreamFile("work-1-abc123.jsonl", "work", 1, 1, null, stamps.CapturedAt, stamps.LastActivityAt),
            ];
            return Task.FromResult(files);
        }

        public Task<AgentStreamFile?> GetAsync(WorkItemId workItemId, string fileName, bool includeLineCount = false, CancellationToken ct = default)
            => Task.FromResult<AgentStreamFile?>(null);

        public Task<Stream?> OpenReadAsync(WorkItemId workItemId, string fileName, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);

        public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class RecordingWorkerPoolRecoverySlotReleaser : IWorkerPoolRecoverySlotReleaser
    {
        public List<(string WorkerId, WorkItemId? WorkItemId, string Reason)> Releases { get; } = [];

        public ValueTask<bool> TryReleaseRecoveredWorkerSlotAsync(
            string workerId,
            WorkItemId? workItemId,
            string reason,
            CancellationToken ct = default)
        {
            Releases.Add((workerId, workItemId, reason));
            return ValueTask.FromResult(true);
        }
    }
}

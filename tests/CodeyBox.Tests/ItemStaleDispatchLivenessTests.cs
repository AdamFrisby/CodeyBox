using ControllableTimeProvider = Microsoft.Extensions.Time.Testing.FakeTimeProvider;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Dispatch-awareness for <see cref="ItemStaleProgressWatchdog"/>: an item
/// with no bound worker that the dispatcher keeps deferring (quota / cap /
/// budget) is waiting — not wedged — and must never consume recovery attempts
/// or park at <c>NeedsOperatorInput</c>. Only an item the dispatcher has
/// stopped evaluating has fallen out of dispatch and may be recovered.
///
/// <para>
/// Regression shape (2026-09-25/29): an item sat in <c>WorkComplete</c>
/// awaiting audit while its agent's quota was exhausted. Dispatch deferred it
/// every recheck, but the stale detector fired every 4 h, "recovered" it
/// <c>WorkComplete → WorkComplete</c> three times, then parked it as
/// <c>item-stale … exceeded MaxRecoveryAttempts (3)</c>.
/// </para>
/// </summary>
public sealed class ItemStaleDispatchLivenessTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-item-stale-dispatch-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly WorkerProgressWatchdogOptions _opts;
    private readonly DispatchItemLivenessTracker _tracker;
    private readonly FakeTimeProvider _time;

    public ItemStaleDispatchLivenessTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _webhooks = new CapturingWebhookDispatcher();
        _opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(60),
            CheckInterval = TimeSpan.FromMinutes(1),
            ItemStaleTimeout = TimeSpan.FromHours(4),
            ItemStaleCheckInterval = TimeSpan.FromMinutes(5),
            ItemStaleMaxRecoveryAttempts = 3,
            ItemStaleDispatchQuietTimeout = TimeSpan.FromMinutes(30),
            ItemQuotaWaitNoticeThreshold = TimeSpan.FromHours(1),
        };
        _opts.Validate();
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 09, 25, 14, 00, 00, TimeSpan.Zero));
        _tracker = new DispatchItemLivenessTracker(_time);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
        TestScratchDirectory.DeleteSqliteCompanions(_dbPath);
    }

    private ItemStaleProgressWatchdog BuildWatchdog() => new(
        _store, _queue, _registry,
        _opts,
        NullLogger<ItemStaleProgressWatchdog>.Instance,
        _webhooks,
        timeProvider: _time,
        dispatchLiveness: _tracker);

    private WorkItem MakeWaitingItem(WorkItemState state)
    {
        var now = _time.GetUtcNow();
        return new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = state,
            Agent = AgentKind.Copilot,
            StartedAt = now,
            UpdatedAt = now,
        };
    }

    [Fact]
    public async Task Sweep_ContinuouslyQuotaDeferred_PastThreeStaleWindows_NeverParked_NoAttemptsConsumed()
    {
        // Incident shape: WorkComplete awaiting audit, pinned to copilot,
        // quota exhausted throughout. Dispatch defers every 5 min (quota
        // recheck cadence); the sweep runs alongside for over three stale
        // windows (3 x 4 h). The item must never be recovered or parked.
        var item = MakeWaitingItem(WorkItemState.WorkComplete);
        await _store.CreateAsync(item);
        var watchdog = BuildWatchdog();

        var end = _time.GetUtcNow() + TimeSpan.FromHours(13);
        while (_time.GetUtcNow() < end)
        {
            _tracker.NoteDispatchEvaluated(item.Id, AgentKind.Copilot.Value);
            await watchdog.RunOnceAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromMinutes(5));
        }
        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.WorkComplete, after.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Null(after.LastError);
        Assert.Equal(0, _queue.Count);
        Assert.DoesNotContain(_webhooks.Events, e => e.Event == "work_item.recovered");
    }

    [Fact]
    public async Task Sweep_DeferredSetPresence_WithStaleEvaluationTimestamp_StillWaiting()
    {
        // Belt-and-suspenders: even when the last recorded evaluation is
        // older than the quiet window, presence in the deferred set proves
        // the dispatcher still owns the item — waiting, not stale.
        var item = MakeWaitingItem(WorkItemState.AuditPassed);
        await _store.CreateAsync(item);
        _tracker.NoteDispatchEvaluated(item.Id, AgentKind.Copilot.Value);
        _time.Advance(TimeSpan.FromHours(5));
        _tracker.IsDeferredProvider = id => id == item.Id;

        await BuildWatchdog().RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.AuditPassed, after.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Sweep_DispatcherStoppedEvaluating_NoWorker_RecoversAsBefore()
    {
        // The real wedge case: dispatcher evaluated the item two hours ago
        // (past the 30 min quiet window) and no longer holds it deferred.
        // UpdatedAt frozen 5 h past the 4 h threshold → stale, recovered.
        var item = MakeWaitingItem(WorkItemState.WorkComplete) with
        {
            UpdatedAt = _time.GetUtcNow(),
        };
        await _store.CreateAsync(item);
        _tracker.NoteDispatchEvaluated(item.Id, AgentKind.Copilot.Value);
        _time.Advance(TimeSpan.FromHours(2));
        _tracker.NoteDispatchEvaluated(item.Id, quotaBlockedAgent: null);
        _time.Advance(TimeSpan.FromHours(3));

        await BuildWatchdog().RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.WorkComplete, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("item-stale", after.LastError);
        Assert.Equal(1, _queue.Count);
        Assert.Contains(_webhooks.Events, e => e.Event == "work_item.recovered");
    }

    [Fact]
    public async Task Sweep_NeverEvaluatedByDispatcher_NoWorker_RecoversAsBefore()
    {
        // No dispatch record at all (dispatcher never saw the item, or the
        // record aged out): identical to the pre-dispatch-awareness behaviour.
        var item = MakeWaitingItem(WorkItemState.WorkComplete) with
        {
            UpdatedAt = _time.GetUtcNow().AddHours(-5),
        };
        await _store.CreateAsync(item);

        await BuildWatchdog().RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.WorkComplete, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("item-stale", after.LastError);
    }

    [Fact]
    public async Task Sweep_QuotaWaitNotice_EmittedOncePerThresholdCrossing()
    {
        // Episode 1 crosses the 1 h notice threshold: exactly one
        // work_item.waiting_on_quota webhook no matter how many sweeps run
        // inside the episode. A second episode notifies once more.
        var item = MakeWaitingItem(WorkItemState.Queued) with { StartedAt = null };
        await _store.CreateAsync(item);
        var watchdog = BuildWatchdog();

        _tracker.NoteDispatchEvaluated(item.Id, AgentKind.Copilot.Value);
        _time.Advance(TimeSpan.FromMinutes(30));
        await watchdog.RunOnceAsync(CancellationToken.None);
        Assert.DoesNotContain(_webhooks.Events, e => e.Event == "work_item.waiting_on_quota");

        _tracker.NoteDispatchEvaluated(item.Id, AgentKind.Copilot.Value);
        _time.Advance(TimeSpan.FromMinutes(35));
        await watchdog.RunOnceAsync(CancellationToken.None);
        var notice = Assert.Single(
            _webhooks.Events,
            e => e.Event == "work_item.waiting_on_quota");

        for (var i = 0; i < 5; i++)
        {
            _tracker.NoteDispatchEvaluated(item.Id, AgentKind.Copilot.Value);
            _time.Advance(TimeSpan.FromMinutes(5));
            await watchdog.RunOnceAsync(CancellationToken.None);
        }
        Assert.Single(
            _webhooks.Events,
            e => e.Event == "work_item.waiting_on_quota");

        // Episode ends (non-quota evaluation) and a new quota episode starts:
        // crossing the threshold again notifies once more.
        _tracker.NoteDispatchEvaluated(item.Id, quotaBlockedAgent: null);
        _time.Advance(TimeSpan.FromMinutes(1));
        await watchdog.RunOnceAsync(CancellationToken.None);
        _tracker.NoteDispatchEvaluated(item.Id, AgentKind.Copilot.Value);
        _time.Advance(TimeSpan.FromMinutes(61));
        await watchdog.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, _webhooks.Events.Count(e => e.Event == "work_item.waiting_on_quota"));

        // The notice carries the queue-status reason shape.
        Assert.Contains("copilot", notice.Details?.ToString(), StringComparison.OrdinalIgnoreCase);

        // Waiting was never mistaken for wedged: no recovery, no park.
        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Orchestrator_QuotaDeferral_RecordsDispatchEvaluationAndQuotaEpisode()
    {
        // Production wiring: a quota-shaped deferral must land in the
        // tracker (evaluation + quota episode + live deferred flag) so the
        // stale watchdog later reads it as liveness. Without this, the
        // watchdog and tracker would agree in unit tests but never connect
        // in production and the original parking incident would recur.
        var time = new ControllableTimeProvider(
            new DateTimeOffset(2026, 09, 25, 14, 00, 00, TimeSpan.Zero));
        var queue = new InMemoryTaskQueue();
        var pipeline = new NeverRunsPipeline();
        using var registry = new CancellationRegistry(CancellationToken.None);
        var tracker = new DispatchItemLivenessTracker(time);
        var router = new AgentClassRouter(
            [new AgentClass
            {
                Id = "codex-cls",
                DisplayName = "codex-cls",
                Members =
                [
                    new AgentMembership { Agent = AgentKind.Codex, Billing = AgentBilling.Subscription, QualityScore = 100 },
                ],
            }],
            [new FakeProbe(AgentKind.Codex, 0.0)],
            new QuotaRouterOptions
            {
                MinQuotaPct = 5.0,
                QuotaRecheckInterval = TimeSpan.FromMinutes(5),
                CapRetryRecheckInterval = TimeSpan.FromSeconds(15),
            },
            NullLogger<AgentClassRouter>.Instance);
        using var svc = new OrchestratorService(
            queue, _store, pipeline, registry,
            new OrchestratorOptions { MaxConcurrentWorkers = 2 },
            NullLogger<OrchestratorService>.Instance,
            router: router,
            timeProvider: time,
            dispatchLiveness: tracker);

        var item = MakeWaitingItem(WorkItemState.WorkComplete) with { AgentClassId = "codex-cls", Agent = null };
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id);
        await svc.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(
                await WaitUntilAsync(() => svc.IsDeferredForTest(item.Id), TimeSpan.FromSeconds(30)),
                "quota-exhausted item must be deferred, not dispatched");

            var liveness = svc.DispatchLiveness;
            Assert.Same(tracker, liveness);
            Assert.NotNull(liveness);
            Assert.True(liveness.TryGetLiveness(item.Id, out var snapshot));
            Assert.True(snapshot.IsDeferred);
            Assert.NotNull(snapshot.QuotaBlockedSince);
            Assert.Equal("eligible agents", snapshot.QuotaBlockedAgent);
            Assert.Single(tracker.GetQuotaWaits());
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void Tracker_QuotaEpisode_EndsOnNonQuotaEvaluation_And_Pickup()
    {
        var id = WorkItemId.New();
        _tracker.NoteDispatchEvaluated(id, "copilot");
        Assert.True(_tracker.TryGetLiveness(id, out var live));
        Assert.NotNull(live.QuotaBlockedSince);

        _tracker.NoteDispatchEvaluated(id, quotaBlockedAgent: null);
        Assert.True(_tracker.TryGetLiveness(id, out live));
        Assert.Null(live.QuotaBlockedSince);

        _tracker.NoteDispatchEvaluated(id, "copilot");
        _tracker.NotePickedUp(id);
        Assert.True(_tracker.TryGetLiveness(id, out live));
        Assert.Null(live.QuotaBlockedSince);

        Assert.Empty(_tracker.GetQuotaWaits());
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return true;
            await Task.Delay(25);
        }
        return predicate();
    }

    private sealed class NeverRunsPipeline : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken ct, CancellationToken hostShutdownToken = default) =>
            throw new InvalidOperationException("a quota-deferred item must never reach the pipeline");
    }
}

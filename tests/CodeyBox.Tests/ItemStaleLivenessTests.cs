using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Liveness gating for <see cref="ItemStaleProgressWatchdog"/>: an item whose
/// <c>UpdatedAt</c> is frozen past <c>ItemStaleTimeout</c> is stale only when
/// its agent shows no other liveness. A still-appending agent stream means the
/// agent is producing output — parking it would repeat the 2026-09-08
/// incident (1h44m healthy turn parked 49 minutes after it had already
/// committed, exhausting the recovery budget on a live run).
/// </summary>
public sealed class ItemStaleLivenessTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-item-stale-liveness-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly WorkerProgressWatchdogOptions _opts;
    private readonly FakeTimeProvider _time;

    public ItemStaleLivenessTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _webhooks = new CapturingWebhookDispatcher();
        _opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(60),
            CheckInterval = TimeSpan.FromMinutes(1),
            ItemStaleTimeout = TimeSpan.FromMinutes(90),
            ItemStaleCheckInterval = TimeSpan.FromMinutes(5),
            ItemStaleMaxRecoveryAttempts = 3,
        };
        _opts.Validate();
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 09, 08, 00, 00, 00, TimeSpan.Zero));
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private ItemStaleProgressWatchdog BuildWatchdog(
        FakeAgentStreamStore? streams = null,
        FakeActivitySource? activitySource = null)
        => new(
            _store, _queue, _registry,
            _opts,
            NullLogger<ItemStaleProgressWatchdog>.Instance,
            _webhooks,
            timeProvider: _time,
            streams: streams,
            activitySource: activitySource);

    private WorkItem MakeItem(WorkItemState state, DateTimeOffset updatedAt, int recoveryAttempts = 0)
        => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = state,
            RecoveryAttempts = recoveryAttempts,
            WorkBranch = "codeybox/auto/work-liveness",
            StartedAt = state == WorkItemState.Queued ? null : _time.GetUtcNow().AddMinutes(-100),
            UpdatedAt = updatedAt,
        };

    [Fact]
    public async Task Sweep_StreamStillBeingWritten_DoesNotParkAsStale()
    {
        // Incident shape: UpdatedAt frozen for the whole 1h44m turn (older
        // than the 90m threshold) while the captured stream keeps advancing.
        // The agent is demonstrably producing output — not stale.
        var item = MakeItem(WorkItemState.Working, _time.GetUtcNow().AddMinutes(-100));
        await _store.CreateAsync(item);
        var streams = new FakeAgentStreamStore(
            new AgentStreamFile(
                "work-0.jsonl", "work", 0, 1024, 10,
                _time.GetUtcNow().AddMinutes(-100),
                _time.GetUtcNow().AddMinutes(-1)));

        await BuildWatchdog(streams: streams).RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Working, after.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task Sweep_StreamAlive_DoesNotConsumeRecoveryBudget()
    {
        // A live run must not burn a recovery attempt: skipping on liveness
        // leaves RecoveryAttempts exactly as it was.
        var item = MakeItem(WorkItemState.Working, _time.GetUtcNow().AddMinutes(-100), recoveryAttempts: 2);
        await _store.CreateAsync(item);
        var streams = new FakeAgentStreamStore(
            new AgentStreamFile(
                "work-0.jsonl", "work", 0, 1024, 10,
                _time.GetUtcNow().AddMinutes(-100),
                _time.GetUtcNow().AddMinutes(-2)));

        await BuildWatchdog(streams: streams).RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Working, after.State);
        Assert.Equal(2, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Sweep_NoStreamActivity_StillParksAfterInterval()
    {
        // Bound for genuinely hung runs: no output for the configured
        // interval plus no transition is still stale, and the park records
        // the evidence considered.
        var item = MakeItem(WorkItemState.Working, _time.GetUtcNow().AddMinutes(-100));
        await _store.CreateAsync(item);
        var streams = new FakeAgentStreamStore(
            new AgentStreamFile(
                "work-0.jsonl", "work", 0, 1024, 10,
                _time.GetUtcNow().AddMinutes(-100),
                _time.GetUtcNow().AddMinutes(-100)));

        await BuildWatchdog(streams: streams).RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("lastStreamWrite=", after.LastError);
        Assert.Contains("sandbox=", after.LastError);
        Assert.Contains("lastTransition=", after.LastError);
        Assert.Equal(1, _queue.Count);
        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
    }

    [Fact]
    public async Task Sweep_NoStreamStore_StillParksAfterInterval()
    {
        // Streams unavailable (store not wired / capture disabled) falls back
        // to UpdatedAt-only so hung runs are still bounded.
        var item = MakeItem(WorkItemState.Working, _time.GetUtcNow().AddMinutes(-100));
        await _store.CreateAsync(item);

        await BuildWatchdog().RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Contains("streams-unavailable", after.LastError);
    }

    [Fact]
    public async Task Sweep_SandboxActivityAlive_DoesNotParkAsStale()
    {
        // Secondary liveness signal: a newly-observed sandbox state for the
        // bound worker defers the stale classification for this sweep.
        var item = MakeItem(WorkItemState.Working, _time.GetUtcNow().AddMinutes(-100));
        await _store.CreateAsync(item);
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "live-sandbox-worker",
            HostName = "host",
            ProcessId = 4242,
            StartedAt = _time.GetUtcNow().AddMinutes(-100),
            LastHeartbeatAt = _time.GetUtcNow(),
            CurrentWorkItemId = item.Id.ToString(),
        });
        var activitySource = new FakeActivitySource(new WorkerProgressActivity("active-sandbox-change"));

        await BuildWatchdog(activitySource: activitySource).RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Working, after.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        // The item-stale probe must not use host CPU as liveness: the
        // reconnect-loop wedge this detector owns stays CPU-active while
        // frozen, so CPU would mask it.
        Assert.False(activitySource.LastProbe.ProcessCpuProgressSignalEnabled);
        Assert.True(activitySource.LastProbe.ActiveSandboxProgressSignalEnabled);
    }

    [Fact]
    public async Task Sweep_SandboxQuietAndStreamQuiet_StillParks()
    {
        // Stable sandbox ownership alone is not liveness: a wedge holding its
        // VM open with no output and no transition is still stale.
        var item = MakeItem(WorkItemState.Working, _time.GetUtcNow().AddMinutes(-100));
        await _store.CreateAsync(item);
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "wedged-quiet-worker",
            HostName = "host",
            ProcessId = 4243,
            StartedAt = _time.GetUtcNow().AddMinutes(-100),
            LastHeartbeatAt = _time.GetUtcNow(),
            CurrentWorkItemId = item.Id.ToString(),
        });
        var activitySource = new FakeActivitySource(activity: null);

        await BuildWatchdog(activitySource: activitySource).RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("sandbox=no-sandbox-activity", after.LastError);
    }

    private sealed class FakeAgentStreamStore(params AgentStreamFile[] files) : IAgentStreamStore
    {
        private readonly IReadOnlyList<AgentStreamFile> _files = files;

        public AgentStreamsOptions Options { get; } = new();

        public Task<AgentStreamCapture?> BeginCaptureAsync(WorkItemId workItemId, string phase, int iteration, CancellationToken ct = default)
            => Task.FromResult<AgentStreamCapture?>(null);

        public Task<IReadOnlyList<AgentStreamFile>> ListAsync(WorkItemId workItemId, int limit = 100, bool includeLineCount = false, CancellationToken ct = default)
            => Task.FromResult(_files);

        public Task<AgentStreamFile?> GetAsync(WorkItemId workItemId, string fileName, bool includeLineCount = false, CancellationToken ct = default)
            => Task.FromResult<AgentStreamFile?>(null);

        public Task<Stream?> OpenReadAsync(WorkItemId workItemId, string fileName, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);

        public Task<int> SweepAsync(DateTimeOffset now, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class FakeActivitySource(WorkerProgressActivity? activity) : IWorkerProgressActivitySource
    {
        public WorkerProgressActivityProbe LastProbe { get; private set; }

        public ValueTask<WorkerProgressActivity?> ObserveAsync(
            WorkerRegistration worker,
            WorkItemId itemId,
            WorkerProgressActivityProbe probe,
            CancellationToken ct)
        {
            LastProbe = probe;
            return ValueTask.FromResult(activity);
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="ItemStaleProgressWatchdog"/> — the per-item
/// stale-updatedAt detector that complements the per-worker
/// <see cref="WorkerProgressWatchdog"/> (which treats CPU / stream / sandbox
/// activity as progress and so cannot catch a wedge where the worker is
/// still active but the item is frozen).
///
/// <para>
/// Covers the two production incident shapes from 2026-06-12:
/// </para>
/// <list type="bullet">
///   <item>Item A — orphaned by orchestrator restart: item Working with no
///         live worker row. Recovery: reclaim by requeueing preserving the
///         work branch.</item>
///   <item>Item B — codex stuck in transport reconnect loop: live worker,
///         live heartbeat, CPU active, but item.UpdatedAt frozen. The
///         per-worker watchdog cannot see this; this detector must.</item>
/// </list>
/// </summary>
public sealed class ItemStaleProgressWatchdogTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-item-stale-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly WorkerProgressWatchdogOptions _opts;
    private readonly RecordingSlotReleaser _slotReleaser;
    private readonly FakeTimeProvider _time;
    private readonly ItemStaleProgressWatchdog _watchdog;

    public ItemStaleProgressWatchdogTests()
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
        _slotReleaser = new RecordingSlotReleaser();
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 06, 12, 12, 00, 00, TimeSpan.Zero));
        _watchdog = new ItemStaleProgressWatchdog(
            _store, _queue, _registry,
            _opts,
            NullLogger<ItemStaleProgressWatchdog>.Instance,
            _webhooks,
            _slotReleaser,
            timeProvider: _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private WorkItem MakeItem(
        WorkItemState state,
        DateTimeOffset? updatedAt = null,
        int recoveryAttempts = 0,
        string? workBranch = null,
        string? preemptCheckpoint = null) => new()
        {
            Id = WorkItemId.New(),
            ProjectId = new ProjectId("test"),
            Title = "t",
            Prompt = "p",
            State = state,
            RecoveryAttempts = recoveryAttempts,
            WorkBranch = workBranch,
            PreemptCheckpoint = preemptCheckpoint,
            StartedAt = state == WorkItemState.Queued ? null : _time.GetUtcNow().AddMinutes(-100),
            UpdatedAt = updatedAt ?? _time.GetUtcNow().AddMinutes(-100),
        };

    // ── Acceptance (a): in-flight item with frozen updatedAt detected ────────

    [Fact]
    public async Task Sweep_FrozenItemUpdatedAt_BeyondThreshold_RecoversAndPreservesBranch()
    {
        // Item B incident shape: codex stuck in transport reconnect, worker
        // heartbeating, but item.UpdatedAt has not advanced for the full
        // 90 min stale window.
        const string workBranch = "codeybox/auto/work-stuck-codex";
        var frozenAt = _time.GetUtcNow().AddMinutes(-100); // > 90 min threshold
        var item = MakeItem(WorkItemState.Working, updatedAt: frozenAt, workBranch: workBranch);
        await _store.CreateAsync(item);

        // Heartbeating worker — simulates Item B exactly.
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "wedged-codex-worker",
            HostName = "host",
            ProcessId = 999,
            StartedAt = _time.GetUtcNow().AddMinutes(-100),
            LastHeartbeatAt = _time.GetUtcNow(),
            CurrentWorkItemId = item.Id.ToString(),
        });

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(workBranch, after.WorkBranch);
        Assert.True(after.PreserveWorkBranchOnQueuedPickup,
            "work branch must be preserved so the next pickup re-rebases existing commits rather than discarding partial progress");
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Null(after.StartedAt);
        Assert.Contains("item-stale", after.LastError);

        // Bound worker registry row claimed: pool slot released.
        Assert.Single(_slotReleaser.Releases);
        Assert.Equal("wedged-codex-worker", _slotReleaser.Releases[0].WorkerId);

        // Item enqueued for re-dispatch.
        Assert.Equal(1, _queue.Count);

        // Webhook fired with the right reason.
        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
    }

    [Fact]
    public async Task Sweep_FrozenItemUpdatedAt_NoLiveWorker_StillRecovers()
    {
        // Item A incident shape: orchestrator restarted, agent process
        // orphaned, no live worker row remains. The per-item watchdog must
        // walk items by state (not by worker registry) and catch this even
        // when no worker row exists.
        const string workBranch = "codeybox/auto/work-orphan-restart";
        var item = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-95),
            workBranch: workBranch);
        await _store.CreateAsync(item);

        Assert.Empty(await _registry.ListAsync());

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(workBranch, after.WorkBranch);
        Assert.True(after.PreserveWorkBranchOnQueuedPickup);
        Assert.Equal(1, after.RecoveryAttempts);
        // No worker to claim, so no slot release; recovery still succeeds.
        Assert.Empty(_slotReleaser.Releases);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Sweep_FreshItemUpdatedAt_NotRecovered()
    {
        // Item updated recently — must not trip even if the configured
        // worker-progress signals would all be stale. Detector is item.UpdatedAt-only.
        var item = MakeItem(WorkItemState.Working, updatedAt: _time.GetUtcNow().AddMinutes(-30));
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task Sweep_SuspendedVmItem_IsLeftAlone()
    {
        // SandboxResumeOnStartupService owns these; they may legitimately
        // sit at Working with stale UpdatedAt for the duration of the resume.
        var item = MakeItem(WorkItemState.Working, updatedAt: _time.GetUtcNow().AddMinutes(-100)) with
        {
            SuspendedVmName = "cb-vm-suspended",
        };
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
    }

    [Fact]
    public async Task Sweep_DisabledByZeroTimeout_NoOps()
    {
        _opts.ItemStaleTimeout = TimeSpan.Zero;
        var item = MakeItem(WorkItemState.Working, updatedAt: _time.GetUtcNow().AddMinutes(-100));
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
    }

    // ── Acceptance (d): bounded then escalates to NeedsOperatorInput ─────────

    [Fact]
    public async Task Sweep_AtBoundedCap_EscalatesToNeedsOperatorInput()
    {
        // Already at the configured cap — next recovery escalates instead of
        // looping Working → Queued → Working forever burning a slot per cycle.
        var item = MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-95),
            recoveryAttempts: _opts.ItemStaleMaxRecoveryAttempts,
            workBranch: "codeybox/auto/work-chronic");
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, after!.State);
        Assert.Equal(_opts.ItemStaleMaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Contains("MaxRecoveryAttempts", after.LastError);
        // NeedsOperatorInput is not enqueued — operator triage required.
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Sweep_NoDoubleRecovery_InSameProcess()
    {
        // After recovery the item is in Queued; if the same sweep runs again
        // with no UpdatedAt advance (re-pickup hasn't happened yet), the item
        // must not be re-recovered.
        var item = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/auto/work-once");
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);
        var first = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, first!.State);
        Assert.Equal(1, first.RecoveryAttempts);

        await _watchdog.RunOnceAsync(CancellationToken.None);
        var second = await _store.GetAsync(item.Id);
        // No second recovery attempt — still 1.
        Assert.Equal(1, second!.RecoveryAttempts);
    }

    // ── Watched-state coverage ──────────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Reworking)]
    [InlineData(WorkItemState.Auditing)]
    [InlineData(WorkItemState.Merging)]
    [InlineData(WorkItemState.ReworkingForConflict)]
    [InlineData(WorkItemState.UpstreamPushing)]
    public async Task Sweep_EveryActiveInFlightState_IsWatched(WorkItemState state)
    {
        var item = MakeItem(state, updatedAt: _time.GetUtcNow().AddMinutes(-100));
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        // The exact target depends on the state's MapToRecoveryState mapping,
        // but it must have left the in-flight state.
        Assert.NotEqual(state, after!.State);
    }

    [Theory]
    [InlineData(WorkItemState.Queued)]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merged)]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.NeedsOperatorInput)]
    [InlineData(WorkItemState.WaitingForQuotaReset)]
    public async Task Sweep_NonWatchedState_IsLeftAlone(WorkItemState state)
    {
        // Phase-boundary resting states and terminal/parked states are
        // dispatcher- or operator-owned; the per-item watchdog must not
        // re-recover them even if UpdatedAt is ancient.
        var item = MakeItem(state, updatedAt: _time.GetUtcNow().AddMinutes(-100));
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(state, after!.State);
    }

    // ── Independent of pool-level spawn health ──────────────────────────────

    [Fact]
    public async Task Sweep_PoolSpawningNormally_DoesNotPreventDetection()
    {
        // Two items: one healthy worker spawning normally; one wedged item
        // with frozen UpdatedAt. The per-item detector must still trip on
        // the wedged item — pool-level spawn health is irrelevant.
        var healthy = MakeItem(WorkItemState.Working, updatedAt: _time.GetUtcNow().AddSeconds(-30));
        var wedged = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-95),
            workBranch: "codeybox/auto/work-wedged");
        await _store.CreateAsync(healthy);
        await _store.CreateAsync(wedged);

        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "healthy-worker",
            HostName = "host",
            ProcessId = 1001,
            StartedAt = _time.GetUtcNow().AddMinutes(-1),
            LastHeartbeatAt = _time.GetUtcNow(),
            CurrentWorkItemId = healthy.Id.ToString(),
        });

        await _watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Equal(WorkItemState.Working, (await _store.GetAsync(healthy.Id))!.State);
        Assert.Equal(WorkItemState.Queued, (await _store.GetAsync(wedged.Id))!.State);
    }

    // ── Operator-triggered recovery via RecoverItemAsync ────────────────────

    [Fact]
    public async Task RecoverItemAsync_Operator_RecoversItemAndReportsResult()
    {
        // The operator endpoint POST /workitems/{id}/recover calls
        // RecoverItemAsync directly. The result must include the resolved
        // states / attempt / branch-preserved flag.
        const string workBranch = "codeybox/auto/work-operator";
        var item = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-5),
            workBranch: workBranch);
        await _store.CreateAsync(item);

        var result = await _watchdog.RecoverItemAsync(item, "operator: stuck on codex transport loop", CancellationToken.None);

        Assert.True(result.Recovered);
        Assert.Equal(WorkItemState.Working, result.FromState);
        Assert.Equal(WorkItemState.Queued, result.NewState);
        Assert.Equal(1, result.Attempt);
        Assert.True(result.BranchPreserved);
        Assert.Null(result.Error);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(workBranch, after.WorkBranch);
        Assert.True(after.PreserveWorkBranchOnQueuedPickup);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task RecoverItemAsync_NonActiveState_RefusesWithStructuredError()
    {
        // Operator-recover refuses anything that isn't in an active in-flight
        // state. The error message is structured (callers can surface it via
        // 409 Conflict).
        var item = MakeItem(WorkItemState.Queued, updatedAt: _time.GetUtcNow());
        await _store.CreateAsync(item);

        var result = await _watchdog.RecoverItemAsync(item, "operator-triggered", CancellationToken.None);

        Assert.False(result.Recovered);
        Assert.Equal(WorkItemState.Queued, result.FromState);
        Assert.Null(result.NewState);
        Assert.NotNull(result.Error);
        Assert.Contains("not an active in-flight state", result.Error);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task RecoverItemAsync_OperatorAtCap_EscalatesToNeedsOperatorInput()
    {
        // Operator-triggered recovery is bounded by the same cap as the
        // watchdog sweep. Once exceeded the item escalates to triage.
        var item = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow(),
            recoveryAttempts: _opts.ItemStaleMaxRecoveryAttempts,
            workBranch: "codeybox/auto/work-chronic-operator");
        await _store.CreateAsync(item);

        var result = await _watchdog.RecoverItemAsync(item, "operator", CancellationToken.None);

        Assert.True(result.Recovered);
        Assert.Equal(WorkItemState.NeedsOperatorInput, result.NewState);
    }

    // ── Test doubles ────────────────────────────────────────────────────────

    private sealed class RecordingSlotReleaser : IWorkerPoolRecoverySlotReleaser
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

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}

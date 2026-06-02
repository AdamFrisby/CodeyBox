using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="WorkerProgressWatchdog"/> — covers the two wedge
/// classes the operator observed (f9ea330a/69ee86c4: agent reported completed
/// but item never transitioned; 739f9bb3: no stream dir ever created /
/// pre-agent setup hang) plus the dependents-preservation requirement
/// (recovering a chain-head must restore cascade-cancelled descendants).
/// </summary>
public sealed class WorkerProgressWatchdogTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-watchdog-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly StaleStreamStore _streams;
    private readonly WorkerProgressWatchdogOptions _opts;
    private readonly WorkerProgressWatchdog _watchdog;
    private readonly RecordingWorkerPoolRecoverySlotReleaser _slotReleaser;

    public WorkerProgressWatchdogTests()
    {
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
        _watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WorkItem MakeItem(
        WorkItemState state,
        DateTimeOffset updatedAt,
        IReadOnlyList<WorkItemId>? dependsOn = null,
        WorkItemCancellationReason? cancellationReason = null,
        string? lastError = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
        UpdatedAt = updatedAt,
        DependsOn = dependsOn ?? [],
        CancellationReason = cancellationReason,
        LastError = lastError,
    };

    private async Task PlantHeartbeatingWorkerAsync(string workerId, WorkItemId itemId)
    {
        // A fresh heartbeat — the watchdog must NOT rely on heartbeat staleness;
        // progress is decided from item.UpdatedAt + stream activity only.
        var reg = new WorkerRegistration
        {
            WorkerId = workerId,
            HostName = "host",
            ProcessId = 1,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CurrentWorkItemId = itemId.ToString(),
        };
        await _registry.RegisterAsync(reg);
    }

    // ── (a) agent-completed-but-no-transition ────────────────────────────────

    [Fact]
    public async Task Watchdog_AgentCompletedButNoTransition_RecoversAndReclaimsSlot()
    {
        // Simulates f9ea330a / 69ee86c4: stream has activity (agent ran), then
        // updatedAt freezes because the post-agent commit/transition wedged.
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);
        // Stream mtime is also stale — the agent completed long ago but no new
        // activity since. Replicates the "result/completed but item stuck" case.
        _streams.StampActivity(item.Id, staleUpdatedAt + TimeSpan.FromMinutes(2));

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        // Working without a preempt checkpoint maps to Queued.
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("no progress", after.LastError);

        var release = Assert.Single(_slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.Equal(item.Id, release.WorkItemId);

        // Registry row reclaimed (worker not held).
        Assert.Empty(await _registry.ListAsync());
        // Item re-queued for dispatch.
        Assert.Equal(1, _queue.Count);
    }

    // ── (b) no-stream-pre-agent-hang ─────────────────────────────────────────

    [Fact]
    public async Task Watchdog_NoStreamPreAgentHang_RecoversAndReclaimsSlot()
    {
        // Simulates 739f9bb3: 87m on the pool, NO agent-stream dir ever created
        // because the wedge happened during pre-agent setup (VM provision /
        // repo mount). No stream activity at all → fall back to UpdatedAt.
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(90);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);
        // No stream activity stamped — _streams returns 0 files.

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(1, after.RecoveryAttempts);

        var release = Assert.Single(_slotReleaser.Releases);
        Assert.Equal(workerId, release.WorkerId);
        Assert.Equal(item.Id, release.WorkItemId);

        Assert.Empty(await _registry.ListAsync());
        Assert.Equal(1, _queue.Count);
    }

    // ── Progress within window keeps slot ────────────────────────────────────

    [Fact]
    public async Task Watchdog_RecentStreamActivity_LeavesWorkerAlone()
    {
        // Item.UpdatedAt is stale but stream has been written recently — agent
        // is making progress; do not recover.
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);
        _streams.StampActivity(item.Id, DateTimeOffset.UtcNow.AddMinutes(-2));

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Empty(_slotReleaser.Releases);
        // Registry row preserved.
        Assert.Single(await _registry.ListAsync());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_RecentUpdatedAt_LeavesWorkerAlone()
    {
        var freshUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var item = MakeItem(WorkItemState.Auditing, freshUpdatedAt);
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);
        // No stream activity at all — UpdatedAt within window is enough.

        await _watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
    }

    // ── State recovery mapping (mirrors DeadWorkerReaper) ────────────────────

    [Theory]
    [InlineData(WorkItemState.Reworking, WorkItemState.Queued)]
    [InlineData(WorkItemState.Auditing, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Merging, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Merged)]
    [InlineData(WorkItemState.ReworkingForConflict, WorkItemState.AuditPassed)]
    public async Task Watchdog_StuckMidFlightState_MapsToExpectedRecoveryState(
        WorkItemState fromState, WorkItemState expectedTo)
    {
        var item = MakeItem(fromState, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(expectedTo, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
    }

    // ── Preempt-checkpointed Working items stay Working ──────────────────────

    [Fact]
    public async Task Watchdog_StuckWorkingWithPreemptCheckpoint_ResumesFromCheckpoint()
    {
        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45))
            with { PreemptCheckpoint = "preempt-ref/abc123" };
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        // Preempt checkpoint is preserved; state remains Working so the next
        // pickup resumes from the captured ref rather than restarting work.
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal("preempt-ref/abc123", after.PreemptCheckpoint);
        Assert.Equal(1, after.RecoveryAttempts);
    }

    // ── Off-by-default and disabled paths ────────────────────────────────────

    [Fact]
    public async Task Watchdog_DisabledByZeroTimeout_TakesNoAction()
    {
        var disabledOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.Zero,
            CheckInterval = TimeSpan.FromMinutes(1),
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, disabledOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);
        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
    }

    [Fact]
    public async Task Watchdog_AutoRecoverDisabled_ParksItemAtNeedsOperatorInput()
    {
        var parkOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            AutoRecover = false,
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, parkOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);
        var item = MakeItem(WorkItemState.Auditing, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, after!.State);
        Assert.Contains("auto-recover disabled", after.LastError);
        Assert.Empty(await _registry.ListAsync());
        // No re-enqueue when parked.
        Assert.Equal(0, _queue.Count);
    }

    // ── Terminal / not-watched states are skipped ────────────────────────────

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.Queued)]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.WaitingForQuotaReset)]
    public async Task Watchdog_NonWatchedStates_TakesNoActionEvenIfStale(WorkItemState state)
    {
        var item = MakeItem(state, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(state, after!.State);
        Assert.Empty(_slotReleaser.Releases);
    }

    // ── Webhook event fires on recovery ──────────────────────────────────────

    [Fact]
    public async Task Watchdog_FiresRecoveredWebhook_OnAutoRecover()
    {
        var item = MakeItem(WorkItemState.Auditing, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
        Assert.NotNull(evt.WorkItem);
        Assert.Equal(item.Id, evt.WorkItem!.Id);
    }

    // ── DEPENDENCY-CHAIN PRESERVATION (the critical requirement) ─────────────

    [Fact]
    public async Task Watchdog_RecoveringChainHead_RestoresCascadeCancelledDependents()
    {
        // Build A → B → C: A is the wedged chain-head; B and C were previously
        // cascade-cancelled (reason=ParentCascaded) when A was operator-cancelled
        // before being uncancelled into this wedged-Working state. The test
        // confirms the watchdog's recovery restores B and C to Queued with
        // their dependsOn intact, instead of leaving them silently stranded.
        var staleAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var a = MakeItem(WorkItemState.Working, staleAt);
        var b = MakeItem(WorkItemState.Cancelled, DateTimeOffset.UtcNow.AddHours(-1),
            dependsOn: [a.Id],
            cancellationReason: WorkItemCancellationReason.ParentCascaded,
            lastError: "parent dependency cancelled");
        var c = MakeItem(WorkItemState.Cancelled, DateTimeOffset.UtcNow.AddHours(-1),
            dependsOn: [b.Id],
            cancellationReason: WorkItemCancellationReason.ParentCascaded,
            lastError: "parent dependency cancelled");
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await _store.CreateAsync(c);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), a.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        // A recovered.
        var aAfter = await _store.GetAsync(a.Id);
        Assert.Equal(WorkItemState.Queued, aAfter!.State);

        // B and C restored to Queued, dependsOn preserved.
        var bAfter = await _store.GetAsync(b.Id);
        var cAfter = await _store.GetAsync(c.Id);
        Assert.Equal(WorkItemState.Queued, bAfter!.State);
        Assert.Equal(WorkItemState.Queued, cAfter!.State);
        Assert.Null(bAfter.CancellationReason);
        Assert.Null(cAfter.CancellationReason);
        Assert.Equal([a.Id], bAfter.DependsOn);
        Assert.Equal([b.Id], cAfter.DependsOn);
        // The dependsOn gate is preserved — only A is in the kicked queue;
        // B and C will become eligible when A reaches a satisfying state.
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_RecoveringHead_DoesNotResurrectOperatorCancelledDependents()
    {
        // Operator-cancelled descendants stay Cancelled even when their parent
        // is recovered. Only ParentCascaded items are restored.
        var staleAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var a = MakeItem(WorkItemState.Working, staleAt);
        var b = MakeItem(WorkItemState.Cancelled, DateTimeOffset.UtcNow.AddHours(-1),
            dependsOn: [a.Id],
            cancellationReason: WorkItemCancellationReason.OperatorRequested);
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), a.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var bAfter = await _store.GetAsync(b.Id);
        Assert.Equal(WorkItemState.Cancelled, bAfter!.State);
        Assert.Equal(WorkItemCancellationReason.OperatorRequested, bAfter.CancellationReason);
    }

    [Fact]
    public async Task Watchdog_RecoveringHead_DoesNotRestoreDependentsBlockedByOtherFailures()
    {
        // A chain A → C and B → C where A is the wedged head, B is independently
        // Failed (a real blocker). C is ParentCascaded. Restoring C would make
        // it eligible to run with a failed dep, so leave C parked.
        var staleAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var a = MakeItem(WorkItemState.Working, staleAt);
        var b = MakeItem(WorkItemState.Failed, DateTimeOffset.UtcNow.AddHours(-1));
        var c = MakeItem(WorkItemState.Cancelled, DateTimeOffset.UtcNow.AddHours(-1),
            dependsOn: [a.Id, b.Id],
            cancellationReason: WorkItemCancellationReason.ParentCascaded);
        await _store.CreateAsync(a);
        await _store.CreateAsync(b);
        await _store.CreateAsync(c);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), a.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var cAfter = await _store.GetAsync(c.Id);
        // C remains Cancelled — its B dependency is genuinely failed; restoring
        // would let it run against a broken prerequisite.
        Assert.Equal(WorkItemState.Cancelled, cAfter!.State);
    }

    // ── Duplicate-recovery guard ─────────────────────────────────────────────

    [Fact]
    public async Task Watchdog_SecondSweep_DoesNotDoubleRecoverSameWorker()
    {
        var item = MakeItem(WorkItemState.Auditing, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);
        // Pretend the dispatcher hasn't picked it up yet — re-stamp a worker row
        // with the same id (production won't, but the test guards the bookkeeping).
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);
        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, after!.State);
        // RecoveryAttempts incremented exactly once.
        Assert.Equal(1, after.RecoveryAttempts);
        // Slot released exactly once.
        Assert.Single(_slotReleaser.Releases);
    }

    // ── Recently-started items get a grace period ────────────────────────────

    [Fact]
    public async Task Watchdog_RecentlyStartedItem_GetsGracePeriod()
    {
        // A just-picked-up item whose StartedAt is fresh but UpdatedAt is stale
        // (e.g. requeued from an old run) must not be recovered before it has
        // had a chance to make progress.
        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromHours(2))
            with { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Empty(_slotReleaser.Releases);
    }

    // ── No worker rows → no-op ───────────────────────────────────────────────

    [Fact]
    public async Task Watchdog_NoLiveWorkers_DoesNothing()
    {
        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromHours(2));
        await _store.CreateAsync(item);
        // No worker row planted.

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Empty(_slotReleaser.Releases);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Lets the test stamp synthetic stream-file activity per work item.
    /// Honours only the API the watchdog reads: <see cref="ListAsync"/>
    /// with file CapturedAt timestamps.
    /// </summary>
    private sealed class StaleStreamStore : IAgentStreamStore
    {
        private readonly Dictionary<WorkItemId, DateTimeOffset> _activity = [];

        public AgentStreamsOptions Options { get; } = new() { Enabled = true, Path = "/tmp/codeybox-test-streams" };

        public void StampActivity(WorkItemId id, DateTimeOffset at) => _activity[id] = at;

        public Task<AgentStreamCapture?> BeginCaptureAsync(WorkItemId workItemId, string phase, int iteration, CancellationToken ct = default)
            => Task.FromResult<AgentStreamCapture?>(null);

        public Task<IReadOnlyList<AgentStreamFile>> ListAsync(
            WorkItemId workItemId, int limit = AgentStreamStore.DefaultListLimit,
            bool includeLineCount = false, CancellationToken ct = default)
        {
            if (!_activity.TryGetValue(workItemId, out var at))
                return Task.FromResult<IReadOnlyList<AgentStreamFile>>([]);
            IReadOnlyList<AgentStreamFile> files =
            [
                new AgentStreamFile("work-1-abc123.jsonl", "work", 1, 1, null, at),
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

        public bool TryReleaseRecoveredWorkerSlot(string workerId, WorkItemId? workItemId, string reason)
        {
            Releases.Add((workerId, workItemId, reason));
            return true;
        }
    }
}

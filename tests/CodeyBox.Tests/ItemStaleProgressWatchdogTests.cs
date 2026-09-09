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
    private readonly CancellationRegistry _cancellations;
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
        _cancellations = new CancellationRegistry(CancellationToken.None);
        _time = new FakeTimeProvider(new DateTimeOffset(2026, 06, 12, 12, 00, 00, TimeSpan.Zero));
        _watchdog = new ItemStaleProgressWatchdog(
            _store, _queue, _registry,
            _opts,
            NullLogger<ItemStaleProgressWatchdog>.Instance,
            _webhooks,
            _slotReleaser,
            cancellations: _cancellations,
            timeProvider: _time);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        _cancellations.Dispose();
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

    private WorkItem WithClaimedAgentTurnCheckpoint(WorkItem item)
    {
        var phase = item.State switch
        {
            WorkItemState.Working => AgentTurnResumePhase.Work,
            WorkItemState.Reworking => AgentTurnResumePhase.Rework,
            _ => throw new ArgumentOutOfRangeException(
                nameof(item),
                item.State,
                "Claimed agent-turn test checkpoints require Working or Reworking state."),
        };
        var checkpoint = new AgentTurnResumeCheckpoint(
                AgentKind.Claude,
                "claude/default",
                modelId: null,
                reasoningMode: null,
                nativeSessionId: null,
                item.State,
                phase,
                iteration: phase == AgentTurnResumePhase.Rework ? 1 : null,
                item.PromptRevision,
                _time.GetUtcNow().AddMinutes(-10))
            .ClaimDispatch(Guid.Parse("77b7d18d-5969-4c03-9792-604c93a98ad2"));
        var archive = new AgentTurnScratchpadArchive([0x1f, 0x8b, 0x08, 0x00]);
        var checkpointRef = AgentTurnCheckpointRef.Create(
            item.Id,
            new string('a', 40),
            archive);
        return item with
        {
            PreemptedAt = _time.GetUtcNow().AddMinutes(-10),
            PreemptCheckpoint = checkpointRef.Value,
            AgentTurnResumeCheckpoint = checkpoint,
        };
    }

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

        using var registration = _cancellations.Register(item.Id);
        Assert.False(registration.Token.IsCancellationRequested);

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
        Assert.True(registration.Token.IsCancellationRequested);
        Assert.Equal(CancellationRequestKind.Recovery, _cancellations.GetRequestKind(item.Id));

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

    [Theory]
    [InlineData(WorkItemState.Planning, WorkItemState.Queued)]
    [InlineData(WorkItemState.PlanReview, WorkItemState.PlanReview)]
    public async Task Sweep_FrozenPlanningStateUpdatedAt_RecoversAndRequeues(
        WorkItemState frozenState,
        WorkItemState expectedState)
    {
        var item = MakeItem(
            frozenState,
            updatedAt: _time.GetUtcNow().AddMinutes(-95)) with
        {
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = frozenState is WorkItemState.Planning or WorkItemState.PlanReview
                ? _time.GetUtcNow().AddMinutes(-100)
                : null,
        };
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(expectedState, after!.State);
        if (expectedState == WorkItemState.Queued)
        {
            Assert.Null(after.PlanArtifact);
            Assert.Null(after.PlanGeneratedAt);
            Assert.Null(after.PlanReviewedAt);
            Assert.Null(after.PlanReviewSummary);
        }
        else
        {
            Assert.Equal(ValidPlan, after.PlanArtifact);
        }
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Contains("item-stale", after.LastError);
        Assert.Equal(1, _queue.Count);
        Assert.Contains(_webhooks.Events, e => e.Event == "work_item.recovered");
    }

    [Fact]
    public async Task Sweep_RecoveredParent_RestoresParentCascadedDependents()
    {
        var parent = MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-95),
            workBranch: "codeybox/auto/work-parent");
        var child = MakeItem(WorkItemState.Cancelled, updatedAt: _time.GetUtcNow().AddMinutes(-90)) with
        {
            DependsOn = [parent.Id],
            CancellationReason = WorkItemCancellationReason.ParentCascaded,
            LastError = "parent dependency cancelled",
            StartedAt = null,
        };
        await _store.CreateAsync(parent);
        await _store.CreateAsync(child);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var recoveredParent = await _store.GetAsync(parent.Id);
        var restoredChild = await _store.GetAsync(child.Id);
        Assert.Equal(WorkItemState.Queued, recoveredParent!.State);
        Assert.Equal(WorkItemState.Queued, restoredChild!.State);
        Assert.Null(restoredChild.CancellationReason);
        Assert.Null(restoredChild.LastError);
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
    public async Task RecoverItemAsync_RowAdvancedSinceSnapshot_SkipsRecoveryAndDoesNotCancelWorker()
    {
        var staleSnapshot = MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/auto/work-progress-race");
        await _store.CreateAsync(staleSnapshot);

        var advanced = staleSnapshot with
        {
            UpdatedAt = _time.GetUtcNow(),
            StartedAt = _time.GetUtcNow().AddMinutes(-1),
        };
        await _store.UpdateAsync(advanced);

        using var registration = _cancellations.Register(staleSnapshot.Id);

        var result = await _watchdog.RecoverItemAsync(
            staleSnapshot,
            "operator: stale snapshot",
            CancellationToken.None);

        Assert.False(result.Recovered);
        Assert.Contains("advanced", result.Error);
        Assert.False(registration.Token.IsCancellationRequested);

        var after = await _store.GetAsync(staleSnapshot.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(advanced.UpdatedAt, after.UpdatedAt);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task RecoverItemAsync_ClaimedCheckpoint_WaitsForLocalPipelineToBecomeInactive()
    {
        var item = WithClaimedAgentTurnCheckpoint(MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/claimed-local"));
        await _store.CreateAsync(item);
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "claimed-local-worker",
            HostName = "host",
            ProcessId = 1001,
            StartedAt = _time.GetUtcNow().AddMinutes(-100),
            LastHeartbeatAt = _time.GetUtcNow(),
            CurrentWorkItemId = item.Id.ToString(),
        });

        var registration = _cancellations.Register(item.Id);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationObserver = registration.Token.Register(
            () => cancellationObserved.TrySetResult());

        var recoveryTask = _watchdog.RecoverItemAsync(
            item,
            "operator: fence claimed local pipeline",
            CancellationToken.None);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var whilePipelineActive = await _store.GetAsync(item.Id);
        Assert.Equal(
            item.AgentTurnResumeCheckpoint?.DispatchClaimId,
            whilePipelineActive?.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Equal(0, whilePipelineActive?.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Equal(CancellationRequestKind.Recovery, _cancellations.GetRequestKind(item.Id));

        registration.Dispose();
        var result = await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Recovered, result.Error);
        var recovered = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, recovered?.State);
        Assert.Equal(item.PreemptCheckpoint, recovered?.PreemptCheckpoint);
        Assert.NotNull(recovered?.AgentTurnResumeCheckpoint);
        Assert.Null(recovered!.AgentTurnResumeCheckpoint!.DispatchClaimId);
        Assert.Equal(1, recovered.AgentTurnResumeCheckpoint.AttemptCount);
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Equal(1, _queue.Count);
        var release = Assert.Single(_slotReleaser.Releases);
        Assert.Equal("claimed-local-worker", release.WorkerId);
        Assert.Empty(await _registry.ListAsync());
    }

    [Fact]
    public async Task RecoverItemAsync_ClaimedCheckpointWithoutLocalOwner_FailsClosed()
    {
        var item = WithClaimedAgentTurnCheckpoint(MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/claimed-remote"));
        await _store.CreateAsync(item);
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "claimed-remote-worker",
            HostName = "remote-host",
            ProcessId = 2002,
            StartedAt = _time.GetUtcNow().AddMinutes(-100),
            LastHeartbeatAt = _time.GetUtcNow(),
            CurrentWorkItemId = item.Id.ToString(),
        });

        var result = await _watchdog.RecoverItemAsync(
            item,
            "operator: remote claimed checkpoint",
            CancellationToken.None);

        Assert.False(result.Recovered);
        Assert.Contains("remote or unfenceable", result.Error, StringComparison.Ordinal);
        var after = await _store.GetAsync(item.Id);
        Assert.Equal(item.State, after?.State);
        Assert.Equal(item.UpdatedAt, after?.UpdatedAt);
        Assert.Equal(
            item.AgentTurnResumeCheckpoint?.DispatchClaimId,
            after?.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Equal(0, after?.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
    }

    [Fact]
    public async Task RecoverItemAsync_ClaimedCheckpointWithoutCancellationRegistry_FailsClosed()
    {
        var item = WithClaimedAgentTurnCheckpoint(MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/claimed-unfenceable"));
        await _store.CreateAsync(item);
        using var unfenceableWatchdog = new ItemStaleProgressWatchdog(
            _store,
            _queue,
            _registry,
            _opts,
            NullLogger<ItemStaleProgressWatchdog>.Instance,
            _webhooks,
            _slotReleaser,
            cancellations: null,
            timeProvider: _time);

        var result = await unfenceableWatchdog.RecoverItemAsync(
            item,
            "operator: no local fencing capability",
            CancellationToken.None);

        Assert.False(result.Recovered);
        Assert.Contains("no local cancellation registry", result.Error, StringComparison.Ordinal);
        var after = await _store.GetAsync(item.Id);
        Assert.Equal(item.UpdatedAt, after?.UpdatedAt);
        Assert.Equal(
            item.AgentTurnResumeCheckpoint?.DispatchClaimId,
            after?.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_slotReleaser.Releases);
    }

    [Fact]
    public async Task RecoverItemAsync_ClaimedCheckpointOwnerDoesNotQuiesce_FailsClosedAtBound()
    {
        _opts.PostAgentTransitionTimeout = TimeSpan.FromMilliseconds(50);
        var item = WithClaimedAgentTurnCheckpoint(MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/claimed-timeout"));
        await _store.CreateAsync(item);
        var registration = _cancellations.Register(item.Id);

        var result = await _watchdog.RecoverItemAsync(
                item,
                "operator: claimed pipeline ignores cancellation",
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Recovered);
        Assert.Contains("did not quiesce", result.Error, StringComparison.Ordinal);
        Assert.True(registration.Token.IsCancellationRequested);
        Assert.Equal(CancellationRequestKind.Recovery, _cancellations.GetRequestKind(item.Id));
        var after = await _store.GetAsync(item.Id);
        Assert.Equal(item.UpdatedAt, after?.UpdatedAt);
        Assert.Equal(
            item.AgentTurnResumeCheckpoint?.DispatchClaimId,
            after?.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Equal(0, after?.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_slotReleaser.Releases);

        registration.Dispose();
    }

    [Fact]
    public async Task RecoverItemAsync_ClaimedCheckpointAdvancesWhileQuiescing_DoesNotOverwritePipeline()
    {
        var item = WithClaimedAgentTurnCheckpoint(MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/claimed-advanced"));
        await _store.CreateAsync(item);
        var registration = _cancellations.Register(item.Id);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationObserver = registration.Token.Register(
            () => cancellationObserved.TrySetResult());

        var recoveryTask = _watchdog.RecoverItemAsync(
            item,
            "operator: pipeline advances during quiescence",
            CancellationToken.None);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pipelineUpdate = item with
        {
            AgentTurnResumeCheckpoint = item.AgentTurnResumeCheckpoint!.ReleaseDispatchClaim(),
            UpdatedAt = _time.GetUtcNow().AddSeconds(1),
        };
        await _store.UpdateAsync(pipelineUpdate);
        registration.Dispose();

        var result = await recoveryTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Recovered);
        Assert.Contains("advanced", result.Error, StringComparison.Ordinal);
        var after = await _store.GetAsync(item.Id);
        Assert.Equal(pipelineUpdate.UpdatedAt, after?.UpdatedAt);
        Assert.Null(after?.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Equal(0, after?.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_slotReleaser.Releases);
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

    [Fact]
    public async Task Sweep_AfterRecovery_RepickupAdvancesUpdatedAt_NextWedgeIsDetected()
    {
        // Regression: _recoveredItemsThisProcess must expire once the item is
        // re-picked up and UpdatedAt advances past the recorded mark.
        // Otherwise the same item can wedge again later in the same process
        // and become permanently invisible to the detector, never hitting
        // the bounded-then-escalate cap.
        var item = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/auto/work-chronic");
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);
        var first = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, first!.State);
        Assert.Equal(1, first.RecoveryAttempts);

        // Simulate the re-pickup advancing UpdatedAt and the worker wedging
        // again past the threshold. Advance the clock past the recorded
        // recovery mark so the marker is stale.
        _time.Advance(TimeSpan.FromMinutes(120));
        var wedgedAgain = first with
        {
            State = WorkItemState.Working,
            // UpdatedAt is now > the recorded recovery stamp but still > the
            // 90 min stale window from "now".
            UpdatedAt = _time.GetUtcNow().AddMinutes(-100),
            StartedAt = _time.GetUtcNow().AddMinutes(-110),
        };
        await _store.UpdateAsync(wedgedAgain);

        await _watchdog.RunOnceAsync(CancellationToken.None);
        var second = await _store.GetAsync(item.Id);
        // Marker must have expired, second recovery attempt fires.
        Assert.Equal(WorkItemState.Queued, second!.State);
        Assert.Equal(2, second.RecoveryAttempts);
    }

    // ── Acceptance (d) for rerunnable job types: cap escalates them too ──────

    [Fact]
    public async Task Sweep_CheckAndActAtCap_EscalatesToNeedsOperatorInput()
    {
        // CheckAndAct items without a preempt checkpoint go through the
        // Build{CheckAndAct}Rerun branch of BuildStaleItemRecovery. That branch
        // must NOT bypass the bounded-then-escalate cap — otherwise a chronic
        // CheckAndAct wedge requeues forever and never escalates.
        var item = MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-95),
            recoveryAttempts: _opts.ItemStaleMaxRecoveryAttempts) with
        {
            JobType = JobType.CheckAndAct,
        };
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, after!.State);
        Assert.Equal(_opts.ItemStaleMaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Contains("MaxRecoveryAttempts", after.LastError);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Sweep_AgentControlAtCap_EscalatesToNeedsOperatorInput()
    {
        // Same bounded-then-escalate semantics for AgentControl items.
        var item = MakeItem(
            WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-95),
            recoveryAttempts: _opts.ItemStaleMaxRecoveryAttempts) with
        {
            JobType = JobType.AgentControl,
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Pause,
                Agent = AgentKind.Claude.Value,
                Reason = "test",
            },
        };
        await _store.CreateAsync(item);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.NeedsOperatorInput, after!.State);
        Assert.Equal(_opts.ItemStaleMaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Contains("MaxRecoveryAttempts", after.LastError);
        Assert.Equal(0, _queue.Count);
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

    // ── Per-agent ItemStaleTimeout overrides (crock batch-latency liveness) ──

    [Fact]
    public async Task PerAgentItemStaleOverride_SavesCrockItemFromGlobalCutoff()
    {
        // Headline acceptance criterion for crock runtime-enablement on this
        // watchdog: a crock work item legitimately parked waiting on an
        // Anthropic Message Batches API task (minutes-to-hours) must NOT be
        // recovered by the synchronous-agent default ItemStaleTimeout. The
        // per-agent override under
        // CodeyBox:WorkerProgressWatchdog:PerAgent:crock:ItemStaleTimeout
        // extends the per-item stale window for crock items only.
        _opts.ItemStaleTimeout = TimeSpan.FromMinutes(75);
        _opts.PerAgent["crock"] = new AgentWatchdogOverride
        {
            ItemStaleTimeout = TimeSpan.FromHours(8),
        };

        // Stale 100 minutes — well past the 75-min global default but inside
        // the 8h crock override. The watchdog must leave it alone.
        var crockItem = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100)) with
        { Agent = AgentKind.Crock };
        await _store.CreateAsync(crockItem);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(crockItem.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task PerAgentItemStaleOverride_StillRecoversCrockItemPastOverrideCeiling()
    {
        // Defence-in-depth: the per-agent override extends but does not
        // disable the watchdog. A crock item stale past the override window
        // still gets recovered — operators sized the override to the
        // realistic batch latency, not to "never kill crock".
        _opts.ItemStaleTimeout = TimeSpan.FromMinutes(75);
        _opts.PerAgent["crock"] = new AgentWatchdogOverride
        {
            ItemStaleTimeout = TimeSpan.FromHours(2),
        };

        var crockItem = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddHours(-3),
            workBranch: "codeybox/auto/work-crock-stuck") with
        { Agent = AgentKind.Crock };
        await _store.CreateAsync(crockItem);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(crockItem.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
    }

    [Fact]
    public async Task PerAgentItemStaleOverride_DoesNotApplyToOtherAgents()
    {
        // The override is scoped to the configured kind. A Claude (or any
        // non-crock) item stale past the global default still gets recovered
        // even though a crock override is configured — defending against a
        // bug where an override entry silently widens the window for every
        // kind.
        _opts.ItemStaleTimeout = TimeSpan.FromMinutes(75);
        _opts.PerAgent["crock"] = new AgentWatchdogOverride
        {
            ItemStaleTimeout = TimeSpan.FromHours(8),
        };

        var claudeItem = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-100),
            workBranch: "codeybox/auto/work-claude-stuck") with
        { Agent = AgentKind.Claude };
        await _store.CreateAsync(claudeItem);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(claudeItem.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
    }

    [Fact]
    public async Task GlobalItemStaleTimeoutZero_PerAgentOverrideStillFires()
    {
        // Off-by-default + per-agent opt-in: the global ItemStaleTimeout=0
        // would normally short-circuit the sweep entirely (see
        // Sweep_DisabledByZeroTimeout above). The per-agent override is the
        // explicit opt-in for the kind, so the sweep must still execute when
        // one is configured.
        _opts.ItemStaleTimeout = TimeSpan.Zero;
        _opts.PerAgent["crock"] = new AgentWatchdogOverride
        {
            ItemStaleTimeout = TimeSpan.FromHours(2),
        };

        // Crock item stale past the override → should be recovered.
        var crockItem = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddHours(-3),
            workBranch: "codeybox/auto/work-crock-zero-global") with
        { Agent = AgentKind.Crock };
        await _store.CreateAsync(crockItem);

        // A non-crock item past the (zero, ergo disabled) global timeout
        // must NOT be touched — defending against the override accidentally
        // re-enabling the sweep for every kind.
        var claudeItem = MakeItem(WorkItemState.Working,
            updatedAt: _time.GetUtcNow().AddMinutes(-200)) with
        { Agent = AgentKind.Claude };
        await _store.CreateAsync(claudeItem);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var afterCrock = await _store.GetAsync(crockItem.Id);
        Assert.Equal(WorkItemState.Queued, afterCrock!.State);
        Assert.Equal(1, afterCrock.RecoveryAttempts);

        var afterClaude = await _store.GetAsync(claudeItem.Id);
        Assert.Equal(WorkItemState.Working, afterClaude!.State);
        Assert.Equal(0, afterClaude.RecoveryAttempts);
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

    private const string ValidPlan = """
        {
          "approach": "recover plan review",
          "files": ["output.txt"],
          "testStrategy": ["run tests"],
          "risks": ["none"],
          "satisfiesTask": "keeps plan review resumable"
        }
        """;
}

using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Webhooks;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="DeadWorkerReaper.SweepStrandedItemsAsync"/> — the
/// startup-only safety net that catches mid-flight items orphaned when the
/// orchestrator crashed before its worker-heartbeat row was ever written.
///
/// <para>
/// Distinct from the heartbeat-based <see cref="DeadWorkerReaper.RunOnceAsync"/>
/// (which only sees items whose worker rows exist and have gone stale): the
/// startup sweep enumerates items in worker-owned states, cross-references
/// against the live worker registry, and routes orphans through the same
/// shared per-item recovery helper. Items still held by a live worker are
/// untouched.
/// </para>
/// </summary>
public sealed class StartupStrandedItemSweepTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-stranded-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;
    private readonly SqliteWorkerRegistry _registry;
    private readonly InMemoryTaskQueue _queue;
    private readonly CapturingWebhookDispatcher _webhooks;
    private readonly DeadWorkerOptions _opts;
    private readonly DeadWorkerReaper _reaper;

    public StartupStrandedItemSweepTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
        _registry = new SqliteWorkerRegistry(_dbPath);
        _queue = new InMemoryTaskQueue();
        _webhooks = new CapturingWebhookDispatcher();
        _opts = new DeadWorkerOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(5),
            DeadWorkerThreshold = TimeSpan.FromSeconds(15),
            CheckInterval = TimeSpan.FromMinutes(60),
            MaxRecoveryAttempts = 2,
        };
        _reaper = new DeadWorkerReaper(
            _registry, _store, _queue, _opts,
            NullLogger<DeadWorkerReaper>.Instance,
            _webhooks);
    }

    public void Dispose()
    {
        _store.Dispose();
        _registry.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(WorkItemState state, int recoveryAttempts = 0, string? preemptCheckpoint = null) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
        RecoveryAttempts = recoveryAttempts,
        PreemptCheckpoint = preemptCheckpoint,
        StartedAt = state == WorkItemState.Queued ? null : DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    [Fact]
    public async Task Sweep_WorkingItem_NoWorker_NoCheckpoint_ReclaimsPreservingBranch()
    {
        // Spec change 2026-06-12: orphaned Working items with no live worker
        // are reclaimed (requeued preserving the work branch) instead of
        // marked Failed — the bare repo holds the work branch across the
        // restart, so the next pickup re-rebases existing commits onto
        // current upstream main rather than discarding partial progress.
        const string workBranch = "codeybox/auto/work-orphan";
        var item = MakeItem(WorkItemState.Working) with { WorkBranch = workBranch };
        await _store.CreateAsync(item);

        // No worker row in the registry — the orchestrator crashed before the
        // heartbeat row was written. The periodic reaper would never see this.
        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Equal(workBranch, after.WorkBranch);
        Assert.True(after.PreserveWorkBranchOnQueuedPickup);
        Assert.Null(after.StartedAt);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Contains("orchestrator restarted while work was in progress", after.LastError);
        Assert.Equal(1, _queue.Count);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
    }

    [Fact]
    public async Task Sweep_CheckAndActWorkingItem_NoWorker_NoCheckpoint_Requeues()
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            JobType = JobType.CheckAndAct,
            Check = new CheckAndActSpec
            {
                Question = "Is action needed?",
                OnYes = new OnYesActionSpec
                {
                    Title = "Act",
                    Prompt = "Act on the check.",
                },
            },
        };
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Null(after.StartedAt);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Null(after.LastError);
        Assert.Equal(1, _queue.Count);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
        Assert.NotNull(evt.WorkItem);
        Assert.Equal(item.Id, evt.WorkItem!.Id);
    }

    [Fact]
    public async Task Sweep_AgentControlWorkingItem_NoWorker_NoCheckpoint_Requeues()
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            JobType = JobType.AgentControl,
            AgentControl = new AgentControlSpec
            {
                Action = AgentControlAction.Pause,
                Agent = AgentKind.Claude.Value,
                Reason = "reserve quota",
            },
        };
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Queued, after.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Null(after.StartedAt);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Null(after.LastError);
        Assert.Equal(item.AgentControl, after.AgentControl);
        Assert.Equal(1, _queue.Count);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
        Assert.NotNull(evt.WorkItem);
        Assert.Equal(item.Id, evt.WorkItem!.Id);
    }

    [Fact]
    public async Task Sweep_WorkingItem_NoWorker_WithCheckpoint_ReenqueuesForResume()
    {
        var item = MakeItem(
            WorkItemState.Working,
            preemptCheckpoint: "refs/heads/codeybox/preempt/abc123");
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        // Preempt-checkpointed items stay in Working state so the pipeline's
        // resume path picks up the checkpoint ref on next pickup.
        Assert.Equal(WorkItemState.Working, after.State);
        Assert.Equal(item.PreemptCheckpoint, after.PreemptCheckpoint);
        // StartedAt is cleared so the item doesn't appear in-flight to budget queries.
        Assert.Null(after.StartedAt);
        // Checkpoint resume is still a recovery; repeated checkpoint resumes
        // without phase completion must reach MaxRecoveryAttempts.
        Assert.Equal(1, after.RecoveryAttempts);
        // Re-enqueued for the dispatcher to pick up.
        Assert.Equal(1, _queue.Count);
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Reworking)]
    public async Task Sweep_PreemptCheckpoint_AtRecoveryCap_Abandons(WorkItemState state)
    {
        var item = MakeItem(
            state,
            recoveryAttempts: _opts.MaxRecoveryAttempts,
            preemptCheckpoint: "refs/heads/codeybox/preempt/abc123");
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, after!.State);
        Assert.Equal("exceeded MaxRecoveryAttempts", after.LastError);
        Assert.Equal(_opts.MaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Equal(0, _queue.Count);
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Reworking)]
    public async Task Sweep_WorkingOrReworkingItem_AtRecoveryCap_NoCheckpoint_Abandons(
        WorkItemState state)
    {
        // Startup dead-worker recovery shares the dead-letter budget with the
        // periodic reaper. Once the cap is exceeded, it must reach the permanent
        // abandoned state operators monitor rather than parking in the stale-item
        // watchdog's NeedsOperatorInput triage state.
        var item = MakeItem(state, recoveryAttempts: _opts.MaxRecoveryAttempts);
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, after!.State);
        Assert.Equal(_opts.MaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Equal("exceeded MaxRecoveryAttempts", after.LastError);
        Assert.Equal(0, _queue.Count);
    }

    [Theory]
    [InlineData(WorkItemState.Reworking, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Auditing, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Merging, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.ReworkingForConflict, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Merged)]
    public async Task Sweep_ResumableState_NoWorker_RecoversToMappedState(
        WorkItemState fromState, WorkItemState expectedTo)
    {
        var item = MakeItem(fromState);
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(expectedTo, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Sweep_ItemHeldByLiveWorker_IsLeftAlone()
    {
        // A live worker owns this item — represented by a registry row whose
        // CurrentWorkItemId points at it. The sweep must NOT clobber it.
        var item = MakeItem(WorkItemState.Auditing);
        await _store.CreateAsync(item);

        var liveWorker = new WorkerRegistration
        {
            WorkerId = "live-worker",
            HostName = "host",
            ProcessId = 1234,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CurrentWorkItemId = item.Id.ToString(),
        };
        await _registry.RegisterAsync(liveWorker);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Auditing, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
        Assert.Empty(_webhooks.Events);
    }

    [Fact]
    public async Task Sweep_OverRecoveryCap_AbandonsAsExceeded()
    {
        // Item already at the cap; the next recovery transitions it to
        // AbandonedAfterRecoveryAttempts with the shared MaxRecoveryAttempts
        // reason verified for both reaper and sweep entry points.
        var item = MakeItem(WorkItemState.Auditing, recoveryAttempts: _opts.MaxRecoveryAttempts);
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, after!.State);
        Assert.Equal("exceeded MaxRecoveryAttempts", after.LastError);
        Assert.Equal(_opts.MaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Sweep_PhaseBoundaryStates_NoWorker_RedispatchesAndCountsRecoveryAttempt()
    {
        var states = new[]
        {
            WorkItemState.WorkComplete,
            WorkItemState.AuditPassed,
            WorkItemState.Merged,
        };
        var ids = new List<WorkItemId>();
        foreach (var state in states)
        {
            var item = MakeItem(state) with { LastError = "diagnostic", RecoveryAttempts = 1 };
            await _store.CreateAsync(item);
            ids.Add(item.Id);
        }

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        for (int i = 0; i < states.Length; i++)
        {
            var after = await _store.GetAsync(ids[i]);
            Assert.Equal(states[i], after!.State);
            Assert.Equal(2, after.RecoveryAttempts);
            Assert.Null(after.LastError);
        }
        Assert.Equal(states.Length, _queue.Count);
        Assert.Equal(states.Length, _webhooks.Events.Count);
    }

    [Theory]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merged)]
    public async Task Sweep_PhaseBoundaryStateAtRecoveryCap_Abandons(WorkItemState state)
    {
        var item = MakeItem(state, recoveryAttempts: _opts.MaxRecoveryAttempts);
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, after.State);
        Assert.Equal("exceeded MaxRecoveryAttempts", after.LastError);
        Assert.Equal(_opts.MaxRecoveryAttempts + 1, after.RecoveryAttempts);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Sweep_OnlySkipsNonReaperOwnedStates()
    {
        // Queued, terminal, and parked states are not owned by dead-worker
        // recovery; the sweep must leave them alone.
        var states = new[]
        {
            WorkItemState.Queued,
            WorkItemState.Done,
            WorkItemState.Failed,
            WorkItemState.Cancelled,
            WorkItemState.AuditFailed,
            WorkItemState.NeedsOperatorInput,
            WorkItemState.WaitingForQuotaReset,
        };
        var ids = new List<WorkItemId>();
        foreach (var s in states)
        {
            var item = MakeItem(s);
            await _store.CreateAsync(item);
            ids.Add(item.Id);
        }

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        for (int i = 0; i < states.Length; i++)
        {
            var after = await _store.GetAsync(ids[i]);
            Assert.Equal(states[i], after!.State);
            Assert.Equal(0, after.RecoveryAttempts);
        }
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Sweep_FiresRecoveryWebhook_WithOrchestratorRestartReason()
    {
        var item = MakeItem(WorkItemState.Merging);
        await _store.CreateAsync(item);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        var evt = Assert.Single(_webhooks.Events);
        Assert.Equal("work_item.recovered", evt.Event);
        Assert.NotNull(evt.WorkItem);
        Assert.Equal(item.Id, evt.WorkItem!.Id);
    }

    [Fact]
    public async Task Sweep_MultipleStrandedItems_AcrossStates_AllRecovered()
    {
        // One of each worker-owned state, no workers registered.
        var working = MakeItem(WorkItemState.Working);
        var reworking = MakeItem(WorkItemState.Reworking);
        var auditing = MakeItem(WorkItemState.Auditing);
        var merging = MakeItem(WorkItemState.Merging);
        await _store.CreateAsync(working);
        await _store.CreateAsync(reworking);
        await _store.CreateAsync(auditing);
        await _store.CreateAsync(merging);

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        // Spec change 2026-06-12: Working-without-checkpoint orphans are
        // reclaimed (requeued preserving the work branch) rather than failed.
        // Reworking still maps to WorkComplete (resumable state with intact
        // work-phase commits — no work-branch preservation needed there).
        Assert.Equal(WorkItemState.Queued, (await _store.GetAsync(working.Id))!.State);
        Assert.Equal(WorkItemState.WorkComplete, (await _store.GetAsync(reworking.Id))!.State);
        Assert.Equal(WorkItemState.WorkComplete, (await _store.GetAsync(auditing.Id))!.State);
        Assert.Equal(WorkItemState.AuditPassed, (await _store.GetAsync(merging.Id))!.State);
        // Four enqueues now: Working→Queued, Reworking→WorkComplete, Auditing→WorkComplete, Merging→AuditPassed.
        Assert.Equal(4, _queue.Count);
    }

    [Fact]
    public async Task Sweep_MixOfLiveAndStrandedItems_RecoversOnlyStranded()
    {
        // One item is owned by a live worker; another is stranded.
        var live = MakeItem(WorkItemState.Auditing);
        var stranded = MakeItem(WorkItemState.Auditing);
        await _store.CreateAsync(live);
        await _store.CreateAsync(stranded);
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "alive",
            HostName = "host",
            ProcessId = 7,
            StartedAt = DateTimeOffset.UtcNow,
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            CurrentWorkItemId = live.Id.ToString(),
        });

        await _reaper.SweepStrandedItemsAsync(CancellationToken.None);

        Assert.Equal(WorkItemState.Auditing, (await _store.GetAsync(live.Id))!.State);
        Assert.Equal(WorkItemState.WorkComplete, (await _store.GetAsync(stranded.Id))!.State);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Sweep_RunOnceAsync_StillHandlesStaleWorkerRows_NoRegression()
    {
        // Plant a stale worker row pointing at a Working item. The periodic
        // reaper's RunOnceAsync path (claim-via-stale-heartbeat) must still
        // recover it through the shared helper exactly as before.
        var item = MakeItem(WorkItemState.Auditing);
        await _store.CreateAsync(item);
        await _registry.RegisterAsync(new WorkerRegistration
        {
            WorkerId = "stale-worker",
            HostName = "crashed",
            ProcessId = 99,
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-1),
            CurrentWorkItemId = item.Id.ToString(),
        });

        await _reaper.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.WorkComplete, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Equal(1, _queue.Count);
        Assert.Empty(await _registry.ListAsync());
    }

    [Fact]
    public async Task OrchestratorStartup_RunsSweepBeforeDispatch_StrandedItemNeverRacedByFreshPickup()
    {
        // End-to-end: a Working item with no worker row sits stranded. When
        // the orchestrator starts, the startup sweep must reclaim it BEFORE
        // the worker pool begins picking from the queue. If the sweep ran too
        // late, the item could be picked up in its mid-flight Working state
        // and executed against a half-broken pipeline state.
        //
        // Spec change 2026-06-12: the sweep now reclaims orphaned Working
        // items by requeueing them to Queued (work branch preserved), so the
        // observable race-free signal is the transition to Queued (or Done
        // once the pipeline picks the requeued item up). The transition out
        // of Working is what we verify here.
        var item = MakeItem(WorkItemState.Working) with { WorkBranch = "codeybox/auto/work-orphan-startup" };
        await _store.CreateAsync(item);

        var pipeline = new ImmediateDonePipeline(_store);
        var cancellations = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1, MaxRecoveryAttempts = 5 };

        using var svc = new OrchestratorService(
            _queue, _store, pipeline, cancellations, opts,
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: _registry,
            deadWorkerOpts: _opts,
            reaper: _reaper);

        await svc.StartAsync(CancellationToken.None);

        // BackgroundService.ExecuteAsync runs on the thread pool; under heavy
        // CPU contention (parallel audit suites) the startup-sweep chain can
        // take several seconds to flush before the item leaves Working.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
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
        if (pipeline.EntryStates.TryGetValue(item.Id, out var entryState))
            Assert.Equal(WorkItemState.Queued, entryState);
        // The observed pipeline entry state proves the orphaned mid-flight
        // Working state was reclaimed before dispatch; successful completion
        // then clears RecoveryAttempts as real progress.
    }

    [Fact]
    public async Task OrchestratorStartup_WaitsForRecoveryInputBeforeStartupReaperAndReplay()
    {
        var item = MakeItem(WorkItemState.Working) with { WorkBranch = "codeybox/auto/work-orphan-barrier" };
        await _store.CreateAsync(item);

        var barrier = new TestStartupRecoveryBarrier();
        var pipeline = new ImmediateDonePipeline(_store);
        var cancellations = new CancellationRegistry(CancellationToken.None);
        var opts = new OrchestratorOptions { MaxConcurrentWorkers = 1, MaxRecoveryAttempts = 5 };

        using var svc = new OrchestratorService(
            _queue, _store, pipeline, cancellations, opts,
            NullLogger<OrchestratorService>.Instance,
            workerRegistry: _registry,
            deadWorkerOpts: _opts,
            reaper: _reaper,
            startupRecoveryBarrier: barrier,
            startupRecoveryCompletion: barrier);

        await svc.StartAsync(CancellationToken.None);
        await barrier.WaitObserved.WaitAsync(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        var beforeSignal = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, beforeSignal!.State);
        Assert.DoesNotContain(item.Id, pipeline.Executed);

        barrier.MarkRecoveryInputReady();

        // Spec change 2026-06-12: the orphan is reclaimed (requeued) instead
        // of failed; the immediate-Done pipeline then advances the requeued
        // item to Done. Wait for any post-Working state.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        WorkItem? final = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            final = await _store.GetAsync(item.Id);
            if (final is not null && final.State != WorkItemState.Working) break;
            await Task.Delay(30);
        }

        await barrier.InitialRecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(30));
        await svc.StopAsync(CancellationToken.None);

        Assert.NotNull(final);
        Assert.NotEqual(WorkItemState.Working, final.State);
        Assert.Equal(1, barrier.InitialRecoveryCompletedSignals);
    }

    private sealed class TestStartupRecoveryBarrier :
        IStartupRecoveryInputBarrier,
        IStartupRecoveryInputSink,
        IStartupInitialRecoveryBarrier,
        IStartupInitialRecoverySink
    {
        private readonly TaskCompletionSource _recoveryInputReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _initialRecoveryCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _waitObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _initialRecoveryCompletedSignals;

        public Task WaitObserved => _waitObserved.Task;
        public int InitialRecoveryCompletedSignals => Volatile.Read(ref _initialRecoveryCompletedSignals);

        public Task RecoveryInputReady
        {
            get
            {
                _waitObserved.TrySetResult();
                return _recoveryInputReady.Task;
            }
        }

        public Task InitialRecoveryCompleted => _initialRecoveryCompleted.Task;

        public void MarkRecoveryInputReady() => _recoveryInputReady.TrySetResult();

        public void MarkInitialRecoveryCompleted()
        {
            Interlocked.Increment(ref _initialRecoveryCompletedSignals);
            _initialRecoveryCompleted.TrySetResult();
        }
    }
}

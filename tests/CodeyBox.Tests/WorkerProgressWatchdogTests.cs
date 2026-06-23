using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox;
using DiagProcess = System.Diagnostics.Process;

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

    [Theory]
    [InlineData(WorkItemState.Planning, true)]
    [InlineData(WorkItemState.PlanReview, true)]
    [InlineData(WorkItemState.PlanApproved, false)]
    [InlineData(WorkItemState.Working, true)]
    [InlineData(WorkItemState.WorkComplete, false)]
    public void IsWatchedState_IncludesActivePlanningStates(WorkItemState state, bool expected)
    {
        Assert.Equal(expected, WorkerProgressWatchdog.IsWatchedState(state));
    }

    [Fact]
    public async Task Watchdog_PlanningRecoveryToQueued_ClearsPlanFields()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Planning, staleUpdatedAt) with
        {
            PlanArtifact = """
                {
                  "approach": "stale",
                  "files": ["old.txt"],
                  "testStrategy": ["old"],
                  "risks": ["old"],
                  "satisfiesTask": "old"
                }
                """,
            PlanGeneratedAt = staleUpdatedAt.AddMinutes(1),
            PlanReviewedAt = staleUpdatedAt.AddMinutes(2),
            PlanReviewSummary = "stale approval",
        };
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var recovered = await _store.GetAsync(item.Id);
        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.Queued, recovered!.State);
        Assert.Null(recovered.PlanArtifact);
        Assert.Null(recovered.PlanGeneratedAt);
        Assert.Null(recovered.PlanReviewedAt);
        Assert.Null(recovered.PlanReviewSummary);
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(1));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        Assert.True(await condition(), "condition was not met before the timeout elapsed");
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

    [Fact]
    public async Task Watchdog_WorkingToQueuedRecoveryClearsPreserveBranchIntent()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt) with
        {
            WorkBranch = "feature/operator-resume",
            PreserveWorkBranchOnQueuedPickup = true,
        };
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);
        using var cancellations = new CancellationRegistry();
        using var registration = cancellations.Register(item.Id);
        using var watchdog = new WorkerProgressWatchdog(
            _registry,
            _store,
            _queue,
            _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams,
            _webhooks,
            _slotReleaser,
            cancellationRegistry: cancellations);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Null(after.WorkBranch);
        Assert.False(after.PreserveWorkBranchOnQueuedPickup);
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

    [Fact]
    public async Task Watchdog_SuspendedItem_SkipsRecovery()
    {
        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(90))
            with
        {
            SuspendedVmName = "vm-watchdog-startup-owned",
            SuspendedAt = DateTimeOffset.UtcNow,
        };
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Working, after.State);
        Assert.Equal("vm-watchdog-startup-owned", after.SuspendedVmName);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Equal(0, _queue.Count);
        var worker = Assert.Single(await _registry.ListAsync());
        Assert.Equal(workerId, worker.WorkerId);
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

    [Fact]
    public async Task Watchdog_BusyButQuietProcessActivity_LeavesWorkerAlone()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        var activity = new ScriptedWorkerProgressActivitySource(new WorkerProgressActivity("process-cpu"));
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: activity);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Equal(1, activity.Calls);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_BusyButQuietTaggedProcessWithDefaultActivitySource_LeavesWorkerAlone()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);

        // Deterministic CPU signal: an actively-scheduled (R-state) tagged
        // process. Injecting the sample reader exercises the same
        // ObserveAsync -> TryObserveProcessCpu -> watchdog decision path as a
        // real busy process, but without depending on a real `yes` process
        // winning a /proc CPU tick on a contended host (the previous source of
        // flakiness — under suite-wide saturation the burner was starved and
        // the "process-cpu" signal never primed within the deadline).
        var cpuReader = ActiveProcessSample("active-cpu-process");
        var activitySource = new DefaultWorkerProgressActivitySource(
            activeSandboxProvider: null,
            processCpuSampleReader: cpuReader);
        var activityProbe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: true,
            ActiveSandboxProgressSignalEnabled: false);
        var primed = await activitySource.ObserveAsync(
            WorkerForItem(workerId, item.Id),
            item.Id,
            activityProbe,
            CancellationToken.None);
        Assert.NotNull(primed);
        Assert.Equal("process-cpu", primed!.Reason);

        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            ProcessCpuProgressSignalEnabled = true,
            ActiveSandboxProgressSignalEnabled = false,
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: activitySource);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_TrulyHungWithNoActivity_Recovers()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        var activity = new ScriptedWorkerProgressActivitySource(null);
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: activity);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Equal(1, activity.Calls);
        Assert.Single(_slotReleaser.Releases);
        Assert.Empty(await _registry.ListAsync());
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_ActivitySignalsDisabled_RecoversWithoutCallingActivitySource()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            ProcessCpuProgressSignalEnabled = false,
            ActiveSandboxProgressSignalEnabled = false,
        };
        var activity = new ScriptedWorkerProgressActivitySource(new WorkerProgressActivity("process-cpu"));
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: activity);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(0, activity.Calls);
        Assert.Single(_slotReleaser.Releases);
    }

    [Fact]
    public async Task Watchdog_DisabledActivitySignalsIgnoreCachedProgress()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            ProcessCpuProgressSignalEnabled = true,
            ActiveSandboxProgressSignalEnabled = false,
        };
        var activity = new ScriptedWorkerProgressActivitySource(new WorkerProgressActivity("process-cpu"));
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, () => opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: activity);

        await watchdog.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Equal(1, activity.Calls);

        opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            ProcessCpuProgressSignalEnabled = false,
            ActiveSandboxProgressSignalEnabled = false,
        };

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(1, activity.Calls);
        Assert.Single(_slotReleaser.Releases);
    }

    [Fact]
    public async Task Watchdog_ActiveSandboxOwnershipWithDefaultActivitySource_LeavesWorkerAlone()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        var provider = new ActiveSandboxProviderStub(item.Id);
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: new DefaultWorkerProgressActivitySource(provider));

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_StableActiveSandboxOwnershipDoesNotMaskRecoveryAfterTimeout()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMilliseconds(100),
            CheckInterval = TimeSpan.FromMilliseconds(50),
            ProcessCpuProgressSignalEnabled = false,
            ActiveSandboxProgressSignalEnabled = true,
        };
        var provider = new ActiveSandboxProviderStub(item.Id);
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: new DefaultWorkerProgressActivitySource(provider));

        await watchdog.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_slotReleaser.Releases);

        await Task.Delay(TimeSpan.FromMilliseconds(250));
        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Single(_slotReleaser.Releases);
        Assert.Empty(await _registry.ListAsync());
    }

    [Fact]
    public async Task Watchdog_ActivityDisappearingAfterPriorSignal_RecoversAfterTimeout()
    {
        var staleUpdatedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var item = MakeItem(WorkItemState.Working, staleUpdatedAt);
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        // ProgressTimeout is intentionally wider than the test loop wall-clock
        // so the second sweep (with the activity signal cleared but well inside
        // the timeout window) is unambiguously the "still fresh" case. A tighter
        // window (e.g. 200ms) flakes under parallel-suite CPU contention because
        // the second RunOnceAsync's `now` capture can land outside the prior
        // sweep's tolerance even though no real progress has elapsed.
        var opts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromSeconds(2),
            CheckInterval = TimeSpan.FromMilliseconds(50),
            ProcessCpuProgressSignalEnabled = true,
            ActiveSandboxProgressSignalEnabled = false,
        };
        var activity = new MutableWorkerProgressActivitySource
        {
            Activity = new WorkerProgressActivity("process-cpu"),
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: activity);

        await watchdog.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
        Assert.Equal(1, activity.Calls);

        activity.Activity = null;
        await watchdog.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
        Assert.Equal(1, activity.Calls);

        await Task.Delay(TimeSpan.FromMilliseconds(2500));
        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(2, activity.Calls);
        Assert.Single(_slotReleaser.Releases);
        Assert.Empty(await _registry.ListAsync());
    }

    [Fact]
    public async Task DefaultActivitySource_WorkItemProcessCpuDelta_CountsAsProgress()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var itemId = WorkItemId.New();
        // One stable tagged process set whose CPU ticks strictly increase across
        // samples, exercising the pure "accrued utime/stime counts as progress"
        // branch (TryConfirmImmediateCpuProgress on first observe, then the
        // steady-state CpuTicks > previous.CpuTicks delta). HasActiveProcessState
        // is false throughout so the R-state shortcut is bypassed and the delta
        // path is the only thing under test. Scripting the samples removes the
        // dependency on a real `yes` process winning a /proc CPU tick between two
        // reads, which starved under suite-wide CPU saturation and null-failed
        // the first Assert.NotNull (the previous source of flakiness).
        var cpuReader = ScriptedCpuSamples(
            new DefaultWorkerProgressActivitySource.ProcessCpuSample(
                CpuTicks: 100,
                ProcessSetSignature: "cpu-delta-process",
                HasActiveProcessState: false,
                HasConfirmedProgress: false),
            new DefaultWorkerProgressActivitySource.ProcessCpuSample(
                CpuTicks: 200,
                ProcessSetSignature: "cpu-delta-process",
                HasActiveProcessState: false,
                HasConfirmedProgress: false),
            new DefaultWorkerProgressActivitySource.ProcessCpuSample(
                CpuTicks: 300,
                ProcessSetSignature: "cpu-delta-process",
                HasActiveProcessState: false,
                HasConfirmedProgress: false));
        var source = new DefaultWorkerProgressActivitySource(
            activeSandboxProvider: null,
            processCpuSampleReader: cpuReader,
            initialCpuSampleAttempts: 1);
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: true,
            ActiveSandboxProgressSignalEnabled: false);
        var worker = WorkerForItem("cpu-test", itemId);

        var first = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("process-cpu", first!.Reason);

        var second = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        Assert.NotNull(second);
        Assert.Equal("process-cpu", second!.Reason);
    }

    [Fact]
    public async Task DefaultActivitySource_RunnableProcessOnInitialConfirmation_CountsAsProgress()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var itemId = WorkItemId.New();
        var source = new DefaultWorkerProgressActivitySource(
            activeSandboxProvider: null,
            processCpuSampleReader: ScriptedCpuSamples(
                new DefaultWorkerProgressActivitySource.ProcessCpuSample(10, "pid:1", HasActiveProcessState: true, HasConfirmedProgress: false),
                new DefaultWorkerProgressActivitySource.ProcessCpuSample(10, "pid:1", HasActiveProcessState: true, HasConfirmedProgress: false)),
            initialCpuSampleAttempts: 1);
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: true,
            ActiveSandboxProgressSignalEnabled: false);

        var activity = await source.ObserveAsync(
            WorkerForItem("runnable-initial-test", itemId),
            itemId,
            probe,
            CancellationToken.None);

        Assert.NotNull(activity);
        Assert.Equal("process-cpu", activity!.Reason);
    }

    [Fact]
    public async Task DefaultActivitySource_RunnableProcessWithoutCpuDelta_CountsAsProgress()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var itemId = WorkItemId.New();
        var source = new DefaultWorkerProgressActivitySource(
            activeSandboxProvider: null,
            processCpuSampleReader: ScriptedCpuSamples(
                new DefaultWorkerProgressActivitySource.ProcessCpuSample(10, "pid:1", HasActiveProcessState: false, HasConfirmedProgress: false),
                new DefaultWorkerProgressActivitySource.ProcessCpuSample(10, "pid:1", HasActiveProcessState: false, HasConfirmedProgress: false),
                new DefaultWorkerProgressActivitySource.ProcessCpuSample(10, "pid:1", HasActiveProcessState: true, HasConfirmedProgress: false)),
            initialCpuSampleAttempts: 1);
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: true,
            ActiveSandboxProgressSignalEnabled: false);
        var worker = WorkerForItem("runnable-later-test", itemId);

        var baseline = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        var runnable = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);

        Assert.Null(baseline);
        Assert.NotNull(runnable);
        Assert.Equal("process-cpu", runnable!.Reason);
    }

    [Fact]
    public async Task DefaultActivitySource_IdleWorkItemProcess_DoesNotReportCpuDelta()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var itemId = WorkItemId.New();
        using var process = StartIdleProcess(itemId);
        try
        {
            var source = new DefaultWorkerProgressActivitySource();
            var probe = new WorkerProgressActivityProbe(
                ProcessCpuProgressSignalEnabled: true,
                ActiveSandboxProgressSignalEnabled: false);
            var worker = WorkerForItem("idle-process-test", itemId);

            var first = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
            Assert.Null(first);

            WorkerProgressActivity? idleActivity = null;
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(75));
                idleActivity = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
                if (idleActivity is null)
                    break;
            }

            Assert.Null(idleActivity);
        }
        finally
        {
            StopProcess(process);
        }
    }

    [Fact]
    public async Task DefaultActivitySource_RunningProcessStateCountsAsProgressWithoutCpuDelta()
    {
        var itemId = WorkItemId.New();
        var active = false;
        var source = new DefaultWorkerProgressActivitySource(
            activeSandboxProvider: null,
            processCpuSampleReader: (WorkItemId _, out DefaultWorkerProgressActivitySource.ProcessCpuSample sample) =>
            {
                sample = new DefaultWorkerProgressActivitySource.ProcessCpuSample(
                    CpuTicks: 42,
                    ProcessSetSignature: "123:456",
                    HasActiveProcessState: active,
                    HasConfirmedProgress: false);
                return true;
            });
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: true,
            ActiveSandboxProgressSignalEnabled: false);
        var worker = WorkerForItem("active-state-test", itemId);

        var baseline = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        active = true;
        var activeState = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);

        Assert.Null(baseline);
        Assert.NotNull(activeState);
        Assert.Equal("process-cpu", activeState!.Reason);
        Assert.True(DefaultWorkerProgressActivitySource.IsActiveProcessState('R'));
        Assert.False(DefaultWorkerProgressActivitySource.IsActiveProcessState('D'));
        Assert.False(DefaultWorkerProgressActivitySource.IsActiveProcessState('S'));
    }

    [Fact]
    public async Task DefaultActivitySource_ActiveProcessStateDuringInitialConfirmationCountsAsProgress()
    {
        var itemId = WorkItemId.New();
        var sampleReads = 0;
        var source = new DefaultWorkerProgressActivitySource(
            activeSandboxProvider: null,
            processCpuSampleReader: (WorkItemId _, out DefaultWorkerProgressActivitySource.ProcessCpuSample sample) =>
            {
                sampleReads++;
                sample = new DefaultWorkerProgressActivitySource.ProcessCpuSample(
                    CpuTicks: 42,
                    ProcessSetSignature: "123:456",
                    HasActiveProcessState: sampleReads > 1,
                    HasConfirmedProgress: false);
                return true;
            });
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: true,
            ActiveSandboxProgressSignalEnabled: false);
        var worker = WorkerForItem("active-state-confirm-test", itemId);

        var activity = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);

        Assert.NotNull(activity);
        Assert.Equal("process-cpu", activity!.Reason);
        Assert.True(sampleReads >= 2);
    }

    [Fact]
    public async Task Watchdog_IdleTaggedProcess_Recovers()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        using var process = StartIdleProcess(item.Id);
        try
        {
            var idleOpts = new WorkerProgressWatchdogOptions
            {
                ProgressTimeout = TimeSpan.FromMilliseconds(20),
                CheckInterval = TimeSpan.FromMilliseconds(10),
                ProcessCpuProgressSignalEnabled = true,
                ActiveSandboxProgressSignalEnabled = false,
            };
            var watchdog = new WorkerProgressWatchdog(
                _registry, _store, _queue, idleOpts,
                NullLogger<WorkerProgressWatchdog>.Instance,
                _streams, _webhooks, _slotReleaser,
                activitySource: new DefaultWorkerProgressActivitySource());

            await watchdog.RunOnceAsync(CancellationToken.None);

            var after = await _store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Queued, after!.State);
            Assert.Single(_slotReleaser.Releases);
        }
        finally
        {
            StopProcess(process);
        }
    }

    [Fact]
    public async Task DefaultActivitySource_ProcessCpuFlagFalse_IgnoresBusyTaggedProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var itemId = WorkItemId.New();
        using var process = StartBusyProcess(itemId);
        try
        {
            var source = new DefaultWorkerProgressActivitySource();
            var probe = new WorkerProgressActivityProbe(
                ProcessCpuProgressSignalEnabled: false,
                ActiveSandboxProgressSignalEnabled: false);

            await Task.Delay(TimeSpan.FromMilliseconds(75));
            var activity = await source.ObserveAsync(WorkerForItem("cpu-disabled-test", itemId), itemId, probe, CancellationToken.None);

            Assert.Null(activity);
        }
        finally
        {
            StopProcess(process);
        }
    }

    [Fact]
    public async Task DefaultActivitySource_ActiveSandboxFlagFalse_IgnoresProviderEntry()
    {
        var itemId = WorkItemId.New();
        var source = new DefaultWorkerProgressActivitySource(new ActiveSandboxProviderStub(itemId));
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: false,
            ActiveSandboxProgressSignalEnabled: false);

        var activity = await source.ObserveAsync(WorkerForItem("sandbox-disabled-test", itemId), itemId, probe, CancellationToken.None);

        Assert.Null(activity);
    }

    [Fact]
    public async Task DefaultActivitySource_ActiveSandboxSetChange_CountsAsProgress()
    {
        var itemId = WorkItemId.New();
        var provider = new ActiveSandboxProviderStub(itemId);
        var source = new DefaultWorkerProgressActivitySource(provider);
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: false,
            ActiveSandboxProgressSignalEnabled: true);
        var worker = WorkerForItem("sandbox-replacement-test", itemId);

        var first = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        provider.SandboxId = "replacement";
        var replacement = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("active-sandbox", first!.Reason);
        Assert.NotNull(replacement);
        Assert.Equal("active-sandbox-change", replacement!.Reason);
    }

    [Fact]
    public async Task DefaultActivitySource_ActiveSandboxStatusChange_CountsAsProgress()
    {
        var itemId = WorkItemId.New();
        var provider = new ActiveSandboxProviderStub(itemId);
        var source = new DefaultWorkerProgressActivitySource(provider);
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: false,
            ActiveSandboxProgressSignalEnabled: true);
        var worker = WorkerForItem("sandbox-status-test", itemId);

        var first = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        provider.Status = "busy";
        var statusChange = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("active-sandbox", first!.Reason);
        Assert.NotNull(statusChange);
        Assert.Equal("active-sandbox-change", statusChange!.Reason);
    }

    [Fact]
    public async Task DefaultActivitySource_StableActiveSandboxSet_DoesNotRepeatProgress()
    {
        var itemId = WorkItemId.New();
        var provider = new ActiveSandboxProviderStub(itemId);
        var source = new DefaultWorkerProgressActivitySource(provider);
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: false,
            ActiveSandboxProgressSignalEnabled: true);
        var worker = WorkerForItem("stable-sandbox-test", itemId);

        var first = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        var second = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal("active-sandbox", first!.Reason);
        Assert.Null(second);
    }

    [Fact]
    public async Task DefaultActivitySource_MismatchedWorkerItem_ReturnsNoActivity()
    {
        var itemId = WorkItemId.New();
        var otherItemId = WorkItemId.New();
        var source = new DefaultWorkerProgressActivitySource(new ActiveSandboxProviderStub(itemId));
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: false,
            ActiveSandboxProgressSignalEnabled: true);

        var activity = await source.ObserveAsync(WorkerForItem("mismatched-worker-test", otherItemId), itemId, probe, CancellationToken.None);

        Assert.Null(activity);
    }

    [Fact]
    public async Task Watchdog_UnrelatedActiveSandbox_DoesNotMaskRecovery()
    {
        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        var provider = new ActiveSandboxProviderStub(WorkItemId.New());
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: new DefaultWorkerProgressActivitySource(provider));

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Single(_slotReleaser.Releases);
    }

    [Fact]
    public async Task Watchdog_ProcessStampedForAnotherItem_DoesNotMaskRecovery()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        using var process = StartBusyProcess(WorkItemId.New());
        try
        {
            var watchdog = new WorkerProgressWatchdog(
                _registry, _store, _queue, _opts,
                NullLogger<WorkerProgressWatchdog>.Instance,
                _streams, _webhooks, _slotReleaser,
                activitySource: new DefaultWorkerProgressActivitySource());

            await watchdog.RunOnceAsync(CancellationToken.None);

            var after = await _store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Queued, after!.State);
            Assert.Single(_slotReleaser.Releases);
        }
        finally
        {
            StopProcess(process);
        }
    }

    [Fact]
    public async Task Watchdog_ActivityProbeFailure_TreatsAsNoActivityAndRecovers()
    {
        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            activitySource: new ThrowingWorkerProgressActivitySource());

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Single(_slotReleaser.Releases);
    }

    [Fact]
    public async Task DefaultActivitySource_ProcessReplacement_CountsAsProgress()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var itemId = WorkItemId.New();
        // Two distinct, actively-scheduled process sets observed in sequence:
        // the first primes a confirmed-progress baseline; the second has a
        // different signature, exercising the "process replacement counts as
        // progress" branch. Scripting the samples removes the dependency on two
        // real `yes` processes each winning a /proc CPU tick under host load
        // (the previous source of flakiness).
        var cpuReader = ScriptedCpuSamples(
            new DefaultWorkerProgressActivitySource.ProcessCpuSample(
                CpuTicks: 100,
                ProcessSetSignature: "first-process-set",
                HasActiveProcessState: true,
                HasConfirmedProgress: false),
            new DefaultWorkerProgressActivitySource.ProcessCpuSample(
                CpuTicks: 200,
                ProcessSetSignature: "replacement-process-set",
                HasActiveProcessState: true,
                HasConfirmedProgress: false));
        var source = new DefaultWorkerProgressActivitySource(
            activeSandboxProvider: null,
            processCpuSampleReader: cpuReader);
        var probe = new WorkerProgressActivityProbe(
            ProcessCpuProgressSignalEnabled: true,
            ActiveSandboxProgressSignalEnabled: false);
        var worker = WorkerForItem("replacement-process-test", itemId);

        var first = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        Assert.NotNull(first);
        Assert.Equal("process-cpu", first!.Reason);

        var observed = await source.ObserveAsync(worker, itemId, probe, CancellationToken.None);
        Assert.NotNull(observed);
        Assert.Equal("process-cpu", observed!.Reason);
    }

    // ── State recovery mapping (mirrors DeadWorkerReaper) ────────────────────

    [Theory]
    [InlineData(WorkItemState.Reworking, WorkItemState.WorkComplete)]
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
            with
        { PreemptCheckpoint = "preempt-ref/abc123" };
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

    [Fact]
    public async Task Watchdog_StuckReworkingWithAgentTurnCheckpoint_PreservesPairedResumeState()
    {
        var item = MakeItem(WorkItemState.Reworking, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        var checkpoint = new AgentTurnResumeCheckpoint(
            AgentKind.Claude,
            "claude/account-a",
            modelId: null,
            reasoningMode: null,
            nativeSessionId: null,
            WorkItemState.Reworking,
            AgentTurnResumePhase.Rework,
            iteration: 2,
            item.PromptRevision,
            DateTimeOffset.UtcNow.AddMinutes(-10))
            .ClaimDispatch(Guid.Parse("ca1db018-7196-4d4c-9e56-7bd48ea3b3a8"));
        item = item with
        {
            PreemptedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}",
            AgentTurnResumeCheckpoint = checkpoint,
        };
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await _registry.RegisterAsync(WorkerForItem(workerId, item.Id));
        using var cancellations = new CancellationRegistry(CancellationToken.None);
        var registration = cancellations.Register(item.Id);
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            cancellationRegistry: cancellations);

        var recovery = watchdog.RunOnceAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(registration.Token.IsCancellationRequested));
            Assert.Equal(CancellationRequestKind.Recovery, cancellations.GetRequestKind(item.Id));
            Assert.False(recovery.IsCompleted);

            var whileOwnerActive = await _store.GetAsync(item.Id);
            Assert.Equal(checkpoint.DispatchClaimId, whileOwnerActive?.AgentTurnResumeCheckpoint?.DispatchClaimId);
        }
        finally
        {
            registration.Dispose();
        }
        await recovery;

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.Reworking, after!.State);
        Assert.Equal(item.PreemptCheckpoint, after.PreemptCheckpoint);
        Assert.Equal(checkpoint.AttemptCount, after.AgentTurnResumeCheckpoint?.AttemptCount);
        Assert.Null(after.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_RemoteOwnerWithDispatchClaim_FailsClosed()
    {
        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        var checkpoint = new AgentTurnResumeCheckpoint(
            AgentKind.Claude,
            "claude/account-a",
            modelId: null,
            reasoningMode: null,
            nativeSessionId: null,
            WorkItemState.Working,
            AgentTurnResumePhase.Work,
            iteration: 1,
            item.PromptRevision,
            DateTimeOffset.UtcNow.AddMinutes(-10))
            .ClaimDispatch(Guid.Parse("93168f15-8277-449e-8424-0e44514eea44"));
        item = item with
        {
            PreemptedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{item.Id}",
            AgentTurnResumeCheckpoint = checkpoint,
        };
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);
        using var cancellations = new CancellationRegistry(CancellationToken.None);
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, _opts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser,
            cancellationRegistry: cancellations);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(item.UpdatedAt, after?.UpdatedAt);
        Assert.Equal(checkpoint.DispatchClaimId, after?.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Equal(0, _queue.Count);
        Assert.Single(await _registry.ListAsync());
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
            with
        { StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
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

    [Fact]
    public async Task Watchdog_PeriodicSweep_WaitsForStartupRecoveryCompletion()
    {
        var barrier = new StartupRecoveryBarrier();
        var watchdog = new WorkerProgressWatchdog(
            _registry,
            _store,
            _queue,
            new WorkerProgressWatchdogOptions
            {
                ProgressTimeout = TimeSpan.FromMilliseconds(200),
                CheckInterval = TimeSpan.FromSeconds(30),
                AutoRecover = true,
            },
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams,
            _webhooks,
            _slotReleaser,
            startupRecoveryBarrier: barrier);

        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow.AddMinutes(-5));
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync("startup-resume-worker", item.Id);

        await watchdog.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(80));

            var beforeRelease = await _store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Working, beforeRelease!.State);
            Assert.Single(await _registry.ListAsync(CancellationToken.None));
            Assert.Equal(0, _queue.Count);

            barrier.MarkRecoveryInputReady();
            await Task.Delay(TimeSpan.FromMilliseconds(80));
            var afterInputReady = await _store.GetAsync(item.Id);
            Assert.Equal(WorkItemState.Working, afterInputReady!.State);
            Assert.Single(await _registry.ListAsync(CancellationToken.None));
            Assert.Equal(0, _queue.Count);

            barrier.MarkInitialRecoveryCompleted();
            // Wider deadline absorbs thread-pool starvation under parallel-suite
            // CPU contention; the happy path still resolves in milliseconds once
            // the barrier releases and the post-barrier sweep runs.
            await WaitUntilAsync(async () =>
            {
                var after = await _store.GetAsync(item.Id);
                return after?.State == WorkItemState.Queued && _queue.Count == 1;
            }, TimeSpan.FromSeconds(30));
        }
        finally
        {
            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await watchdog.StopAsync(stopCts.Token);
        }
    }

    // ── Multi-worker collateral survival (regression test) ───────────────────

    [Fact]
    public async Task Watchdog_RecoveringOneWedgedWorker_PreservesHealthyPeerRegistryRows()
    {
        // Regression: an earlier version of the watchdog claimed wedged workers
        // by calling ClaimDeadWorkersAsync(now), which deletes EVERY row whose
        // heartbeat is in the past (i.e. all healthy peers). The per-id claim
        // must only remove the targeted wedged row.
        var wedgedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45);
        var wedgedItem = MakeItem(WorkItemState.Working, wedgedAt);
        await _store.CreateAsync(wedgedItem);

        var healthyItem = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _store.CreateAsync(healthyItem);

        var wedgedWorkerId = Guid.NewGuid().ToString();
        var healthyWorkerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(wedgedWorkerId, wedgedItem.Id);
        await PlantHeartbeatingWorkerAsync(healthyWorkerId, healthyItem.Id);

        await _watchdog.RunOnceAsync(CancellationToken.None);

        // Only the wedged row is gone; the healthy peer's row is preserved.
        var remaining = await _registry.ListAsync();
        var survivor = Assert.Single(remaining);
        Assert.Equal(healthyWorkerId, survivor.WorkerId);

        // The healthy peer's item is untouched.
        var healthyAfter = await _store.GetAsync(healthyItem.Id);
        Assert.Equal(WorkItemState.Working, healthyAfter!.State);
        Assert.Equal(0, healthyAfter.RecoveryAttempts);

        // Only the wedged worker's slot was released.
        var release = Assert.Single(_slotReleaser.Releases);
        Assert.Equal(wedgedWorkerId, release.WorkerId);
    }

    [Fact]
    public async Task Watchdog_ParkPath_PreservesHealthyPeerRegistryRows()
    {
        // Same regression as above, repeated for the AutoRecover=false (park)
        // path which also used ClaimDeadWorkersAsync(now).
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

        var wedgedItem = MakeItem(WorkItemState.Auditing, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45));
        await _store.CreateAsync(wedgedItem);
        var healthyItem = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow.AddMinutes(-1));
        await _store.CreateAsync(healthyItem);
        var wedgedWorkerId = Guid.NewGuid().ToString();
        var healthyWorkerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(wedgedWorkerId, wedgedItem.Id);
        await PlantHeartbeatingWorkerAsync(healthyWorkerId, healthyItem.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var remaining = await _registry.ListAsync();
        var survivor = Assert.Single(remaining);
        Assert.Equal(healthyWorkerId, survivor.WorkerId);
    }

    // ── MaxRecoveryAttempts ceiling (mirrors DeadWorkerReaper) ────────────────

    [Fact]
    public async Task Watchdog_ExceedsMaxRecoveryAttempts_TransitionsToAbandoned()
    {
        var lowCeilingOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            MaxRecoveryAttempts = 2,
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, lowCeilingOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);

        // Already at the ceiling; the next watchdog hit will increment to 3 (> 2) and Fail.
        var item = MakeItem(WorkItemState.Auditing, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45))
            with
        { RecoveryAttempts = 2 };
        await _store.CreateAsync(item);
        var workerId = Guid.NewGuid().ToString();
        await PlantHeartbeatingWorkerAsync(workerId, item.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, after!.State);
        Assert.Equal(3, after.RecoveryAttempts);
        Assert.Contains("MaxRecoveryAttempts", after.LastError);
        // Slot still released and registry row still claimed even on terminal Fail.
        Assert.Single(_slotReleaser.Releases);
        Assert.Empty(await _registry.ListAsync());
        // Not re-enqueued — Failed items don't go back on the queue.
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_StuckWorkingWithPreemptCheckpoint_AtCapTransitionsToAbandoned()
    {
        var lowCeilingOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            MaxRecoveryAttempts = 2,
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, lowCeilingOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);

        var item = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45))
            with
        {
            PreemptCheckpoint = "refs/heads/codeybox/preempt/test",
            RecoveryAttempts = 2,
        };
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, after!.State);
        Assert.Equal(3, after.RecoveryAttempts);
        Assert.Null(after.PreemptCheckpoint);
        Assert.Single(_slotReleaser.Releases);
        Assert.Empty(await _registry.ListAsync());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_StuckWorkingWithRetainedSandbox_AtCapPreservesOperatorRecoveryBoundary()
    {
        var lowCeilingOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            MaxRecoveryAttempts = 2,
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, lowCeilingOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);

        var item = MakeItem(
            WorkItemState.Working,
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45)) with
        {
            RecoveryAttempts = 2,
        };
        var checkpoint = new AgentTurnResumeCheckpoint(
            AgentKind.Claude,
            "claude/default",
            modelId: null,
            reasoningMode: null,
            nativeSessionId: null,
            WorkItemState.Working,
            AgentTurnResumePhase.Work,
            iteration: null,
            item.PromptRevision,
            DateTimeOffset.UtcNow.AddMinutes(-10));
        var lease = new SandboxRecoveryLease(
            "incus",
            $"retained-{item.Id}",
            $"token-{item.Id}");
        item = item with
        {
            PreemptedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            AgentTurnResumeCheckpoint = checkpoint,
            AgentTurnRecoveryLease = lease,
        };
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, after!.State);
        Assert.Equal(3, after.RecoveryAttempts);
        Assert.Equal(checkpoint, after.AgentTurnResumeCheckpoint);
        Assert.Equal(lease, after.AgentTurnRecoveryLease);
        Assert.True(after.HasAgentTurnRecoveryBoundary);
        Assert.Single(_slotReleaser.Releases);
        Assert.Empty(await _registry.ListAsync());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_MaxRecoveryAttemptsZero_TreatsAsUnlimited()
    {
        var unlimitedOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            MaxRecoveryAttempts = 0,
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, unlimitedOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);

        var item = MakeItem(WorkItemState.Auditing, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(45))
            with
        { RecoveryAttempts = 999 };
        await _store.CreateAsync(item);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), item.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(item.Id);
        // 0 = unlimited → still recovers normally instead of Failing.
        Assert.Equal(WorkItemState.WorkComplete, after!.State);
        Assert.Equal(1000, after.RecoveryAttempts);
    }

    // ── Per-agent ProgressTimeout overrides (crock batch-latency liveness) ───

    [Fact]
    public async Task Watchdog_PerAgentProgressOverride_SavesCrockItemFromGlobalCutoff()
    {
        // The headline acceptance criterion for crock runtime-enablement:
        // a crock work item legitimately waiting on a minutes-to-hours batch
        // must NOT be killed by the synchronous-agent default ProgressTimeout
        // window. The per-agent override under
        // CodeyBox:WorkerProgressWatchdog:PerAgent:crock:ProgressTimeout
        // extends the per-worker liveness window for crock items only;
        // every other kind keeps the default.
        var overrideOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            AutoRecover = true,
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromHours(8),
                },
            },
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, overrideOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);

        // Stale 90 minutes — well past the global default but inside the
        // 8h crock override. The watchdog must leave it alone.
        var crockItem = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(90))
            with
        { Agent = AgentKind.Crock };
        await _store.CreateAsync(crockItem);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), crockItem.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(crockItem.Id);
        Assert.Equal(WorkItemState.Working, after!.State);
        Assert.Equal(0, after.RecoveryAttempts);
        Assert.Empty(_slotReleaser.Releases);
        Assert.Single(await _registry.ListAsync());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task Watchdog_PerAgentProgressOverride_StillRecoversCrockItemPastOverrideCeiling()
    {
        // Defence-in-depth: the per-agent override extends but does not
        // disable the watchdog. A crock item stale past the override window
        // still gets recovered — operators sized the override to the
        // realistic batch latency, not to "never kill crock".
        var overrideOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            AutoRecover = true,
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromHours(2),
                },
            },
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, overrideOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);

        var crockItem = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromHours(3))
            with
        { Agent = AgentKind.Crock };
        await _store.CreateAsync(crockItem);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), crockItem.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(crockItem.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Single(_slotReleaser.Releases);
    }

    [Fact]
    public async Task Watchdog_PerAgentProgressOverride_DoesNotApplyToOtherAgents()
    {
        // The override is scoped to the configured kind. A Claude (or any
        // non-crock) item stale past the global default still gets recovered
        // even though a crock override is configured — defending against a
        // bug where an override entry silently widens the window for every
        // kind.
        var overrideOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.FromMinutes(30),
            CheckInterval = TimeSpan.FromMinutes(1),
            AutoRecover = true,
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromHours(8),
                },
            },
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, overrideOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);

        var claudeItem = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(90))
            with
        { Agent = AgentKind.Claude };
        await _store.CreateAsync(claudeItem);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), claudeItem.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(claudeItem.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Equal(1, after.RecoveryAttempts);
        Assert.Single(_slotReleaser.Releases);
    }

    [Fact]
    public async Task Watchdog_GlobalProgressTimeoutZero_PerAgentOverrideStillFires()
    {
        // Off-by-default + per-agent opt-in: the global ProgressTimeout=0
        // would normally short-circuit the sweep entirely. The per-agent
        // override is the explicit opt-in for the kind, so the sweep must
        // still execute when one is configured. The non-crock branch of the
        // short-circuit is covered by Watchdog_DisabledByZeroTimeout above.
        var optInOpts = new WorkerProgressWatchdogOptions
        {
            ProgressTimeout = TimeSpan.Zero,
            CheckInterval = TimeSpan.FromMinutes(1),
            AutoRecover = true,
            PerAgent =
            {
                ["crock"] = new AgentWatchdogOverride
                {
                    ProgressTimeout = TimeSpan.FromHours(2),
                },
            },
        };
        var watchdog = new WorkerProgressWatchdog(
            _registry, _store, _queue, optInOpts,
            NullLogger<WorkerProgressWatchdog>.Instance,
            _streams, _webhooks, _slotReleaser);

        // Crock item stale past the override → should be recovered.
        var crockItem = MakeItem(WorkItemState.Working, DateTimeOffset.UtcNow - TimeSpan.FromHours(3))
            with
        { Agent = AgentKind.Crock };
        await _store.CreateAsync(crockItem);
        await PlantHeartbeatingWorkerAsync(Guid.NewGuid().ToString(), crockItem.Id);

        await watchdog.RunOnceAsync(CancellationToken.None);

        var after = await _store.GetAsync(crockItem.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
        Assert.Single(_slotReleaser.Releases);
    }

    // ── Test doubles ─────────────────────────────────────────────────────────

    private static DiagProcess StartBusyProcess(WorkItemId itemId)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("exec yes >/dev/null");
        psi.Environment[SandboxConventions.WorkItemIdEnvironmentVariable] = itemId.ToString();
        var process = DiagProcess.Start(psi)
            ?? throw new InvalidOperationException("failed to start busy test process");
        // Leave the CPU burner at the platform default priority. These tests
        // intentionally assert that an active tagged process remains observable
        // even when the rest of the suite is busy; lowering its priority makes
        // the assertion depend on scheduler luck rather than watchdog logic.
        return process;
    }

    private static DiagProcess StartIdleProcess(WorkItemId itemId)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sleep",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("10");
        psi.Environment[SandboxConventions.WorkItemIdEnvironmentVariable] = itemId.ToString();
        return DiagProcess.Start(psi)
            ?? throw new InvalidOperationException("failed to start idle test process");
    }

    private static void StopProcess(DiagProcess process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.WaitForExit(1000);
        }
        catch { }
    }

    private static WorkerRegistration WorkerForItem(string workerId, WorkItemId itemId) => new()
    {
        WorkerId = workerId,
        HostName = Environment.MachineName,
        ProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.UtcNow,
        LastHeartbeatAt = DateTimeOffset.UtcNow,
        CurrentWorkItemId = itemId.ToString(),
    };

    // Deterministic CPU sample reader: reports a single actively-scheduled
    // (R-state) tagged process on every probe. This drives the
    // HasActiveProcessState "process-cpu" branch without a real OS process, so
    // the watchdog's "active process leaves the worker alone" contract is
    // tested independently of host scheduler luck.
    private static DefaultWorkerProgressActivitySource.ProcessCpuSampleReader ActiveProcessSample(
        string processSignature, long cpuTicks = 100)
    {
        return (WorkItemId _, out DefaultWorkerProgressActivitySource.ProcessCpuSample sample) =>
        {
            sample = new DefaultWorkerProgressActivitySource.ProcessCpuSample(
                CpuTicks: cpuTicks,
                ProcessSetSignature: processSignature,
                HasActiveProcessState: true,
                HasConfirmedProgress: false);
            return true;
        };
    }

    private static DefaultWorkerProgressActivitySource.ProcessCpuSampleReader ScriptedCpuSamples(
        params DefaultWorkerProgressActivitySource.ProcessCpuSample[] samples)
    {
        var queue = new Queue<DefaultWorkerProgressActivitySource.ProcessCpuSample>(samples);
        return (WorkItemId _, out DefaultWorkerProgressActivitySource.ProcessCpuSample sample) =>
        {
            if (queue.Count == 0)
            {
                sample = default;
                return false;
            }

            sample = queue.Dequeue();
            return true;
        };
    }

    private sealed class ScriptedWorkerProgressActivitySource(WorkerProgressActivity? activity) : IWorkerProgressActivitySource
    {
        public int Calls { get; private set; }

        public ValueTask<WorkerProgressActivity?> ObserveAsync(
            WorkerRegistration worker,
            WorkItemId itemId,
            WorkerProgressActivityProbe probe,
            CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(activity);
        }
    }

    private sealed class MutableWorkerProgressActivitySource : IWorkerProgressActivitySource
    {
        public WorkerProgressActivity? Activity { get; set; }
        public int Calls { get; private set; }

        public ValueTask<WorkerProgressActivity?> ObserveAsync(
            WorkerRegistration worker,
            WorkItemId itemId,
            WorkerProgressActivityProbe probe,
            CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(Activity);
        }
    }

    private sealed class ThrowingWorkerProgressActivitySource : IWorkerProgressActivitySource
    {
        public ValueTask<WorkerProgressActivity?> ObserveAsync(
            WorkerRegistration worker,
            WorkItemId itemId,
            WorkerProgressActivityProbe probe,
            CancellationToken ct) =>
            throw new InvalidOperationException("activity probe failed");
    }

    private sealed class ActiveSandboxProviderStub(WorkItemId itemId) : IActiveSandboxProgressProvider
    {
        public string SandboxId { get; set; } = "noop";
        public string Status { get; set; } = "active";

        public IReadOnlyList<ActiveSandboxProgress> SnapshotActiveSandboxProgress()
            => [new ActiveSandboxProgress(itemId, SandboxId, Status)];
    }

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

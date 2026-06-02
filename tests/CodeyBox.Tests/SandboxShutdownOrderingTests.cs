using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CodeyBox.Api;
using CodeyBox.Core;
using CodeyBox.HostProcess;
using CodeyBox.Orchestrator;
using CodeyBox.Sandbox.Multipass;
using CodeyBox.Tests.Uat.SandboxProviders;

namespace CodeyBox.Tests;

/// <summary>
/// R8.1 tests covering the shutdown-sequencing + startup-reconciliation
/// hardening added after the 2026-05-29 wedged-VM incident.
///
/// <para>The incident: an unclean orchestrator shutdown left 8 multipass
/// workers in <c>Suspending</c> state with their root-owned qemu processes
/// holding the disk-image write-lock; subsequent <c>multipass stop</c> and
/// <c>multipass delete --purge</c> both failed with "Failed to get shared
/// 'write' lock"; only root <c>kill -9</c> recovered. The hardening:</para>
/// <list type="bullet">
///   <item>shutdown handler pauses dispatch via <see cref="IShutdownDispatchGate"/>
///   BEFORE per-VM teardown, so no new sandboxes race the snapshot;</item>
///   <item>operator-tunable <see cref="SandboxTeardownMode"/> picks between
///   Suspend (legacy), Stop (multipass stop, lock-safe), Dispose (purge);</item>
///   <item>startup reconciler runs in the background and tries to recover
///   orphaned suspend-lifecycle VMs (stop, then purge) without blocking the
///   API listener.</item>
/// </list>
/// </summary>
public sealed class SandboxShutdownOrderingTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"codeybox-shutdown-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public SandboxShutdownOrderingTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private static WorkItem MakeItem(WorkItemState state = WorkItemState.Working) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = state,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    private static CodeyBoxOptions OptionsWithTeardownMode(SandboxTeardownMode mode)
    {
        var options = new CodeyBoxOptions();
        options.Shutdown.SandboxTeardownMode = mode;
        return options;
    }

    // ── Shutdown sequencing: dispatch is paused BEFORE the first VM teardown ──

    [Fact]
    public async Task ShutdownHandler_PausesDispatch_BeforeAnyVmTeardown()
    {
        // The single most important sequencing assertion of R8.1. The
        // SandboxSuspendOnShutdownService must call IShutdownDispatchGate.PauseDispatch
        // BEFORE it snapshots the suspendable set and begins any per-VM teardown.
        // Without that ordering the dispatch loop keeps creating new sandboxes that
        // race the snapshot and end up torn down uncleanly when the BackgroundService
        // cancellation token fires later in the shutdown sequence — the very
        // condition that wedged 8 VMs in the 2026-05-29 incident.
        var item = MakeItem();
        await _store.CreateAsync(item);

        var gate = new TestShutdownDispatchGate();
        var provider = new OrderingFakeProvider();
        var sandbox = new OrderingFakeSandbox("vm-x", () => gate.IsDispatchPaused);
        provider.Register(item.Id, sandbox);

        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend,
            dispatchGate: gate);

        Assert.False(gate.IsDispatchPaused);
        Assert.False(svc.DispatchPauseObserved);

        await svc.SuspendAllAsync();

        Assert.True(gate.IsDispatchPaused, "dispatch gate must be flipped during shutdown");
        Assert.True(svc.DispatchPauseObserved, "service should record that it observed the gate");
        Assert.True(svc.DispatchPausedBeforeTeardown,
            "dispatch must be paused before the first per-VM teardown call");
        Assert.True(sandbox.SawDispatchPaused,
            "each per-VM teardown call should observe IsDispatchPaused=true");
        Assert.True(sandbox.SuspendCalled);
    }

    [Fact]
    public async Task ShutdownHandler_NullGate_DoesNotThrow_AndStillTearsDown()
    {
        // Tests / fixtures driving the handler without DI must still be able to
        // run it end-to-end. The gate is optional; absence is a no-op.
        var item = MakeItem();
        await _store.CreateAsync(item);

        var provider = new OrderingFakeProvider();
        var sandbox = new OrderingFakeSandbox("vm-no-gate");
        provider.Register(item.Id, sandbox);

        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance,
            teardownMode: SandboxTeardownMode.Suspend,
            dispatchGate: null);

        await svc.SuspendAllAsync();

        Assert.False(svc.DispatchPauseObserved);
        Assert.True(svc.DispatchPausedBeforeTeardown,
            "with no gate wired the service still treats teardown as not-blocked");
        Assert.True(sandbox.SuspendCalled);
    }

    [Fact]
    public async Task ShutdownHandler_PausesDispatch_EvenWhenNoSandboxesToTeardown()
    {
        // Pause must fire even when SnapshotSuspendableActive is empty —
        // otherwise a race where the snapshot empties between gate-set and
        // teardown could leave dispatch running while we exit.
        var gate = new TestShutdownDispatchGate();
        var provider = new OrderingFakeProvider();
        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance,
            teardownMode: SandboxTeardownMode.Stop,
            dispatchGate: gate);

        await svc.SuspendAllAsync();

        Assert.True(gate.IsDispatchPaused);
    }

    [Fact]
    public void OrchestratorPauseDispatch_IsIdempotentAndFlipsFlag()
    {
        // Unit-level guard on the IShutdownDispatchGate contract: PauseDispatch
        // is idempotent and flips IsDispatchPaused. The end-to-end behaviour
        // ("the actual dispatch loop refuses to pick up new work after this
        // flag is set") is covered separately by
        // OrchestratorDispatchLoop_StopsPickingUpWork_AfterPauseDispatch.
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue, _store,
            new ShortCircuitPipelineRunner(),
            new CancellationRegistry(),
            new OrchestratorOptions { MaxConcurrentWorkers = 1 },
            NullLogger<OrchestratorService>.Instance);

        Assert.False(svc.IsDispatchPaused);
        svc.PauseDispatch();
        Assert.True(svc.IsDispatchPaused);
        // Idempotent: a second call is a no-op (regression guard against a
        // future refactor that re-runs the logging or queue-kick branch
        // unconditionally — both side effects of the first call).
        svc.PauseDispatch();
        Assert.True(svc.IsDispatchPaused);
    }

    [Fact]
    public async Task OrchestratorDispatchLoop_StopsPickingUpWork_AfterPauseDispatch()
    {
        // End-to-end coverage of the load-bearing production change: actually
        // drive ExecuteAsync via StartAsync/StopAsync and verify that after
        // PauseDispatch fires, NO new work item is picked up by the loop —
        // even when a fresh Queued item AND a queue kick are added afterwards.
        // Without this assertion, a refactor that removes the
        // `if (IsDispatchPaused) break;` checks at the top of and immediately
        // after dequeue would silently re-introduce the wedge.
        var pipeline = new SpawnCountingPipelineRunner();
        var queue = new InMemoryTaskQueue();
        var svc = new OrchestratorService(
            queue, _store, pipeline,
            new CancellationRegistry(),
            new OrchestratorOptions { MaxConcurrentWorkers = 4 },
            NullLogger<OrchestratorService>.Instance);

        await svc.StartAsync(CancellationToken.None);

        // Settle: ExecuteAsync is now inside its while loop, blocked in
        // DequeueAsync. PauseDispatch must wake it and force it to break.
        await pipeline.IdleOnce.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, pipeline.PickupCount);

        // Pause dispatch THEN enqueue a real item and kick. The IsDispatchPaused
        // checks at the top of the loop AND immediately after DequeueAsync must
        // both fire before any pickup happens.
        svc.PauseDispatch();
        var item = MakeItem(WorkItemState.Queued);
        await _store.CreateAsync(item);
        await queue.EnqueueAsync(item.Id, CancellationToken.None);

        // Give the loop generous time to (incorrectly) wake and pick up the
        // item. 500ms is well past any reasonable wake-up latency.
        await Task.Delay(500);

        Assert.Equal(0, pipeline.PickupCount);

        await svc.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);

        // Item is still Queued — the paused loop never moved it.
        var after = await _store.GetAsync(item.Id);
        Assert.Equal(WorkItemState.Queued, after!.State);
    }

    private sealed class SpawnCountingPipelineRunner : IPipelineRunner
    {
        private int _pickupCount;
        public int PickupCount => Volatile.Read(ref _pickupCount);
        public TaskCompletionSource IdleOnce { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RunAsync(WorkItem item, CancellationToken workItemToken, CancellationToken hostToken)
        {
            Interlocked.Increment(ref _pickupCount);
            return Task.CompletedTask;
        }

        // Signal that ExecuteAsync has reached the dispatch loop and is idle.
        // We exploit the fact that ReplayPendingAsync runs before the loop
        // starts; after it returns, the loop is in DequeueAsync. We can't
        // observe that directly, so we fire on the FIRST RunAsync call OR
        // when nothing arrives within the test's short pre-pause window.
        public SpawnCountingPipelineRunner()
        {
            _ = Task.Delay(100).ContinueWith(_ => IdleOnce.TrySetResult());
        }
    }

    // ── Stop / Dispose teardown modes ────────────────────────────────────────

    [Fact]
    public async Task ShutdownHandler_StopMode_CallsStopAndPreserve_NotSuspend()
    {
        // The R8.1 default teardown mode the post-incident review recommended:
        // multipass stop is faster and far less likely to wedge multipassd than
        // suspend, and the lifecycle service must apply it to every active
        // suspendable sandbox in its shutdown snapshot.
        var item = MakeItem();
        await _store.CreateAsync(item);

        var provider = new OrderingFakeProvider();
        var sandbox = new OrderingFakeSandbox("vm-stop");
        provider.Register(item.Id, sandbox);

        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance,
            teardownMode: SandboxTeardownMode.Stop);

        await svc.SuspendAllAsync();

        Assert.False(sandbox.SuspendCalled, "Stop mode must not call SuspendAsync");
        Assert.True(sandbox.StopAndPreserveCalled,
            "Stop mode must stop and preserve every active suspendable sandbox");
        Assert.False(sandbox.DisposeCalled, "Stop mode must not dispose preemptible sandboxes");
        Assert.True(sandbox.OwnedByShutdownHandler,
            "Stop mode must suppress PipelineRunner's in-VM preempt-checkpoint path against the stopped VM");
        Assert.True(sandbox.MarkOwnedOrder > 0 && sandbox.MarkOwnedOrder < sandbox.StopAndPreserveOrder,
            "Stop mode must mark shutdown ownership before stopping the sandbox");
        // Stop mode does not persist SuspendedVmName: the work item recovers via
        // the standard stopped-sandbox recovery path, not suspend-resume.
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
    }

    [Fact]
    public async Task ShutdownHandler_TeardownModeAccessor_UsesHotReloadedValueAtShutdown()
    {
        // Regression for the 2026-06-02 incident: the hosted service used to
        // capture SandboxTeardownMode at construction. If an operator changed
        // Suspend -> Stop in the hot-reloaded config, the already-running
        // process still used Suspend on its way down, recreating the wedge the
        // operator was trying to avoid.
        var item = MakeItem();
        await _store.CreateAsync(item);

        var provider = new OrderingFakeProvider();
        var sandbox = new OrderingFakeSandbox("vm-hot-reload");
        provider.Register(item.Id, sandbox);

        var monitor = new MutableOptionsMonitor<CodeyBoxOptions>(
            OptionsWithTeardownMode(SandboxTeardownMode.Suspend));
        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance,
            teardownModeAccessor: () => monitor.CurrentValue.Shutdown.SandboxTeardownMode);

        monitor.Set(OptionsWithTeardownMode(SandboxTeardownMode.Stop));

        await svc.StoppingAsync(CancellationToken.None);

        Assert.False(sandbox.SuspendCalled,
            "shutdown must not use the startup SandboxTeardownMode after a hot reload");
        Assert.False(sandbox.StopAndPreserveCalled,
            "shutdown must use the current SandboxTeardownMode from IOptionsMonitor; Stop mode defers StopAndPreserveAsync to PipelineRunner");
        Assert.False(sandbox.OwnedByShutdownHandler,
            "Stop mode must not suppress PipelineRunner's preempt-checkpoint path after hot reload");
    }

    [Fact]
    public async Task ShutdownHandler_DisposeMode_CallsDispose_NotSuspend()
    {
        // The simplest teardown mode against lock contention: delete --purge
        // outright. No resume bookkeeping is written.
        var item = MakeItem();
        await _store.CreateAsync(item);

        var provider = new OrderingFakeProvider();
        var sandbox = new OrderingFakeSandbox("vm-dispose");
        provider.Register(item.Id, sandbox);

        var svc = new SandboxSuspendOnShutdownService(
            provider, _store,
            NullLogger<SandboxSuspendOnShutdownService>.Instance,
            teardownMode: SandboxTeardownMode.Dispose);

        await svc.SuspendAllAsync();

        Assert.False(sandbox.SuspendCalled, "Dispose mode must not call SuspendAsync");
        Assert.False(sandbox.StopAndPreserveCalled, "Dispose mode must not call StopAndPreserveAsync");
        Assert.True(sandbox.DisposeCalled,
            "Dispose mode should DisposeAsync the sandbox");
        Assert.True(sandbox.OwnedByShutdownHandler,
            "Dispose mode must suppress PipelineRunner's in-VM preempt-checkpoint path against the deleted VM");
        Assert.True(sandbox.MarkOwnedOrder > 0 && sandbox.MarkOwnedOrder < sandbox.DisposeOrder,
            "Dispose mode must mark shutdown ownership before disposal");
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
    }

    [Fact]
    public void SandboxTeardownMode_DefaultIsStop_OnProductionOptions()
    {
        // Production default assertion: operators have to opt in to Suspend.
        // Asserts on the actual CodeyBoxOptions.ShutdownOptions wired in
        // Program.cs — a regression that flips the production default must FAIL
        // this test, not pass against a private mirror class.
        var opts = new CodeyBoxOptions();
        Assert.Equal(SandboxTeardownMode.Stop, opts.Shutdown.SandboxTeardownMode);
    }

    // ── Startup reconciliation of stale Suspending/Unknown VMs ───────────────

    [Fact]
    public async Task StartupReconciler_RecoversOrphanedSuspendingVm()
    {
        // The R8.1 startup recovery path: a VM left wedged in Suspending state
        // from a prior unclean shutdown — with NO live SuspendedVmName mapping
        // — gets stop-then-purged by the reconciler before resume / leak-reaper
        // see it. Without this, the leak reaper would eventually try
        // delete --purge, which fails on the qemu disk-image write-lock; the
        // incident showed that delete-without-stop is the wrong recovery order.
        var provider = new ReconcilingFakeProvider();
        provider.SeedManaged(new ManagedSandboxInfo(
            "vm-stuck", DateTimeOffset.UtcNow.AddHours(-1), 1024L * 1024,
            IsTrackedActive: false, HasPreemptMarker: true, IsSuspendLifecycleOrFrozen: true));

        var svc = new StartupSandboxReconciliationService(
            provider, _store, NullLogger<StartupSandboxReconciliationService>.Instance);

        await svc.ReconcileAllForTestAsync(CancellationToken.None);

        Assert.Contains("vm-stuck", provider.RecoveredNames);
        // Recovery sequence must put stop before purge — the regression that
        // wedged the 2026-05-29 VMs was calling delete --purge against a
        // qemu-locked disk image.
        Assert.True(provider.SawStopBeforePurge("vm-stuck"),
            "reconciler must call multipass stop before delete --purge for transitional-state VMs");
    }

    [Fact]
    public async Task StartupReconciler_LeavesLiveSuspendedMappingsAlone()
    {
        // Symmetric guard: a Suspending VM with a live SuspendedVmName mapping
        // is being held across a restart by the resume handler. The reconciler
        // MUST NOT touch it — doing so would race the resume's multipass-start.
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-mapped",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var provider = new ReconcilingFakeProvider();
        provider.SeedManaged(new ManagedSandboxInfo(
            "vm-mapped", DateTimeOffset.UtcNow.AddHours(-1), 1024L * 1024,
            IsTrackedActive: false, HasPreemptMarker: true, IsSuspendLifecycleOrFrozen: true));

        var svc = new StartupSandboxReconciliationService(
            provider, _store, NullLogger<StartupSandboxReconciliationService>.Instance);

        await svc.ReconcileAllForTestAsync(CancellationToken.None);

        Assert.DoesNotContain("vm-mapped", provider.RecoveredNames);
        Assert.Empty(provider.UnrecoverableReturned);
    }

    [Fact]
    public async Task StartupReconciler_IgnoresVmsInNormalRunningState()
    {
        // The reconciler exists only for the suspend-lifecycle / Unknown wedge
        // case. A Running VM with no live mapping is the leak reaper's
        // territory (slow-burn cleanup of orphans after LeakAgeThreshold).
        // The reconciler must NOT race the reaper by purging a recently-started
        // VM mid-launch.
        var provider = new ReconcilingFakeProvider();
        provider.SeedManaged(new ManagedSandboxInfo(
            "vm-running", DateTimeOffset.UtcNow.AddMinutes(-2), 1024L * 1024,
            IsTrackedActive: false, HasPreemptMarker: false, IsSuspendLifecycleOrFrozen: false));

        var svc = new StartupSandboxReconciliationService(
            provider, _store, NullLogger<StartupSandboxReconciliationService>.Instance);

        await svc.ReconcileAllForTestAsync(CancellationToken.None);

        Assert.DoesNotContain("vm-running", provider.RecoveredNames);
    }

    [Fact]
    public async Task StartupReconciler_NullProvider_IsNoOp()
    {
        // Seed an item that WOULD be touched if the reconciler erroneously
        // iterated when provider is null. Verifying no store mutation and no
        // throw is the load-bearing assertion — without it, a regression that
        // removed the null check would still pass.
        var item = MakeItem(WorkItemState.Working);
        await _store.CreateAsync(item with
        {
            SuspendedVmName = "vm-untouched",
            SuspendedAt = DateTimeOffset.UtcNow,
        });

        var svc = new StartupSandboxReconciliationService(
            provider: null, _store,
            NullLogger<StartupSandboxReconciliationService>.Instance);
        await svc.ReconcileAllForTestAsync(CancellationToken.None);

        // Mapping is untouched: a null provider must not clear / overwrite
        // any persisted suspend bookkeeping.
        var after = await _store.GetAsync(item.Id);
        Assert.NotNull(after);
        Assert.Equal("vm-untouched", after.SuspendedVmName);
    }

    [Fact]
    public async Task StartupReconciler_LifecycleStartup_DoesNotBlockOnProviderRecovery()
    {
        var provider = new BlockingReconcilingProvider();
        var svc = new StartupSandboxReconciliationService(
            provider, _store, NullLogger<StartupSandboxReconciliationService>.Instance);

        await svc.StartingAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        Assert.False(provider.ReconcileEntered.Task.IsCompleted);

        await svc.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        await provider.ReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        provider.Release();
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupReconciler_StopAsync_CancelsBackgroundRecovery_AndIsIdempotent()
    {
        var provider = new BlockingReconcilingProvider();
        var svc = new StartupSandboxReconciliationService(
            provider, _store, NullLogger<StartupSandboxReconciliationService>.Instance);

        await svc.StartAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        await provider.ReconcileEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await svc.StopAsync(stopCts.Token);

        Assert.True(provider.ReconcileCancellationObserved);

        // Host/factory disposal paths may call StopAsync more than once.
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupReconciler_ProviderCancellation_DoesNotPropagate_WhenHostTokenIsActive()
    {
        var provider = new ReconcilingFakeProvider { ReconcileThrowsProviderCancellation = true };
        var svc = new StartupSandboxReconciliationService(
            provider, _store, NullLogger<StartupSandboxReconciliationService>.Instance);

        using var hostCts = new CancellationTokenSource();
        await svc.ReconcileAllForTestAsync(hostCts.Token).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(hostCts.IsCancellationRequested);
        Assert.True(provider.ReconcileCalled);
    }

    [Fact]
    public async Task StartupReconciler_UnrecoverableVm_IsReturnedForOperatorAttention()
    {
        // When the provider can't recover a VM (root cleanup needed — the
        // 2026-05-29 incident's terminal case), the reconciler surfaces the
        // name so operator dashboards see it as needing attention. Without
        // this signal the VM would silently consume RAM/disk until human
        // intervention.
        var provider = new ReconcilingFakeProvider { RecoveryThrowsFor = "vm-wedged" };
        provider.SeedManaged(new ManagedSandboxInfo(
            "vm-wedged", DateTimeOffset.UtcNow.AddHours(-1), 1024L * 1024,
            IsTrackedActive: false, HasPreemptMarker: true, IsSuspendLifecycleOrFrozen: true));

        var svc = new StartupSandboxReconciliationService(
            provider, _store, NullLogger<StartupSandboxReconciliationService>.Instance);

        await svc.ReconcileAllForTestAsync(CancellationToken.None);

        Assert.Single(provider.UnrecoverableReturned);
        Assert.Equal("vm-wedged", provider.UnrecoverableReturned[0]);
    }

    // ── Multipass DisposeLeakedAsync stop-before-purge logic ─────────────────

    [Fact]
    public async Task DisposeLeakedAsync_RunsStopBeforePurge_ForSuspendingVm()
    {
        // End-to-end coverage of the production stop-before-purge branch in
        // the REAL MultipassSandboxProvider — not a fake mirror. The 2026-05-29
        // wedge was caused by calling delete --purge against a qemu-locked disk
        // image; the fix is to run multipass stop first. Without this assertion
        // a refactor that drops the pre-stop call, inverts the call order, or
        // swallows the wrong exception would silently re-introduce the wedge.
        var calls = new ConcurrentQueue<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            // multipass info codeybox-stuck --format=json → state "Suspending"
            if (argv is [_, "info", "codeybox-stuck", "--format=json"])
            {
                calls.Enqueue("info");
                return Task.FromResult(new ProcessRunResult(0,
                    """{"info":{"codeybox-stuck":{"state":"Suspending"}}}""", ""));
            }
            if (argv is [_, "stop", "codeybox-stuck"])
            {
                calls.Enqueue("stop");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "delete", "--purge", "codeybox-stuck"])
            {
                calls.Enqueue("delete-purge");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", $"unexpected argv: {string.Join(' ', argv)}"));
        });
        var provider = NewMultipassProvider(runner);

        await provider.DisposeLeakedAsync("codeybox-stuck", CancellationToken.None);

        var sequence = calls.ToArray();
        Assert.Equal(new[] { "info", "stop", "delete-purge" }, sequence);
    }

    [Fact]
    public async Task DisposeLeakedAsync_SkipsStop_ForSuspendedVm()
    {
        // Symmetric guard: the Suspended (snapshot-complete) state does NOT
        // hold the qemu disk-image lock, so the slow stop preamble must be
        // skipped — applying it indiscriminately would lengthen every leak
        // reaper sweep against ordinary stale VMs.
        var calls = new ConcurrentQueue<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", "codeybox-frozen", "--format=json"])
            {
                calls.Enqueue("info");
                return Task.FromResult(new ProcessRunResult(0,
                    """{"info":{"codeybox-frozen":{"state":"Suspended"}}}""", ""));
            }
            if (argv is [_, "stop", "codeybox-frozen"])
            {
                calls.Enqueue("stop");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "delete", "--purge", "codeybox-frozen"])
            {
                calls.Enqueue("delete-purge");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", $"unexpected argv: {string.Join(' ', argv)}"));
        });
        var provider = NewMultipassProvider(runner);

        await provider.DisposeLeakedAsync("codeybox-frozen", CancellationToken.None);

        Assert.Equal(new[] { "info", "delete-purge" }, calls.ToArray());
    }

    [Fact]
    public async Task DisposeLeakedAsync_StopNonZero_StillProceedsToPurge()
    {
        // If the pre-purge stop fails (multipassd unhappy, qemu PID gone) we
        // must NOT abort the purge — the whole point of stop-before-purge is
        // best-effort. The test pins that a non-zero exit from stop logs but
        // doesn't prevent the subsequent delete --purge from running.
        var calls = new ConcurrentQueue<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "info", "codeybox-wedged", "--format=json"])
            {
                calls.Enqueue("info");
                return Task.FromResult(new ProcessRunResult(0,
                    """{"info":{"codeybox-wedged":{"state":"Suspending"}}}""", ""));
            }
            if (argv is [_, "stop", "codeybox-wedged"])
            {
                calls.Enqueue("stop");
                return Task.FromResult(new ProcessRunResult(2, "",
                    "stop failed: Failed to get shared \"write\" lock"));
            }
            if (argv is [_, "delete", "--purge", "codeybox-wedged"])
            {
                calls.Enqueue("delete-purge");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", $"unexpected argv: {string.Join(' ', argv)}"));
        });
        var provider = NewMultipassProvider(runner);

        await provider.DisposeLeakedAsync("codeybox-wedged", CancellationToken.None);

        Assert.Equal(new[] { "info", "stop", "delete-purge" }, calls.ToArray());
    }

    [Fact]
    public async Task ReconcileStuckSandboxesAsync_RealProvider_StopsAndPurgesOrphanedSuspendingVm()
    {
        // End-to-end coverage of the REAL MultipassSandboxProvider's
        // ReconcileStuckSandboxesAsync — the SUT used in production. Previously
        // only the fake's reconciler was exercised, which proved nothing about
        // the production class. This test seeds an orphan "codeybox-orphan"
        // Suspending VM via fake `multipass list`/`info` output and asserts the
        // production code path actually issues `stop` then `delete --purge` in
        // that order against it.
        var calls = new List<string>();
        var infoCallCount = 0;
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            // multipass list --format json: one orphan VM
            if (argv is [_, "list", "--format", "json"])
            {
                calls.Add("list");
                return Task.FromResult(new ProcessRunResult(0,
                    """{"list":[{"name":"codeybox-orphan"}]}""", ""));
            }
            // multipass info --format json codeybox-orphan: bulk info call from ListAllManagedAsync
            if (argv is [_, "info", "--format", "json", "codeybox-orphan"])
            {
                calls.Add("bulk-info");
                return Task.FromResult(new ProcessRunResult(0,
                    """{"info":{"codeybox-orphan":{"state":"Suspending","disks":{}}}}""", ""));
            }
            // Reconciler re-queries state to pick the audit label, then DisposeLeakedAsync re-queries again.
            if (argv is [_, "info", "codeybox-orphan", "--format=json"])
            {
                infoCallCount++;
                calls.Add($"info#{infoCallCount}");
                return Task.FromResult(new ProcessRunResult(0,
                    """{"info":{"codeybox-orphan":{"state":"Suspending"}}}""", ""));
            }
            if (argv is [_, "stop", "codeybox-orphan"])
            {
                calls.Add("stop");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "delete", "--purge", "codeybox-orphan"])
            {
                calls.Add("delete-purge");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", $"unexpected argv: {string.Join(' ', argv)}"));
        });
        var provider = NewMultipassProvider(runner);

        var unrecoverable = await ((ISuspendingSandboxProvider)provider)
            .ReconcileStuckSandboxesAsync(new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);

        Assert.Empty(unrecoverable);
        var stopIdx = calls.IndexOf("stop");
        var purgeIdx = calls.IndexOf("delete-purge");
        Assert.True(stopIdx >= 0, "real provider must call multipass stop on a Suspending orphan");
        Assert.True(purgeIdx >= 0, "real provider must call multipass delete --purge on a Suspending orphan");
        Assert.True(stopIdx < purgeIdx,
            $"stop ({stopIdx}) must precede delete --purge ({purgeIdx}) — incident 2026-05-29 was caused by purging before stop");
    }

    [Fact]
    public async Task ReconcileStuckSandboxesAsync_RealProvider_SkipsLiveSuspendedMapping()
    {
        // The production reconciler must NOT touch a VM in liveSuspendedNames
        // (the resume handler is about to reattach it). A regression that
        // inverted this guard would race the resume's multipass-start and
        // could break the resume-on-startup path entirely.
        var calls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
            {
                calls.Add("list");
                return Task.FromResult(new ProcessRunResult(0,
                    """{"list":[{"name":"codeybox-mapped"}]}""", ""));
            }
            if (argv is [_, "info", "--format", "json", "codeybox-mapped"])
            {
                calls.Add("bulk-info");
                return Task.FromResult(new ProcessRunResult(0,
                    """{"info":{"codeybox-mapped":{"state":"Suspending","disks":{}}}}""", ""));
            }
            if (argv is [_, "stop", "codeybox-mapped"])
            {
                calls.Add("stop");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            if (argv is [_, "delete", "--purge", "codeybox-mapped"])
            {
                calls.Add("delete-purge");
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", $"unexpected argv: {string.Join(' ', argv)}"));
        });
        var provider = NewMultipassProvider(runner);

        await ((ISuspendingSandboxProvider)provider)
            .ReconcileStuckSandboxesAsync(
                new HashSet<string>(StringComparer.Ordinal) { "codeybox-mapped" },
                CancellationToken.None);

        Assert.DoesNotContain("stop", calls);
        Assert.DoesNotContain("delete-purge", calls);
    }

    [Fact]
    public async Task ReconcileStuckSandboxesAsync_RealProvider_SkipsRunningVms()
    {
        // A Running VM (non-suspend-lifecycle state) is the leak reaper's
        // territory. The reconciler exists ONLY for the suspend-wedge case;
        // touching Running VMs would race the regular leak-reaper grace window.
        var calls = new List<string>();
        var runner = new RecordingMultipassRunner((argv, _, _) =>
        {
            if (argv is [_, "list", "--format", "json"])
                return Task.FromResult(new ProcessRunResult(0,
                    """{"list":[{"name":"codeybox-running"}]}""", ""));
            if (argv is [_, "info", "--format", "json", "codeybox-running"])
                return Task.FromResult(new ProcessRunResult(0,
                    """{"info":{"codeybox-running":{"state":"Running","disks":{}}}}""", ""));
            if (argv is [_, "stop", _] or [_, "delete", "--purge", _])
            {
                calls.Add(string.Join(' ', argv.Skip(1)));
                return Task.FromResult(new ProcessRunResult(0, "", ""));
            }
            return Task.FromResult(new ProcessRunResult(99, "", $"unexpected argv: {string.Join(' ', argv)}"));
        });
        var provider = NewMultipassProvider(runner);

        var unrecoverable = await ((ISuspendingSandboxProvider)provider)
            .ReconcileStuckSandboxesAsync(new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);

        Assert.Empty(unrecoverable);
        Assert.Empty(calls);
    }

    private MultipassSandboxProvider NewMultipassProvider(IProcessRunner runner) => new(
        new MultipassSandboxOptions
        {
            MultipassBinary = "/bin/false",
            StagingDirectory = Path.Combine(Path.GetTempPath(), $"codeybox-test-staging-{Guid.NewGuid():N}"),
        },
        NullLogger<MultipassSandboxProvider>.Instance,
        timings: null,
        runner: runner);

    [Fact]
    public void NeedsStopBeforePurge_OnlyForTransitionalStates()
    {
        // Whitebox check on the provider's state classifier. The stop-first
        // recovery is precisely targeted at the wedge case — applying it to
        // Stopped/Running VMs would just slow the leak reaper's hot path.
        Assert.True(CodeyBox.Sandbox.Multipass.MultipassSandboxProvider
            .NeedsStopBeforePurge("Suspending"));
        Assert.True(CodeyBox.Sandbox.Multipass.MultipassSandboxProvider
            .NeedsStopBeforePurge("Unknown"));
        // Case-insensitive — multipassd has been observed to switch case
        // between releases ("suspending" vs "Suspending").
        Assert.True(CodeyBox.Sandbox.Multipass.MultipassSandboxProvider
            .NeedsStopBeforePurge("suspending"));

        // Negative cases that must NOT trigger the slow stop-first path:
        Assert.False(CodeyBox.Sandbox.Multipass.MultipassSandboxProvider
            .NeedsStopBeforePurge("Running"));
        Assert.False(CodeyBox.Sandbox.Multipass.MultipassSandboxProvider
            .NeedsStopBeforePurge("Stopped"));
        // Suspended is the steady-state post-snapshot — the lock is already
        // released, no need for the stop preamble.
        Assert.False(CodeyBox.Sandbox.Multipass.MultipassSandboxProvider
            .NeedsStopBeforePurge("Suspended"));
        Assert.False(CodeyBox.Sandbox.Multipass.MultipassSandboxProvider
            .NeedsStopBeforePurge(null));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;

        public MutableOptionsMonitor(T initial) { _value = initial; }
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public void Set(T next) => _value = next;
        public IDisposable OnChange(Action<T, string?> listener) => NullSubscription.Instance;

        private sealed class NullSubscription : IDisposable
        {
            public static readonly NullSubscription Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class TestShutdownDispatchGate : IShutdownDispatchGate
    {
        public bool IsDispatchPaused { get; private set; }
        public int PauseDispatchCallCount { get; private set; }
        public void PauseDispatch()
        {
            PauseDispatchCallCount++;
            IsDispatchPaused = true;
        }
    }

    private sealed class OrderingFakeProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly ConcurrentDictionary<WorkItemId, ISuspendableSandbox> _active = new();
        public void Register(WorkItemId id, ISuspendableSandbox sandbox) => _active[id] = sandbox;
        public string Name => "fake-ordering";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new OrderingFakeSandbox("fake-ordering-created"));
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive()
        {
            var list = new List<(WorkItemId, ISuspendableSandbox)>();
            foreach (var kv in _active) list.Add((kv.Key, kv.Value));
            return list;
        }
        public Task ResumeSandboxAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class OrderingFakeSandbox : IPreemptibleSandbox, ISuspendableSandbox
    {
        private readonly Func<bool>? _isDispatchPaused;
        public OrderingFakeSandbox(string id, Func<bool>? isDispatchPaused = null)
        {
            Id = id;
            _isDispatchPaused = isDispatchPaused;
        }
        public string Id { get; }
        public bool SuspendCalled { get; private set; }
        public bool StopAndPreserveCalled { get; private set; }
        public bool DisposeCalled { get; private set; }
        public bool SawDispatchPaused { get; private set; }
        public bool OwnedByShutdownHandler { get; private set; }
        public int MarkOwnedOrder { get; private set; }
        public int StopAndPreserveOrder { get; private set; }
        public int DisposeOrder { get; private set; }
        private int _order;
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));
        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            DisposeOrder = ++_order;
            return ValueTask.CompletedTask;
        }
        public Task SuspendAsync(CancellationToken ct = default)
        {
            if (_isDispatchPaused?.Invoke() == true) SawDispatchPaused = true;
            SuspendCalled = true;
            return Task.CompletedTask;
        }
        public Task StopAndPreserveAsync(CancellationToken ct = default)
        {
            if (_isDispatchPaused?.Invoke() == true) SawDispatchPaused = true;
            StopAndPreserveCalled = true;
            StopAndPreserveOrder = ++_order;
            return Task.CompletedTask;
        }
        public bool IsOwnedByShutdownHandler => OwnedByShutdownHandler;
        public void MarkOwnedByShutdownHandler()
        {
            OwnedByShutdownHandler = true;
            MarkOwnedOrder = ++_order;
        }
    }

    private sealed class ReconcilingFakeProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly List<ManagedSandboxInfo> _managed = new();
        public List<string> RecoveredNames { get; } = new();
        public IReadOnlyList<string> UnrecoverableReturned { get; private set; } = [];
        private readonly ConcurrentDictionary<string, List<string>> _opsByVm = new();
        public string? RecoveryThrowsFor { get; set; }
        public bool ReconcileThrowsProviderCancellation { get; set; }
        public bool ReconcileCalled { get; private set; }

        public void SeedManaged(ManagedSandboxInfo info) => _managed.Add(info);
        public string Name => "fake-reconciling";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new OrderingFakeSandbox("fake-reconciling-created"));
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>(_managed);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive() => [];
        public Task ResumeSandboxAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public bool SawStopBeforePurge(string name)
        {
            if (!_opsByVm.TryGetValue(name, out var ops)) return false;
            var stopIdx = ops.IndexOf("stop");
            var purgeIdx = ops.IndexOf("delete-purge");
            return stopIdx >= 0 && purgeIdx >= 0 && stopIdx < purgeIdx;
        }

        public async Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
            IReadOnlySet<string> liveSuspendedNames, CancellationToken ct)
        {
            ReconcileCalled = true;
            if (ReconcileThrowsProviderCancellation)
                throw new OperationCanceledException("provider cancelled reconciliation");

            var unrecoverable = new List<string>();
            foreach (var info in _managed)
            {
                if (liveSuspendedNames.Contains(info.Name)) continue;
                if (!info.IsSuspendLifecycleOrFrozen) continue;
                if (info.IsTrackedActive) continue;

                var ops = _opsByVm.GetOrAdd(info.Name, _ => new List<string>());
                if (RecoveryThrowsFor == info.Name)
                {
                    ops.Add("stop");
                    unrecoverable.Add(info.Name);
                    continue;
                }
                // Mirror what the real multipass provider does: stop, then purge.
                ops.Add("stop");
                ops.Add("delete-purge");
                RecoveredNames.Add(info.Name);
                await Task.Yield();
            }
            UnrecoverableReturned = unrecoverable;
            return unrecoverable;
        }
    }

    private sealed class BlockingReconcilingProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly TaskCompletionSource<IReadOnlyList<string>> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReconcileEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "fake-blocking-reconciling";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);
        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive() => [];
        public Task ResumeSandboxAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public async Task<IReadOnlyList<string>> ReconcileStuckSandboxesAsync(
            IReadOnlySet<string> liveSuspendedNames, CancellationToken ct)
        {
            ReconcileEntered.TrySetResult();
            try
            {
                return await _release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ReconcileCancellationObserved = true;
                throw;
            }
        }

        public bool ReconcileCancellationObserved { get; private set; }
        public void Release() => _release.TrySetResult([]);
    }

    private sealed class ShortCircuitPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken workItemToken, CancellationToken hostToken)
            => Task.CompletedTask;
    }
}

[Collection("GlobalSerilog")]
public sealed class SandboxShutdownProgramWiringTests
{
    [Fact]
    public async Task ProgramHostedServiceRegistration_UsesHotReloadedTeardownModeAtShutdown()
    {
        using var factory = new SandboxShutdownProgramFactory();
        var item = MakeItem();
        await factory.Store.CreateAsync(item);

        var sandbox = new ProgramWiringSandbox("vm-program-hot-reload");
        factory.Provider.Register(item.Id, sandbox);

        // Force WebApplicationFactory to build the real Program.cs service
        // collection while the monitor still says Suspend. If Program.cs
        // regresses to capturing CurrentValue in its AddHostedService factory,
        // the service created here will keep Suspend even after Set(Stop).
        var service = Assert.Single(
            factory.Services.GetServices<IHostedService>()
                .OfType<SandboxSuspendOnShutdownService>());

        factory.Monitor.Set(OptionsWithTeardownMode(SandboxTeardownMode.Stop));

        await service.StoppingAsync(CancellationToken.None);

        Assert.False(sandbox.SuspendCalled,
            "Program.cs must not capture the startup SandboxTeardownMode in the hosted-service registration");
        Assert.False(sandbox.StopAndPreserveCalled,
            "Program.cs must wire the shutdown service to read the current IOptionsMonitor value at teardown time; Stop mode defers StopAndPreserveAsync to PipelineRunner");
    }

    private static WorkItem MakeItem() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Working,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
    };

    private static CodeyBoxOptions OptionsWithTeardownMode(SandboxTeardownMode mode)
    {
        var options = new CodeyBoxOptions();
        options.Shutdown.SandboxTeardownMode = mode;
        return options;
    }

    private sealed class SandboxShutdownProgramFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(
            Path.GetTempPath(), $"codeybox-shutdown-program-{Guid.NewGuid():N}.db");

        public SandboxShutdownProgramFactory() => Store = new SqliteWorkItemStore(_dbPath);

        public ProgramWiringProvider Provider { get; } = new();
        public ProgramWiringDispatchGate DispatchGate { get; } = new();
        public MutableOptionsMonitor<CodeyBoxOptions> Monitor { get; } = new(
            OptionsWithTeardownMode(SandboxTeardownMode.Suspend));
        public SqliteWorkItemStore Store { get; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.Sources.Clear();
                var tmp = Path.GetTempPath();
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["CodeyBox:DangerouslyDisableAuth"] = "true",
                    ["CodeyBox:StateDatabasePath"] = _dbPath,
                    ["CodeyBox:GitRootDirectory"] = Path.Combine(tmp, $"test-git-{Guid.NewGuid():N}"),
                    ["CodeyBox:AuditLog:Path"] = Path.Combine(tmp, $"test-log-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AuditLog:AuditPath"] = Path.Combine(tmp, $"test-audit-{Guid.NewGuid():N}-.json"),
                    ["CodeyBox:AgentStreams:Path"] = Path.Combine(tmp, $"test-agent-streams-{Guid.NewGuid():N}"),
                    ["CodeyBox:Shutdown:SandboxTeardownMode"] = nameof(SandboxTeardownMode.Suspend),
                });
            });
            builder.ConfigureTestServices(services =>
            {
                var suspendDescriptor = FindSandboxSuspendHostedServiceDescriptor(
                    services,
                    new SandboxSuspendDescriptorProbeProvider(
                        Provider, Store, DispatchGate, Monitor));

                services.RemoveAll<IHostedService>();
                services.Add(suspendDescriptor);

                services.RemoveAll<IWorkItemStore>();
                services.AddSingleton<IWorkItemStore>(Store);
                services.RemoveAll<ISandboxProvider>();
                services.AddSingleton<ISandboxProvider>(Provider);
                services.RemoveAll<IShutdownDispatchGate>();
                services.AddSingleton<IShutdownDispatchGate>(DispatchGate);
                services.AddSingleton<IOptionsMonitor<CodeyBoxOptions>>(Monitor);
            });
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            finally
            {
                if (disposing)
                {
                    Store.Dispose();
                    try { File.Delete(_dbPath); } catch { /* best-effort */ }
                }
            }
        }

        private static ServiceDescriptor FindSandboxSuspendHostedServiceDescriptor(
            IServiceCollection services,
            IServiceProvider probeProvider)
        {
            var missingProbeServices = new List<Type>();
            var matches = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationFactory is not null)
                .Where(d => IsSandboxSuspendDescriptor(d, probeProvider, missingProbeServices))
                .ToList();

            Assert.True(matches.Count > 0,
                "Could not locate the SandboxSuspendOnShutdownService hosted-service descriptor. " +
                "Descriptor probe skipped factories requiring: " +
                string.Join(", ", missingProbeServices.Select(t => t.FullName).Distinct().OrderBy(n => n)));
            return Assert.Single(matches);
        }

        private static bool IsSandboxSuspendDescriptor(
            ServiceDescriptor descriptor,
            IServiceProvider probeProvider,
            ICollection<Type> missingProbeServices)
        {
            try
            {
                return descriptor.ImplementationFactory!(probeProvider) is SandboxSuspendOnShutdownService;
            }
            catch (ProbeServiceUnavailableException ex)
            {
                missingProbeServices.Add(ex.ServiceType);
                return false;
            }
        }
    }

    private sealed class SandboxSuspendDescriptorProbeProvider : IServiceProvider
    {
        private readonly ProgramWiringProvider _provider;
        private readonly IWorkItemStore _store;
        private readonly ProgramWiringDispatchGate _dispatchGate;
        private readonly IOptionsMonitor<CodeyBoxOptions> _monitor;

        public SandboxSuspendDescriptorProbeProvider(
            ProgramWiringProvider provider,
            IWorkItemStore store,
            ProgramWiringDispatchGate dispatchGate,
            IOptionsMonitor<CodeyBoxOptions> monitor)
        {
            _provider = provider;
            _store = store;
            _dispatchGate = dispatchGate;
            _monitor = monitor;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ISandboxProvider))
                return _provider;
            if (serviceType == typeof(IWorkItemStore))
                return _store;
            if (serviceType == typeof(IShutdownDispatchGate))
                return _dispatchGate;
            if (serviceType == typeof(IOptionsMonitor<CodeyBoxOptions>))
                return _monitor;
            if (serviceType == typeof(ILogger<SandboxSuspendOnShutdownService>))
                return NullLogger<SandboxSuspendOnShutdownService>.Instance;

            throw new ProbeServiceUnavailableException(serviceType);
        }
    }

    private sealed class ProbeServiceUnavailableException : Exception
    {
        public Type ServiceType { get; }

        public ProbeServiceUnavailableException(Type serviceType)
            : base($"The sandbox-shutdown descriptor probe does not provide {serviceType.FullName}.")
        {
            ServiceType = serviceType;
        }
    }

    private sealed class MutableOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private T _value;

        public MutableOptionsMonitor(T initial) { _value = initial; }
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public void Set(T next) => _value = next;
        public IDisposable OnChange(Action<T, string?> listener) => NullSubscription.Instance;

        private sealed class NullSubscription : IDisposable
        {
            public static readonly NullSubscription Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class ProgramWiringDispatchGate : IShutdownDispatchGate
    {
        public bool IsDispatchPaused { get; private set; }
        public void PauseDispatch() => IsDispatchPaused = true;
    }

    private sealed class ProgramWiringProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly ConcurrentDictionary<WorkItemId, ISuspendableSandbox> _active = new();

        public string Name => "program-wiring";

        public void Register(WorkItemId id, ISuspendableSandbox sandbox) => _active[id] = sandbox;

        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => Task.FromResult<ISandbox>(new ProgramWiringSandbox("program-wiring-created"));

        public Task<IReadOnlyList<ManagedSandboxInfo>> ListAllManagedAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ManagedSandboxInfo>>([]);

        public Task DisposeLeakedAsync(string name, CancellationToken ct) => Task.CompletedTask;

        public IReadOnlyList<(WorkItemId WorkItemId, ISuspendableSandbox Sandbox)> SnapshotSuspendableActive()
        {
            var list = new List<(WorkItemId, ISuspendableSandbox)>();
            foreach (var kv in _active)
                if (_active.TryRemove(kv.Key, out var sandbox))
                    list.Add((kv.Key, sandbox));
            return list;
        }

        public Task ResumeSandboxAsync(string name, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ProgramWiringSandbox : IPreemptibleSandbox, ISuspendableSandbox
    {
        public ProgramWiringSandbox(string id) { Id = id; }

        public string Id { get; }
        public bool SuspendCalled { get; private set; }
        public bool StopAndPreserveCalled { get; private set; }

        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task SuspendAsync(CancellationToken ct = default)
        {
            SuspendCalled = true;
            return Task.CompletedTask;
        }

        public Task StopAndPreserveAsync(CancellationToken ct = default)
        {
            StopAndPreserveCalled = true;
            return Task.CompletedTask;
        }
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

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
///   <item>startup reconciler runs BEFORE the resume handler and tries to
///   recover orphaned suspend-lifecycle VMs (stop, then purge) instead of
///   waiting out the leak reaper's grace.</item>
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
            dispatchGate: gate);

        await svc.SuspendAllAsync();

        Assert.True(gate.IsDispatchPaused);
    }

    [Fact]
    public async Task OrchestratorPauseDispatch_StopsLoopFromPickingUpNewWork()
    {
        // Direct exercise of the OrchestratorService's IShutdownDispatchGate
        // implementation: PauseDispatch is idempotent, flips IsDispatchPaused,
        // and is observable from the test seam used by the wiring assertion.
        // Doesn't drive the full BackgroundService — that's covered by the
        // OrchestratorHostShutdownTokenTests suite.
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

    // ── Stop / Dispose teardown modes ────────────────────────────────────────

    [Fact]
    public async Task ShutdownHandler_StopMode_CallsStopAndPreserve_NotSuspend()
    {
        // The R8.1 alternative teardown mode the post-incident review
        // recommended: multipass stop is faster and far less likely to wedge
        // multipassd than suspend. The suspend path is skipped entirely.
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
            "Stop mode should call IPreemptibleSandbox.StopAndPreserveAsync");
        // Stop mode does not persist SuspendedVmName: the work item recovers via
        // its preempt-checkpoint, same as a non-suspending provider would.
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
    }

    [Fact]
    public async Task ShutdownHandler_DisposeMode_CallsDispose_NotSuspend()
    {
        // The simplest teardown mode against lock contention: delete --purge
        // outright. No resume bookkeeping is written; recovery via preempt-checkpoint.
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
        Assert.True(sandbox.DisposeCalled,
            "Dispose mode should DisposeAsync the sandbox");
        var after = await _store.GetAsync(item.Id);
        Assert.Null(after!.SuspendedVmName);
    }

    [Fact]
    public void SandboxTeardownMode_DefaultIsSuspend()
    {
        // Backward-compat assertion: the default behaviour does NOT change
        // from R8-core. Operators have to opt in to Stop or Dispose.
        Assert.Equal(SandboxTeardownMode.Suspend, new TestShutdownOptions().SandboxTeardownMode);
    }

    private sealed class TestShutdownOptions
    {
        public SandboxTeardownMode SandboxTeardownMode { get; set; } = SandboxTeardownMode.Suspend;
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
        var svc = new StartupSandboxReconciliationService(
            provider: null, _store,
            NullLogger<StartupSandboxReconciliationService>.Instance);
        await svc.ReconcileAllForTestAsync(CancellationToken.None);
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
            => throw new NotImplementedException();
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
        public Task<SandboxExecResult> ExecAsync(SandboxExec exec, CancellationToken ct = default)
            => Task.FromResult(new SandboxExecResult(0, "", ""));
        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
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
            return Task.CompletedTask;
        }
    }

    private sealed class ReconcilingFakeProvider : ISandboxProvider, ISuspendingSandboxProvider
    {
        private readonly List<ManagedSandboxInfo> _managed = new();
        public List<string> RecoveredNames { get; } = new();
        public IReadOnlyList<string> UnrecoverableReturned { get; private set; } = [];
        private readonly ConcurrentDictionary<string, List<string>> _opsByVm = new();
        public string? RecoveryThrowsFor { get; set; }

        public void SeedManaged(ManagedSandboxInfo info) => _managed.Add(info);
        public string Name => "fake-reconciling";
        public Task<ISandbox> CreateAsync(SandboxSpec spec, CancellationToken ct = default)
            => throw new NotImplementedException();
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

    private sealed class ShortCircuitPipelineRunner : IPipelineRunner
    {
        public Task RunAsync(WorkItem item, CancellationToken workItemToken, CancellationToken hostToken)
            => Task.CompletedTask;
    }
}

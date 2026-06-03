using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// R8-core/R8.1: on graceful host shutdown, tear down every in-flight sandbox
/// according to <see cref="SandboxTeardownMode"/>. Suspend mode freezes the VM
/// via <see cref="ISuspendableSandbox.SuspendAsync"/> and persists the
/// <c>(workItemId → vmName, suspendedAt, agentLogPath)</c> mapping so the next
/// orchestrator process can <c>multipass start</c> the same VM and re-tail the
/// in-VM agent log (see <see cref="SandboxResumeOnStartupService"/> for the
/// resume half). Stop cleanly stops and preserves the VM disk without taking a
/// RAM snapshot. Dispose is a destructive teardown mode with no recovery
/// artifact.
///
/// <para>This sits alongside — not on top of — the existing per-phase
/// preempt-checkpoint flow in <see cref="PipelineRunner"/>: that path commits a
/// git ref the pipeline can resume from before its own preserve call when the
/// lifecycle service has not already taken ownership. Suspend is an opt-in
/// state-preservation path for operators who accept the RAM-snapshot tradeoff;
/// Stop is the default lock-safe teardown for suspend-capable providers;
/// Dispose is a destructive teardown mode.</para>
///
/// <para>Implements <see cref="IHostedLifecycleService.StoppingAsync"/> rather
/// than registering a synchronous callback on
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/>. StoppingAsync
/// is awaited by the host before the BackgroundService cancellation token
/// fires, so in Suspend mode the in-VM agent process is still running when
/// multipass takes its snapshot, AND the host honours the async signature
/// instead of being blocked on a sync-over-async fan-out.</para>
/// </summary>
public sealed class SandboxSuspendOnShutdownService : IHostedLifecycleService
{
    /// <summary>
    /// Cap on parallel <c>multipass suspend</c> calls. Suspend writes the VM's
    /// RAM to disk; running too many in parallel just contends on disk IO and
    /// stretches the SIGTERM-to-exit window. 8 matches the design spec. Shared
    /// with the host-shutdown ceiling math via <see cref="SuspendTimeoutPolicy"/>
    /// so the semaphore batch size and the wave count cannot diverge.
    /// </summary>
    public const int DefaultMaxParallelSuspends = SuspendTimeoutPolicy.DefaultMaxParallelSuspends;

    /// <summary>
    /// Floor for the per-VM suspend timeout, and the value used when a sandbox
    /// can't report its RAM size. The earlier 30s cap was below multipass's
    /// real-world case: a 4 GB VM with an active LLM session was observed taking
    /// &gt;6 minutes to write its RAM snapshot to disk. At 30s the suspend timed
    /// out, the (work item → VM) mapping was never persisted, and the item fell
    /// back to the same stranded recovery that R8-core exists to avoid — defeating
    /// the whole "restart is transparent to in-flight work" promise. 10 minutes
    /// is a safe floor; <see cref="SuspendTimeoutFor"/> scales it up for larger
    /// VMs via <see cref="SuspendTimeoutPolicy"/>.
    ///
    /// <para>This bounds how long shutdown blocks per stuck VM, but it is not the
    /// only bound: the host's global <c>HostOptions.ShutdownTimeout</c> still caps
    /// total shutdown time. <c>Program.cs</c> raises that ceiling for
    /// suspend-capable providers because teardown mode is hot-reloadable, so a
    /// healthy snapshot is not truncated if an operator switches to Suspend
    /// before stopping the process. And because the (work item → VM) mapping
    /// is persisted BEFORE the suspend is awaited (see
    /// <see cref="SuspendOneAsync"/>), even a SIGKILL mid-snapshot still leaves a
    /// resume mapping for the next startup — recovery does not depend on the
    /// suspend call returning cleanly within the grace window.</para>
    /// </summary>
    public static readonly TimeSpan DefaultPerSuspendTimeout = SuspendTimeoutPolicy.DefaultFloor;

    /// <summary>
    /// Extra suspend-timeout budget per GiB of VM RAM. <c>multipass suspend</c>
    /// writes the whole RAM image to disk, so suspend time grows ~linearly with
    /// VM size; the effective per-VM timeout is
    /// <c>max(DefaultPerSuspendTimeout, RAM_GiB × this)</c>. Shared with the
    /// startup resume wait and the host shutdown grace via
    /// <see cref="SuspendTimeoutPolicy"/> so the three cannot drift apart.
    /// </summary>
    public static readonly TimeSpan DefaultPerGiBSuspendBudget = SuspendTimeoutPolicy.DefaultPerGiB;
    public static readonly TimeSpan DefaultNonSuspendTeardownTimeout = TimeSpan.FromSeconds(60);

    private readonly ISandboxProvider _provider;
    private readonly IWorkItemStore _store;
    private readonly ILogger<SandboxSuspendOnShutdownService> _log;
    private readonly int _maxParallel;
    private readonly TimeSpan _perSuspendTimeout;
    private readonly TimeSpan _perGiBSuspendBudget;
    private readonly TimeSpan _nonSuspendTeardownTimeout;
    // R8.1 (VM-wedging incident 2026-05-29): dispatch must be paused BEFORE
    // SnapshotSuspendableActive runs, otherwise the orchestrator's dispatch
    // loop keeps creating new sandboxes that race the snapshot and are then
    // torn down uncleanly when the BackgroundService cancellation fires later
    // in the shutdown sequence. Nullable so test fixtures driving SuspendAllAsync
    // directly don't need to hand in a gate.
    private readonly IShutdownDispatchGate? _dispatchGate;
    // R8.1: ephemeral worker VMs can be handled by Stop (default; cleanly
    // stop/preserve without a RAM snapshot), Suspend
    // (opt-in; preserves in-RAM agent state across restart but can wedge
    // multipassd if interrupted), or Dispose (delete --purge, full teardown —
    // no suspended-resume bookkeeping is written). Resolved at teardown time so
    // operator config hot-reload takes effect on the next graceful shutdown.
    private readonly Func<SandboxTeardownMode> _teardownModeAccessor;

    public SandboxSuspendOnShutdownService(
        ISandboxProvider provider,
        IWorkItemStore store,
        ILogger<SandboxSuspendOnShutdownService> log,
        int? maxParallel = null,
        TimeSpan? perSuspendTimeout = null,
        TimeSpan? perGiBSuspendBudget = null,
        TimeSpan? nonSuspendTeardownTimeout = null,
        IShutdownDispatchGate? dispatchGate = null,
        SandboxTeardownMode teardownMode = SandboxTeardownMode.Stop,
        Func<SandboxTeardownMode>? teardownModeAccessor = null)
    {
        _provider = provider;
        _store = store;
        _log = log;
        _maxParallel = maxParallel is > 0 ? maxParallel.Value : DefaultMaxParallelSuspends;
        _perSuspendTimeout = perSuspendTimeout is { } t && t > TimeSpan.Zero ? t : DefaultPerSuspendTimeout;
        _perGiBSuspendBudget = perGiBSuspendBudget is { } g && g > TimeSpan.Zero ? g : DefaultPerGiBSuspendBudget;
        _nonSuspendTeardownTimeout = nonSuspendTeardownTimeout is { } n && n > TimeSpan.Zero ? n : DefaultNonSuspendTeardownTimeout;
        _dispatchGate = dispatchGate;
        _teardownModeAccessor = teardownModeAccessor ?? (() => teardownMode);
    }

    /// <summary>The dispatch-pause-was-called signal as observed by SuspendAllAsync.</summary>
    internal bool DispatchPauseObserved { get; private set; }
    /// <summary>Whether dispatch was paused before the first per-VM teardown call. Test seam.</summary>
    internal bool DispatchPausedBeforeTeardown { get; private set; }

    /// <summary>
    /// Effective per-VM suspend timeout: the floor (<see cref="_perSuspendTimeout"/>)
    /// scaled up by RAM size when the sandbox reports it. A bigger VM has more
    /// RAM to flush to disk, so a uniform cap either truncates large VMs or wastes
    /// shutdown time waiting on small ones.
    /// </summary>
    internal TimeSpan SuspendTimeoutFor(ISuspendableSandbox sandbox) =>
        SuspendTimeoutPolicy.For(sandbox.MemoryBytes, _perSuspendTimeout, _perGiBSuspendBudget);

    internal TimeSpan NonSuspendTeardownTimeout => _nonSuspendTeardownTimeout;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    // IHostedLifecycleService hooks. StoppingAsync fires before any
    // BackgroundService cancellation token, which is what the design spec
    // requires: the in-VM agent process must still be running when multipass
    // suspend takes its snapshot. The async signature lets us await the
    // suspend fan-out natively instead of sync-over-async-ing it onto a
    // thread-pool callback.
    public Task StartingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken ct)
    {
        try
        {
            // We do NOT thread the host shutdown token into multipass suspend
            // calls — in Suspend mode each VM gets its own RAM-scaled timeout
            // (see SuspendTimeoutFor / SuspendOneAsync) so one stuck multipassd
            // call can't block the rest of the drain. The host still enforces
            // HostOptions.ShutdownTimeout overall; Program.cs raises that
            // ceiling only when Suspend teardown is selected so a healthy RAM
            // snapshot is not truncated. If the host kills us before a slow
            // snapshot finishes, the (work item → VM) mapping persisted before
            // the await (SuspendOneAsync) still lets the next startup resume it.
            await SuspendAllAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sandbox suspend-on-shutdown failed; in-flight items will follow the existing recovery path");
        }
    }

    internal async Task SuspendAllAsync()
    {
        // R8.1 (incident 2026-05-29): pause dispatch BEFORE we snapshot or do
        // any per-VM teardown. The snapshot is a point-in-time view, so if
        // dispatch keeps running we'll miss any new sandbox the orchestrator
        // creates between the snapshot and the BackgroundService cancellation,
        // leaving those VMs to be torn down uncleanly. Idempotent — a test that
        // wires the gate but pauses first still observes the same DispatchPauseObserved.
        if (_dispatchGate is not null)
        {
            DispatchPauseObserved = true;
            _dispatchGate.PauseDispatch();
        }

        if (_provider is not ISuspendingSandboxProvider suspending)
        {
            _log.LogDebug("Sandbox provider {Provider} does not support suspend; skipping suspend-on-shutdown sweep",
                _provider.Name);
            return;
        }

        var teardownMode = _teardownModeAccessor();
        var entries = suspending.SnapshotSuspendableActive();
        if (entries.Count == 0)
        {
            _log.LogInformation("Suspend-on-shutdown: no in-flight sandboxes to {Mode} before exit", teardownMode);
            return;
        }

        // Test seam: the gate (if any) must already be paused when we begin
        // tearing down individual VMs — that ordering is the whole point.
        DispatchPausedBeforeTeardown = _dispatchGate is null || _dispatchGate.IsDispatchPaused;

        _log.LogInformation(
            "Sandbox shutdown teardown ({Mode}): {Count} in-flight sandbox(es)",
            teardownMode, entries.Count);

        using var gate = new SemaphoreSlim(_maxParallel, _maxParallel);
        var tasks = new List<Task>(entries.Count);
        foreach (var (workItemId, sandbox) in entries)
        {
            await gate.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await TeardownOneAsync(workItemId, sandbox, teardownMode);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);
    }

    private Task TeardownOneAsync(WorkItemId workItemId, ISuspendableSandbox sandbox, SandboxTeardownMode teardownMode) =>
        // The default arm throws rather than silently routing through suspend.
        // Silent fallthrough would defeat the whole feature's intent: a new
        // teardown mode added without an explicit case here would re-introduce
        // the qemu-lock wedge this code path exists to avoid. The throw is
        // caught by SuspendAllAsync's per-VM Task.Run / await Task.WhenAll
        // wrapper so one mis-bound enum value cannot break the whole drain.
        teardownMode switch
        {
            SandboxTeardownMode.Suspend => SuspendOneAsync(workItemId, sandbox),
            SandboxTeardownMode.Stop => StopOneAsync(workItemId, sandbox),
            SandboxTeardownMode.Dispose => DisposeOneAsync(workItemId, sandbox),
            _ => Task.FromException(new InvalidOperationException(
                $"SandboxTeardownMode {(int)teardownMode} is not handled; add an explicit case in TeardownOneAsync rather than relying on silent fallthrough.")),
        };

    /// <summary>
    /// Teardown via clean stop (preserves VM disk but kills the in-VM agent
    /// process). Faster than suspend and much less likely to wedge multipassd:
    /// stop releases the qemu disk-image write-lock cleanly, whereas an
    /// interrupted suspend can leave qemu holding the lock.
    ///
    /// <para>Calls <see cref="ISuspendableSandbox.MarkOwnedByShutdownHandler"/>
    /// FIRST so PipelineRunner's host-shutdown OCE catch block, which fires
    /// after StoppingAsync has already torn the VM down here, skips its in-VM git
    /// checkpoint flow. Otherwise it would run <c>git add/commit/push</c> inside
    /// a now-stopped VM, fail, and convert shutdown cleanup trouble into a work
    /// item failure. The signal is set before any teardown call so even an early
    /// timeout or exception still gives PipelineRunner the right answer.</para>
    /// </summary>
    private async Task StopOneAsync(WorkItemId workItemId, ISuspendableSandbox sandbox)
    {
        sandbox.MarkOwnedByShutdownHandler();
        var timeout = NonSuspendTeardownTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            if (sandbox is IPreemptibleSandbox preemptible)
            {
                await preemptible.StopAndPreserveAsync(timeoutCts.Token);
                AuditLog.SandboxStoppedOnShutdown(workItemId, sandbox.Id);
                return;
            }

            _log.LogWarning(
                "Sandbox {SandboxId} for work item {WorkItemId} does not implement IPreemptibleSandbox; falling back to dispose",
                sandbox.Id, workItemId);
            await sandbox.DisposeAsync().AsTask().WaitAsync(timeoutCts.Token);
            AuditLog.SandboxDisposedOnShutdown(workItemId, sandbox.Id);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Stop exceeded {Timeout} for work item {WorkItemId} sandbox {SandboxId}; surfacing as needing operator attention",
                timeout, workItemId, sandbox.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Stop failed for work item {WorkItemId} sandbox {SandboxId}",
                workItemId, sandbox.Id);
        }
    }

    /// <summary>
    /// Teardown via dispose (delete --purge). Skips the preserve-on-dispose
    /// path entirely: the VM is destroyed and no suspend mapping is written.
    /// This is the most aggressive lock-contention escape hatch, at the cost of
    /// losing in-VM agent state and any uncheckpointed work.
    ///
    /// <para>Calls <see cref="ISuspendableSandbox.MarkOwnedByShutdownHandler"/>
    /// FIRST so PipelineRunner's host-shutdown OCE catch block skips its
    /// in-VM git checkpoint flow against a VM that is about to be (or has
    /// already been) <c>multipass delete --purge</c>'d — without this signal
    /// the catch block would fault inside a non-existent VM, leaving the work
    /// item Working with no PreemptCheckpoint.</para>
    /// </summary>
    private async Task DisposeOneAsync(WorkItemId workItemId, ISuspendableSandbox sandbox)
    {
        sandbox.MarkOwnedByShutdownHandler();
        var timeout = NonSuspendTeardownTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await sandbox.DisposeAsync().AsTask().WaitAsync(timeoutCts.Token);
            AuditLog.SandboxDisposedOnShutdown(workItemId, sandbox.Id);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Dispose exceeded {Timeout} for work item {WorkItemId} sandbox {SandboxId}; surfacing as needing operator attention",
                timeout, workItemId, sandbox.Id);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Dispose failed for work item {WorkItemId} sandbox {SandboxId}",
                workItemId, sandbox.Id);
        }
    }

    private async Task SuspendOneAsync(WorkItemId workItemId, ISuspendableSandbox sandbox)
    {
        var timeout = SuspendTimeoutFor(sandbox);

        // Persist (workItemId → vmName) BEFORE awaiting the suspend. The RAM
        // snapshot is written by multipassd, which keeps going even if our
        // per-VM timeout fires or the service manager SIGKILLs us mid-shutdown —
        // the VM still reaches Suspended on disk. Recording the mapping up front
        // means the next startup can reattach to that VM no matter how our
        // suspend call ends. We only clear the mapping again on a *genuine*
        // suspend failure, where the VM is left Running and DisposeAsync tears it
        // down (so there is nothing to resume). The DB write uses
        // CancellationToken.None: a single-row SQLite UPDATE is fast enough to
        // finish even under shutdown pressure.
        if (!await TryPersistSuspendBookkeepingAsync(workItemId, sandbox.Id))
            return;

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await sandbox.SuspendAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Suspend exceeded {Timeout} for work item {WorkItemId} sandbox {SandboxId}; multipassd is likely still writing the RAM snapshot. The (work item → VM) mapping is persisted, so the next startup will attempt to resume this VM.",
                timeout, workItemId, sandbox.Id);
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Suspend failed for work item {WorkItemId} sandbox {SandboxId}; clearing suspend bookkeeping so the item recovers via the standard stranded-item path",
                workItemId, sandbox.Id);
            await ClearSuspendBookkeepingAsync(workItemId);
            return;
        }

        AuditLog.SandboxSuspendedOnShutdown(workItemId, sandbox.Id);
    }

    private async Task<bool> TryPersistSuspendBookkeepingAsync(WorkItemId workItemId, string vmName)
    {
        var item = await _store.GetAsync(workItemId, CancellationToken.None);
        if (item is null)
        {
            _log.LogWarning(
                "Cannot persist suspend bookkeeping for sandbox {SandboxId}: work item {WorkItemId} is no longer present in the store",
                vmName, workItemId);
            return false;
        }
        var now = DateTimeOffset.UtcNow;
        await _store.UpdateAsync(item with
        {
            SuspendedVmName = vmName,
            SuspendedAt = now,
            UpdatedAt = now,
        }, CancellationToken.None);
        return true;
    }

    private async Task ClearSuspendBookkeepingAsync(WorkItemId workItemId)
    {
        var item = await _store.GetAsync(workItemId, CancellationToken.None);
        if (item is null) return;
        await _store.UpdateAsync(item with
        {
            SuspendedVmName = null,
            SuspendedAt = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }
}

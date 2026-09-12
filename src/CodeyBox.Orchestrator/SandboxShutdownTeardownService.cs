using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// R8-core/R8.1: on graceful host shutdown, handle every in-flight sandbox
/// according to <see cref="SandboxTeardownMode"/>. Suspend mode freezes the VM
/// via <see cref="ISuspendableSandbox.SuspendAsync"/> and persists the
/// <c>(workItemId → vmName, suspendedAt, agentLogPath)</c> mapping so the next
/// orchestrator process can <c>multipass start</c> the same VM and re-tail the
/// in-VM agent log (see <see cref="SandboxResumeOnStartupService"/> for the
/// resume half). Stop avoids the RAM snapshot. It leaves Working/Reworking
/// items that still need a preempt checkpoint to <see cref="PipelineRunner"/>,
/// and stop/preserves other recoverable active VMs via
/// <see cref="IPreemptibleSandbox.StopAndPreserveAsync"/>. Dispose is a
/// destructive teardown mode with no recovery artifact.
///
/// <para>This sits alongside — not on top of — the existing per-phase
/// preempt-checkpoint flow in <see cref="PipelineRunner"/>. All teardown modes
/// use the same active-sandbox snapshot after dispatch has paused. Suspend is
/// an opt-in state-preservation path for operators who accept the RAM-snapshot
/// tradeoff; Stop avoids RAM snapshots while preserving PipelineRunner's
/// checkpoint recovery path; Dispose is destructive.</para>
///
/// <para>Implements <see cref="IHostedLifecycleService.StoppingAsync"/> rather
/// than registering a synchronous callback on
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/>. StoppingAsync
/// is awaited by the host before the BackgroundService cancellation token
/// fires, so in Suspend mode the in-VM agent process is still running when
/// multipass takes its snapshot, AND the host honours the async signature
/// instead of being blocked on a sync-over-async fan-out.</para>
/// </summary>
public sealed class SandboxShutdownTeardownService : IHostedLifecycleService
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

    /// <summary>
    /// Fallback per-VM timeout for Stop/Dispose teardown when the service is
    /// constructed outside production DI. Program.cs passes
    /// <c>ShutdownOptions.GraceSeconds</c> for this value; the fallback mirrors
    /// that option's 60 second default rather than defining a separate policy.
    /// </summary>
    public static readonly TimeSpan DefaultNonSuspendTeardownTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Default overall budget for the Stop/Dispose per-VM teardown fan-out.
    /// Caps the whole <see cref="TeardownAllAsync"/> VM-teardown phase (not each
    /// VM) so shutdown completes well within the service manager's stop timeout
    /// even when several VMs hang or the daemon is wedged: VMs still running
    /// when the budget expires are left for the next boot's startup
    /// reconciliation and recovery sweeps. Suspend mode is excluded by design —
    /// its RAM-scaled per-VM timeouts and the host-shutdown ceiling already
    /// account for long snapshots, and aborting a snapshot early would defeat
    /// the opt-in state-preservation contract (the pre-suspend mapping still
    /// lets the next startup resume, but the VM is left Running, not Suspended).
    /// </summary>
    public static readonly TimeSpan DefaultTeardownBudget = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Upper bound for <c>ShutdownOptions.SandboxTeardownTimeout</c>. Large
    /// enough for operators with many VMs who also raise their service
    /// manager's stop timeout; the validator rejects anything higher so a
    /// typo cannot silently reintroduce the SIGKILL-mid-teardown wedge.
    /// </summary>
    public static readonly TimeSpan MaxTeardownBudget = TimeSpan.FromMinutes(10);

    private readonly ISandboxProvider _provider;
    private readonly IWorkItemStore _store;
    private readonly ILogger<SandboxShutdownTeardownService> _log;
    private readonly int _maxParallel;
    private readonly TimeSpan _perSuspendTimeout;
    private readonly TimeSpan _perGiBSuspendBudget;
    private readonly TimeSpan _nonSuspendTeardownTimeout;
    // R8.1 (VM-wedging incident 2026-05-29): dispatch must be paused BEFORE
    // SnapshotActiveSandboxes runs, otherwise the orchestrator's dispatch
    // loop keeps creating new sandboxes that race the snapshot and are then
    // torn down uncleanly when the BackgroundService cancellation fires later
    // in the shutdown sequence. Nullable so test fixtures driving TeardownAllAsync
    // directly don't need to hand in a gate.
    private readonly IShutdownDispatchGate? _dispatchGate;
    // R8.1: ephemeral worker VMs can be handled by Stop (default; use
    // PipelineRunner's preempt-checkpoint flow for Working items, otherwise
    // stop/preserve without taking a RAM snapshot), Suspend (opt-in; preserves
    // in-RAM agent state across restart but can wedge multipassd if interrupted),
    // or Dispose (delete --purge, full teardown — no suspended-resume
    // bookkeeping is written). Resolved at teardown time so operator config
    // hot-reload takes effect on the next graceful shutdown.
    private readonly Func<SandboxTeardownMode> _teardownModeAccessor;
    // Overall budget for the Stop/Dispose per-VM fan-out (see
    // DefaultTeardownBudget). Resolved at teardown time like the mode so
    // operator config hot-reload takes effect on the next graceful shutdown.
    private readonly Func<TimeSpan> _teardownBudgetAccessor;
    // Drives the per-VM NonSuspend teardown timeout's CancellationTokenSource
    // timer. Defaults to TimeProvider.System in production; tests that exercise
    // the hung-stop/hung-dispose cancellation path inject a FakeTimeProvider so
    // the timeout fires deterministically on Advance() instead of relying on a
    // 25ms wall-clock timer, which is flaky under scheduler contention when the
    // full test suite runs in parallel (observed as a 12s WaitAsync timeout
    // instead of the ~25ms the inner timer nominally promises).
    private readonly TimeProvider _timeProvider;

    public SandboxShutdownTeardownService(
        ISandboxProvider provider,
        IWorkItemStore store,
        ILogger<SandboxShutdownTeardownService> log,
        int? maxParallel = null,
        TimeSpan? perSuspendTimeout = null,
        TimeSpan? perGiBSuspendBudget = null,
        TimeSpan? nonSuspendTeardownTimeout = null,
        IShutdownDispatchGate? dispatchGate = null,
        SandboxTeardownMode? teardownMode = null,
        Func<SandboxTeardownMode>? teardownModeAccessor = null,
        TimeProvider? timeProvider = null,
        TimeSpan? teardownBudget = null,
        Func<TimeSpan>? teardownBudgetAccessor = null)
    {
        _provider = provider;
        _store = store;
        _log = log;
        _maxParallel = maxParallel is > 0 ? maxParallel.Value : DefaultMaxParallelSuspends;
        _perSuspendTimeout = perSuspendTimeout is { } t && t > TimeSpan.Zero ? t : DefaultPerSuspendTimeout;
        _perGiBSuspendBudget = perGiBSuspendBudget is { } g && g > TimeSpan.Zero ? g : DefaultPerGiBSuspendBudget;
        _nonSuspendTeardownTimeout = nonSuspendTeardownTimeout is { } n && n > TimeSpan.Zero ? n : DefaultNonSuspendTeardownTimeout;
        _dispatchGate = dispatchGate;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (teardownModeAccessor is not null)
        {
            _teardownModeAccessor = teardownModeAccessor;
        }
        else if (teardownMode is { } fixedTeardownMode)
        {
            _teardownModeAccessor = () => fixedTeardownMode;
        }
        else
        {
            throw new ArgumentException(
                "Provide teardownModeAccessor from bound ShutdownOptions, or pass an explicit teardownMode for tests.",
                nameof(teardownMode));
        }
        if (teardownBudgetAccessor is not null)
        {
            _teardownBudgetAccessor = teardownBudgetAccessor;
        }
        else if (teardownBudget is { } fixedTeardownBudget)
        {
            _teardownBudgetAccessor = () => fixedTeardownBudget;
        }
        else
        {
            _teardownBudgetAccessor = () => DefaultTeardownBudget;
        }
    }

    /// <summary>The dispatch-pause-was-called signal as observed by TeardownAllAsync.</summary>
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

    internal TimeSpan TeardownBudget
    {
        get
        {
            var budget = _teardownBudgetAccessor();
            return budget > TimeSpan.Zero ? budget : DefaultTeardownBudget;
        }
    }

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
            // HostOptions.ShutdownTimeout overall; Program.cs keeps that
            // ceiling capability-based for suspending providers because teardown
            // mode is hot-reloadable, so a later switch to Suspend still has
            // room for a healthy RAM snapshot. If the host kills us before a
            // slow snapshot finishes, the (work item → VM) mapping persisted
            // before the await (SuspendOneAsync) still lets the next startup
            // resume it.
            //
            // For Stop/Dispose the token IS honoured: the per-VM fan-out runs
            // under an overall teardown budget (see TeardownBudget) linked with
            // this token, so neither a hung daemon nor a large VM fleet can
            // push shutdown past the service manager's stop timeout.
            await TeardownAllAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sandbox shutdown teardown failed; in-flight items will follow the existing recovery path");
        }
    }

    internal Task TeardownAllAsync() => TeardownAllAsync(CancellationToken.None);

    internal async Task TeardownAllAsync(CancellationToken hostShutdownToken)
    {
        // R8.1 (incident 2026-05-29): pause dispatch BEFORE we either snapshot
        // for Suspend/Stop/Dispose. Idempotent — a test that wires the gate but
        // pauses first still observes the same DispatchPauseObserved.
        if (_dispatchGate is not null)
        {
            DispatchPauseObserved = true;
            _dispatchGate.PauseDispatch();
        }

        var teardownMode = _teardownModeAccessor();
        if (_provider is not IActiveSandboxProvider activeProvider)
        {
            _log.LogDebug("Sandbox provider {Provider} does not expose active sandbox teardown; skipping shutdown sweep",
                _provider.Name);
            return;
        }

        var entries = activeProvider.SnapshotActiveSandboxes();

        // Shutdown ordering (SIGKILL-safety): checkpoint every in-flight item
        // to its durable resume point BEFORE the first VM call, so a force-kill
        // at any later point in shutdown still leaves recoverable rows. The
        // checkpoint consumes no recovery attempt — the worker-abort path (on a
        // clean shutdown) or the startup replay/reaper sweep (after a SIGKILL)
        // counts it exactly once. Suspend mode is excluded: its per-item
        // (work item → VM) mapping, persisted before each suspend is awaited,
        // is its checkpoint, and checkpointing state up front would fight the
        // resume path that expects the pre-suspend state intact.
        if (teardownMode is SandboxTeardownMode.Stop or SandboxTeardownMode.Dispose)
            await CheckpointInflightItemsAsync(entries, teardownMode);

        if (entries.Count == 0)
        {
            _log.LogInformation("Shutdown teardown: no in-flight sandboxes to {Mode} before exit", teardownMode);
            return;
        }

        // Test seam: the gate (if any) must already be paused when we begin
        // tearing down individual VMs — that ordering is the whole point.
        DispatchPausedBeforeTeardown = _dispatchGate is null || _dispatchGate.IsDispatchPaused;

        _log.LogInformation(
            "Sandbox shutdown teardown ({Mode}): {Count} in-flight sandbox(es)",
            teardownMode, entries.Count);

        if (teardownMode is SandboxTeardownMode.Stop or SandboxTeardownMode.Dispose)
        {
            await TeardownAllBoundedAsync(entries, teardownMode, hostShutdownToken);
            return;
        }

        using var gate = new SemaphoreSlim(_maxParallel, _maxParallel);
        var tasks = new List<Task>(entries.Count);
        foreach (var (workItemId, sandbox) in entries)
        {
            await gate.WaitAsync(hostShutdownToken);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await TeardownOneAsync(workItemId, sandbox, teardownMode, CancellationToken.None);
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Persist an interruption checkpoint for every snapshotted in-flight item
    /// before any VM teardown call. Sequential single-row reads/writes against
    /// the local store: milliseconds per item, no VM or network calls, so this
    /// phase cannot stretch the SIGTERM-to-exit window. Items whose phase still
    /// needs PipelineRunner's preempt checkpoint (Stop mode) are deliberately
    /// left alone — their checkpoint is produced by the pipeline's own
    /// host-shutdown path during the worker drain.
    /// </summary>
    private async Task CheckpointInflightItemsAsync(
        IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> entries,
        SandboxTeardownMode teardownMode)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (workItemId, sandbox) in entries)
        {
            try
            {
                // CancellationToken.None throughout: each op is a single-row
                // local SQLite read/write that finishes in milliseconds even
                // under shutdown pressure, and this phase must complete before
                // any VM call so a later SIGKILL still leaves recoverable rows
                // (same rationale as the pre-suspend bookkeeping write).
                var item = await _store.GetAsync(workItemId, CancellationToken.None);
                if (item is null)
                {
                    _log.LogDebug(
                        "Shutdown checkpoint: work item {WorkItemId} for sandbox {SandboxId} is gone; skipping",
                        workItemId, sandbox.Id);
                    continue;
                }
                if (teardownMode == SandboxTeardownMode.Stop
                    && WorkItemRecoveryPolicy.RequiresPipelinePreemptCheckpointBeforeLifecycleTeardown(item))
                {
                    _log.LogInformation(
                        "Shutdown checkpoint: work item {WorkItemId} still needs PipelineRunner's preempt checkpoint; leaving it for the worker drain",
                        workItemId);
                    continue;
                }
                var interrupted = WorkItemRecoveryPolicy.BuildHostShutdownInterruptedState(item, now, HostShutdownCheckpointReason);
                if (interrupted is null)
                    continue;
                if (await _store.TryUpdateIfStateAsync(interrupted, item.State, CancellationToken.None))
                {
                    _log.LogInformation(
                        "Shutdown checkpoint: work item {WorkItemId} {FromState} -> {ToState} (interrupted; clean restart on next boot)",
                        workItemId, item.State, interrupted.State);
                    AuditLog.WorkItemInterruptedByHostShutdown(workItemId, item.State, interrupted.State);
                }
                else
                {
                    _log.LogDebug(
                        "Shutdown checkpoint skipped {WorkItemId}: state changed from {State} before the checkpoint write",
                        workItemId, item.State);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex,
                    "Shutdown checkpoint failed for work item {WorkItemId}; teardown proceeds and existing recovery paths still apply",
                    workItemId);
            }
        }
    }

    /// <summary>
    /// Stop/Dispose fan-out under an overall teardown budget linked with the
    /// host shutdown token. VMs that are still running when the budget expires
    /// (or when the host cancels shutdown) are left alone and reported: the
    /// next boot's startup reconciliation and stranded-item recovery reclaim
    /// both the VM and the (already checkpointed) work item. Never throws for
    /// budget/host-cancel expiry — the host must proceed to the worker drain.
    /// </summary>
    private async Task TeardownAllBoundedAsync(
        IReadOnlyList<(WorkItemId WorkItemId, IShutdownTeardownSandbox Sandbox)> entries,
        SandboxTeardownMode teardownMode,
        CancellationToken hostShutdownToken)
    {
        var budget = TeardownBudget;
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(hostShutdownToken);
        budgetCts.CancelAfter(budget);
        var budgetToken = budgetCts.Token;

        using var gate = new SemaphoreSlim(_maxParallel, _maxParallel);
        var tasks = new List<Task<bool>>(entries.Count);
        foreach (var (workItemId, sandbox) in entries)
        {
            try
            {
                await gate.WaitAsync(budgetToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    return await TeardownOneAsync(workItemId, sandbox, teardownMode, budgetToken);
                }
                finally
                {
                    gate.Release();
                }
            }, CancellationToken.None));
        }

        var completed = 0;
        try
        {
            var results = await Task.WhenAll(tasks);
            completed = results.Count(r => r);
        }
        catch (OperationCanceledException) when (budgetToken.IsCancellationRequested)
        {
            // Budget/host-cancel expiry aborts the join, not the shutdown:
            // fall through to the leftover report below.
        }

        var total = entries.Count;
        if (completed < total)
        {
            _log.LogWarning(
                "Sandbox shutdown teardown ({Mode}) completed {Completed}/{Total} within {Budget}; {Left} sandbox(es) left running for startup reconciliation and recovery on next boot",
                teardownMode, completed, total, budget, total - completed);
        }
    }

    /// <summary>
    /// LastError note stamped by <see cref="CheckpointInflightItemsAsync"/> so a
    /// post-mortem can tell a pre-teardown checkpoint from the worker-abort or
    /// startup recoveries that may follow it.
    /// </summary>
    internal const string HostShutdownCheckpointReason =
        "interrupted by host shutdown before VM teardown";

    private Task<bool> TeardownOneAsync(
        WorkItemId workItemId,
        IShutdownTeardownSandbox sandbox,
        SandboxTeardownMode teardownMode,
        CancellationToken teardownToken) =>
        // The default arm throws rather than silently routing through suspend.
        // Silent fallthrough would defeat the whole feature's intent: a new
        // teardown mode added without an explicit case here would re-introduce
        // the qemu-lock wedge this code path exists to avoid. Task.WhenAll
        // propagates the fault to StoppingAsync, where the lifecycle-level catch
        // logs it and lets the host continue shutting down.
        teardownMode switch
        {
            SandboxTeardownMode.Suspend => SuspendOneAsync(workItemId, sandbox),
            SandboxTeardownMode.Stop => StopOneAsync(workItemId, sandbox, teardownToken),
            SandboxTeardownMode.Dispose => DisposeOneAsync(workItemId, sandbox, teardownToken),
            _ => Task.FromException<bool>(new InvalidOperationException(
                $"SandboxTeardownMode {(int)teardownMode} is not handled; add an explicit case in TeardownOneAsync rather than relying on silent fallthrough.")),
        };

    /// <summary>
    /// Teardown via stop/preserve. Avoids the RAM snapshot that makes
    /// <c>multipass suspend</c> risky. Falls back to dispose only when a
    /// recoverable sandbox lacks stop/preserve support.
    /// Returns true when the entry needs no further handling (stopped, left
    /// running for PipelineRunner by design, or disposed via fallback);
    /// false when the VM was left in an unknown state by a timeout or by the
    /// overall teardown budget / host shutdown firing first.
    /// </summary>
    private async Task<bool> StopOneAsync(WorkItemId workItemId, IShutdownTeardownSandbox sandbox, CancellationToken teardownToken)
    {
        var (loaded, item) = await TryLoadWorkItemForStopTeardownAsync(workItemId, sandbox.Id);
        if (!loaded)
            return true;

        if (item is not null && WorkItemRecoveryPolicy.RequiresPipelinePreemptCheckpointBeforeLifecycleTeardown(item))
        {
            _log.LogInformation(
                "Stop teardown selected for work item {WorkItemId} sandbox {SandboxId}, but the active agent phase still needs PipelineRunner's preempt checkpoint; leaving it running for host-shutdown recovery",
                workItemId, sandbox.Id);
            return true;
        }

        if (sandbox is not IPreemptibleSandbox preemptible)
        {
            _log.LogWarning(
                "Stop teardown selected for work item {WorkItemId} sandbox {SandboxId}, but the sandbox does not support stop/preserve; falling back to dispose",
                workItemId, sandbox.Id);
            return await DisposeOneAsync(workItemId, sandbox, teardownToken);
        }

        var timeout = NonSuspendTeardownTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, teardownToken);
        try
        {
            await preemptible.StopAndPreserveAsync(linkedCts.Token).WaitAsync(linkedCts.Token);
            sandbox.MarkOwnedByShutdownHandler();
            AuditLog.SandboxStoppedOnShutdown(workItemId, sandbox.Id);
            return true;
        }
        catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested)
        {
            _log.LogWarning(
                "Stop/preserve cut short by the shutdown teardown budget for work item {WorkItemId} sandbox {SandboxId}; leaving it running for startup reconciliation and recovery on next boot",
                workItemId, sandbox.Id);
            return false;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Stop/preserve exceeded {Timeout} for work item {WorkItemId} sandbox {SandboxId}; surfacing as needing operator attention",
                timeout, workItemId, sandbox.Id);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Stop/preserve failed for work item {WorkItemId} sandbox {SandboxId}",
                workItemId, sandbox.Id);
            throw;
        }
    }

    private async Task<(bool Loaded, WorkItem? Item)> TryLoadWorkItemForStopTeardownAsync(
        WorkItemId workItemId,
        string sandboxId)
    {
        try
        {
            return (true, await _store.GetAsync(workItemId, CancellationToken.None));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Cannot inspect work item {WorkItemId} before stop teardown for sandbox {SandboxId}; leaving it running for PipelineRunner or startup recovery",
                workItemId, sandboxId);
            return (false, null);
        }
    }

    /// <summary>
    /// Teardown via dispose (delete --purge). Skips the preserve-on-dispose
    /// path entirely: the VM is destroyed and no suspend mapping is written.
    /// This is the most aggressive lock-contention escape hatch, at the cost of
    /// losing in-VM agent state and any uncheckpointed work.
    ///
    /// <para>Calls <see cref="IShutdownTeardownSandbox.MarkOwnedByShutdownHandler"/>
    /// FIRST so PipelineRunner's host-shutdown OCE catch block skips its
    /// in-VM git checkpoint flow against a VM that is about to be (or has
    /// already been) <c>multipass delete --purge</c>'d — without this signal
    /// the catch block would fault inside a non-existent VM, leaving the work
    /// item Working/Reworking with no PreemptCheckpoint.</para>
    ///
    /// <para>Returns true when the VM was disposed; false when the dispose was
    /// cut short by the per-VM timeout or the overall teardown budget / host
    /// shutdown (the VM is left for startup reconciliation).</para>
    /// </summary>
    private async Task<bool> DisposeOneAsync(WorkItemId workItemId, IShutdownTeardownSandbox sandbox, CancellationToken teardownToken)
    {
        sandbox.MarkOwnedByShutdownHandler();
        var timeout = NonSuspendTeardownTimeout;
        using var timeoutCts = new CancellationTokenSource(timeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, teardownToken);
        try
        {
            await sandbox.DisposeAsync().AsTask().WaitAsync(linkedCts.Token);
            var metrics = sandbox.ResourceMetrics;
            AuditLog.SandboxDisposedOnShutdown(
                workItemId,
                sandbox.Id,
                metrics);
            return true;
        }
        catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested)
        {
            _log.LogWarning(
                "Dispose cut short by the shutdown teardown budget for work item {WorkItemId} sandbox {SandboxId}; leaving it for startup reconciliation and recovery on next boot",
                workItemId, sandbox.Id);
            return false;
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Dispose exceeded {Timeout} for work item {WorkItemId} sandbox {SandboxId}; surfacing as needing operator attention",
                timeout, workItemId, sandbox.Id);
            return false;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Dispose failed for work item {WorkItemId} sandbox {SandboxId}",
                workItemId, sandbox.Id);
            throw;
        }
    }

    private async Task<bool> SuspendOneAsync(WorkItemId workItemId, IShutdownTeardownSandbox sandbox)
    {
        if (sandbox is not ISuspendableSandbox suspendable)
        {
            _log.LogWarning(
                "Suspend teardown selected for work item {WorkItemId} sandbox {SandboxId}, but the sandbox does not support suspend; leaving it for normal shutdown recovery",
                workItemId, sandbox.Id);
            return true;
        }

        var timeout = SuspendTimeoutFor(suspendable);

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
            return true;

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            await suspendable.SuspendAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Suspend exceeded {Timeout} for work item {WorkItemId} sandbox {SandboxId}; multipassd is likely still writing the RAM snapshot. The (work item → VM) mapping is persisted, so the next startup will attempt to resume this VM.",
                timeout, workItemId, sandbox.Id);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Suspend failed for work item {WorkItemId} sandbox {SandboxId}; clearing suspend bookkeeping so the item recovers via the standard stranded-item path",
                workItemId, sandbox.Id);
            await ClearSuspendBookkeepingAsync(workItemId);
            return true;
        }

        AuditLog.SandboxSuspendedOnShutdown(workItemId, sandbox.Id);
        return true;
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

using CodeyBox.Core;
using CodeyBox.Sandbox;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

public enum SandboxStartupResumeMode
{
    Background = 0,
    Blocking = 1,
}

public sealed record SandboxStartupResumeOptions
{
    public int MaxParallelResumes { get; init; } = SandboxResumeOnStartupService.DefaultMaxParallelResumes;
    public TimeSpan ResumeTimeout { get; init; } = SandboxResumeOnStartupService.DefaultResumeTimeout;
    public TimeSpan AdoptionDeadline { get; init; } = SandboxResumeOnStartupService.DefaultAdoptionDeadline;
    public SandboxStartupResumeMode Mode { get; init; } = SandboxStartupResumeMode.Background;
}

/// <summary>
/// R8-core startup half: for every work item the previous process suspended
/// (<see cref="WorkItem.SuspendedVmName"/> non-null), <c>multipass start</c>
/// the persisted VM, re-tail the in-VM agent log so the orchestrator can
/// observe what the resumed agent emits post-resume, wait for the wrapper's
/// completion marker, then clear the suspend bookkeeping so the standard
/// stranded-item recovery path can re-engage the pipeline.
///
/// <para>Implemented as <see cref="IHostedLifecycleService.StartingAsync"/>
/// (sibling of <see cref="SandboxShutdownTeardownService.StoppingAsync"/>)
/// only when configured for blocking mode. The default background mode starts
/// the resume sweep from <see cref="StartAsync"/> and signals
/// <see cref="IStartupRecoveryInputSink"/> when done, so the HTTP listener can
/// bind while <see cref="OrchestratorService.ExecuteAsync"/> waits before its
/// dead-worker startup sweep. Sequencing matters: the leak reaper sees a
/// consistent picture (the VM is back to Running, the work item still carries
/// SuspendedVmName until adoption completes) and recovery doesn't race the
/// resume.</para>
///
/// <para>Adoption flow per item:</para>
/// <list type="number">
///   <item><c>multipass start &lt;name&gt;</c> via <see cref="ISuspendingSandboxProvider.ResumeSandboxAsync"/>.</item>
///   <item>If the item carries <see cref="WorkItem.AgentLogPath"/>: re-tail the
///   file inside the VM via <see cref="ISuspendingSandboxProvider.WaitForAdoptedAgentCompletionAsync"/>
///   and stream what we see to the orchestrator log.</item>
///   <item>On wrapper exit marker / wait timeout / resume failure: clear
///   <see cref="WorkItem.SuspendedVmName"/> + <see cref="WorkItem.SuspendedAt"/>
///   so the leak reaper can recycle the VM if anything is orphaned, and the
///   stranded-item recovery path can re-dispatch the work item.</item>
/// </list>
///
/// <para>Best-effort: a failed resume or a timed-out wait clears the
/// bookkeeping so a transient multipassd outage does not leave items
/// suspended forever; the work item's existing PreemptCheckpoint (if any)
/// then drives recovery. A successful resume + adoption means the agent's
/// post-resume work is observed end-to-end.</para>
/// </summary>
public sealed class SandboxResumeOnStartupService : IHostedLifecycleService
{
    /// <summary>
    /// Cap on parallel <c>multipass start</c> calls. Mirrors
    /// <see cref="SandboxShutdownTeardownService.DefaultMaxParallelSuspends"/>
    /// so the resume side cannot flood multipassd worse than the suspend side
    /// already did at shutdown time.
    /// </summary>
    public const int DefaultMaxParallelResumes = SandboxShutdownTeardownService.DefaultMaxParallelSuspends;

    /// <summary>
    /// Default upper bound on how long we wait for an adopted in-VM agent
    /// process to finish post-resume. Long enough that a real LLM call can
    /// finish (typical work-phase agent invocations are minutes to tens of
    /// minutes), short enough that a wedged agent does not block the
    /// orchestrator boot indefinitely. Configurable via constructor.
    /// </summary>
    public static readonly TimeSpan DefaultAdoptionDeadline =
        SandboxStartupResumePolicy.DefaultAdoptionDeadline;

    /// <summary>
    /// Default caller-side cap for a single persisted VM resume. The Multipass
    /// provider has its own launch/readiness limits, but the orchestrator also
    /// needs an outer guard for daemon/provider calls that ignore cancellation.
    /// </summary>
    public static readonly TimeSpan DefaultResumeTimeout =
        SandboxStartupResumePolicy.DefaultResumeTimeout;

    /// <summary>
    /// Hard ceiling for startup resume/adoption waits. The operator-facing
    /// values are configurable, but startup recovery must stay bounded.
    /// </summary>
    public static readonly TimeSpan MaximumResumeTimeout =
        SandboxStartupResumePolicy.MaximumResumeTimeout;
    public static readonly TimeSpan MaximumAdoptionDeadline =
        SandboxStartupResumePolicy.MaximumAdoptionDeadline;

    private readonly ISandboxProvider? _provider;
    private readonly IWorkItemStore _store;
    private readonly ILogger<SandboxResumeOnStartupService> _log;
    private readonly Func<SandboxStartupResumeOptions> _optionsAccessor;
    private readonly IStartupRecoveryInputSink _startupRecovery;
    private readonly IInfrastructureDeferralScheduler? _infrastructureDeferrals;
    private readonly Lock _resumeStartGate = new();
    private CancellationTokenSource? _backgroundCts;
    private Task? _resumeTask;
    private bool _resumeStarted;

    public SandboxResumeOnStartupService(
        ISandboxProvider? provider,
        IWorkItemStore store,
        ILogger<SandboxResumeOnStartupService> log,
        IStartupRecoveryInputSink recoveryInput,
        int? maxParallel = null,
        TimeSpan? adoptionDeadline = null,
        TimeSpan? resumeTimeout = null,
        SandboxStartupResumeMode? mode = null,
        IInfrastructureDeferralScheduler? infrastructureDeferrals = null)
        : this(
            provider,
            store,
            log,
            () => new SandboxStartupResumeOptions
            {
                MaxParallelResumes = maxParallel is > 0 ? maxParallel.Value : DefaultMaxParallelResumes,
                AdoptionDeadline = adoptionDeadline is { } d && d > TimeSpan.Zero
                    ? d
                    : DefaultAdoptionDeadline,
                ResumeTimeout = resumeTimeout is { } r && r > TimeSpan.Zero
                    ? r
                    : DefaultResumeTimeout,
                Mode = mode ?? SandboxStartupResumeMode.Background,
            },
            recoveryInput,
            infrastructureDeferrals)
    {
    }

    public SandboxResumeOnStartupService(
        ISandboxProvider? provider,
        IWorkItemStore store,
        ILogger<SandboxResumeOnStartupService> log,
        Func<SandboxStartupResumeOptions> optionsAccessor,
        IStartupRecoveryInputSink recoveryInput,
        IInfrastructureDeferralScheduler? infrastructureDeferrals = null)
    {
        ArgumentNullException.ThrowIfNull(recoveryInput);
        _provider = provider;
        _store = store;
        _log = log;
        _optionsAccessor = optionsAccessor;
        _startupRecovery = recoveryInput;
        _infrastructureDeferrals = infrastructureDeferrals;
    }

    public Task StartAsync(CancellationToken ct)
    {
        var mode = CurrentOptions().Mode;
        if (mode == SandboxStartupResumeMode.Background)
            return StartResumeOnceAsync(background: true, ct);

        // If configuration reloads from Background to Blocking between
        // StartingAsync and StartAsync, honor the latest value by running the
        // one-shot sweep here. The _resumeStartGate lock + _resumeStarted bool
        // in StartResumeOnceAsync keep the normal Blocking path from executing
        // twice.
        return StartResumeOnceAsync(background: false, ct);
    }

    public Task StopAsync(CancellationToken ct) =>
        HostedLifecycleTask.StopAsync(
            () =>
            {
                lock (_resumeStartGate)
                    return (_backgroundCts, _resumeTask);
            },
            expected =>
            {
                lock (_resumeStartGate)
                {
                    if (!ReferenceEquals(_backgroundCts, expected))
                        return null;

                    var dispose = _backgroundCts;
                    _backgroundCts = null;
                    return dispose;
                }
            },
            ct);

    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken ct)
    {
        var mode = CurrentOptions().Mode;
        if (mode != SandboxStartupResumeMode.Blocking)
            return Task.CompletedTask;

        return StartResumeOnceAsync(background: false, ct);
    }

    /// <summary>
    /// Exposed for test fixtures that drive the resume cycle directly without
    /// spinning the full host lifecycle.
    /// </summary>
    internal Task ResumeAllForTestAsync(CancellationToken ct) => ResumeAllAsync(ct);

    private Task StartResumeOnceAsync(bool background, CancellationToken ct)
    {
        lock (_resumeStartGate)
        {
            if (_resumeStarted)
                return background ? Task.CompletedTask : _resumeTask ?? Task.CompletedTask;

            if (background && ct.IsCancellationRequested)
                return Task.FromCanceled(ct);

            _resumeStarted = true;
            if (background)
            {
                var backgroundCts = new CancellationTokenSource();
                _backgroundCts = backgroundCts;
                _resumeTask = RunLongRunningAsync(() => ResumeAllAndSignalAsync(backgroundCts.Token));
                return Task.CompletedTask;
            }

            _resumeTask = RunLongRunningAsync(() => ResumeAllAndSignalAsync(ct));
            return _resumeTask;
        }
    }

    private async Task ResumeAllAndSignalAsync(CancellationToken ct)
    {
        try
        {
            await ResumeAllAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("Startup resume cancelled before all suspended sandboxes were processed");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup resume sweep threw; continuing host startup");
        }
        finally
        {
            _startupRecovery.MarkRecoveryInputReady();
        }
    }

    private SandboxStartupResumeOptions CurrentOptions()
    {
        var raw = _optionsAccessor();
        var maxParallel = raw.MaxParallelResumes > 0
            ? raw.MaxParallelResumes
            : DefaultMaxParallelResumes;
        var resumeTimeout = raw.ResumeTimeout > TimeSpan.Zero
            ? Cap(raw.ResumeTimeout, MaximumResumeTimeout)
            : DefaultResumeTimeout;
        var adoptionDeadline = raw.AdoptionDeadline > TimeSpan.Zero
            ? Cap(raw.AdoptionDeadline, MaximumAdoptionDeadline)
            : DefaultAdoptionDeadline;
        var mode = Enum.IsDefined(raw.Mode)
            ? raw.Mode
            : SandboxStartupResumeMode.Background;

        return raw with
        {
            MaxParallelResumes = maxParallel,
            ResumeTimeout = resumeTimeout,
            AdoptionDeadline = adoptionDeadline,
            Mode = mode,
        };
    }

    private static TimeSpan Cap(TimeSpan value, TimeSpan maximum)
        => value <= maximum ? value : maximum;

    internal async Task ResumeAllAsync(CancellationToken ct)
    {
        if (_provider is not ISuspendingSandboxProvider suspending)
        {
            // process / bubblewrap providers — nothing to resume.
            return;
        }

        var suspended = new List<WorkItem>();
        await foreach (var item in _store.ListSuspendedAsync(ct))
            suspended.Add(item);

        if (suspended.Count == 0)
            return;

        _log.LogInformation("Startup resume: {Count} suspended sandbox(es) to start", suspended.Count);

        var options = CurrentOptions();
        using var gate = new SemaphoreSlim(options.MaxParallelResumes, options.MaxParallelResumes);
        var tasks = new List<Task>(suspended.Count);
        try
        {
            foreach (var item in suspended)
            {
                await gate.WaitAsync(ct);
                tasks.Add(ResumeOneWithGateAsync(item));
            }
        }
        finally
        {
            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        async Task ResumeOneWithGateAsync(WorkItem item)
        {
            try
            {
                await ResumeOneAsync(suspending, item, ct);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task ResumeOneAsync(ISuspendingSandboxProvider suspending, WorkItem item, CancellationToken ct)
    {
        var vmName = item.SuspendedVmName!;
        var agentLogPath = item.AgentLogPath;
        var (resumeSucceeded, resumeError, provisioningDeferred) =
            await TryResumeSandboxAsync(suspending, item.Id, vmName, ct);
        int? adoptionExitCode = null;
        var adopted = false;

        if (provisioningDeferred is not null)
        {
            await DeferProvisioningResumeAsync(item, vmName, provisioningDeferred, ct);
            return;
        }

        if (resumeSucceeded && !string.IsNullOrWhiteSpace(agentLogPath))
        {
            // Adopt: re-tail the in-VM agent log file until the wrapper's
            // .exit marker appears (or the deadline elapses). Streaming what
            // the agent emits post-resume to the orchestrator log captures
            // output the host stream lost when the previous process exited.
            adoptionExitCode = await TryWaitForAdoptionAsync(
                suspending, item.Id, vmName, agentLogPath!, ct);
            adopted = adoptionExitCode is not null;
        }

        // Promote whatever the adopted agent committed inside the VM into a
        // real PreemptCheckpoint git ref so DeadWorkerReaper.RecoverWorkItemAsync
        // sees a non-null checkpoint and re-enqueues the item for clean resume
        // instead of marking it Failed for "Working without a preempt checkpoint"
        // (the happy-path failure mode of the suspend/resume cycle before R8-core
        // wired the checkpoint promotion). Only attempt when the resumed VM is
        // actually live and the agent has exited cleanly — a non-zero exit, a
        // missing exit code (deadline elapsed) or a resume failure all leave
        // the in-VM state untrustworthy, so we fall through to the standard
        // stranded-item recovery path which re-runs the iteration.
        string? promotedCheckpointRef = null;
        if (resumeSucceeded && adopted && adoptionExitCode == 0)
        {
            var refName = PreemptCheckpointRefFor(item.Id);
            if (await TryPromoteCheckpointAsync(suspending, item.Id, vmName, refName, ct))
                promotedCheckpointRef = refName;
        }

        // Clear the suspended-bookkeeping AFTER the adoption + promotion
        // attempts complete (or after the resume itself failed). This way the
        // leak reaper keeps the suspended VM exempt for the entire window we
        // were waiting for it to come back — see
        // SandboxLeakReaper.BuildSuspendedVmNameSetAsync. When promotion
        // succeeded we ALSO persist PreemptCheckpoint so the next pass of
        // DeadWorkerReaper.SweepStrandedItemsAsync re-enqueues the item via
        // the with-checkpoint branch instead of marking it Failed.
        var fresh = await _store.GetAsync(item.Id, ct);
        if (fresh is null)
        {
            AuditLog.SandboxResumedOnStartup(item.Id, vmName, resumeSucceeded, resumeError);
            return;
        }
        var updatedItem = fresh with
        {
            SuspendedVmName = null,
            SuspendedAt = null,
            AgentLogPath = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        if (!resumeSucceeded
            && WorkItemRecoveryPolicy.TryBuildWorkingWithoutPreemptFailure(
                updatedItem,
                $"startup resume failed for sandbox {vmName}: {resumeError ?? "unknown error"}",
                out var failedItem))
        {
            updatedItem = failedItem;
            _log.LogWarning(
                "Startup resume marked work item {WorkItemId} Failed after sandbox {VmName} could not be resumed: {Error}",
                item.Id, vmName, resumeError ?? "unknown error");
        }
        if (promotedCheckpointRef is not null)
        {
            updatedItem = updatedItem with
            {
                PreemptCheckpoint = promotedCheckpointRef,
                PreemptedAt = DateTimeOffset.UtcNow,
            };
        }
        await _store.UpdateAsync(updatedItem, ct);
        AuditLog.SandboxResumedOnStartup(item.Id, vmName, resumeSucceeded, resumeError, adopted, adoptionExitCode);
    }

    private async Task<(bool Succeeded, string? Error, SandboxProvisioningDeferredException? ProvisioningDeferred)> TryResumeSandboxAsync(
        ISuspendingSandboxProvider suspending,
        WorkItemId itemId,
        string vmName,
        CancellationToken ct)
    {
        var timeout = CurrentOptions().ResumeTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task? resumeTask = null;
        try
        {
            resumeTask = RunLongRunningAsync(
                () => suspending.ResumeSandboxAsync(vmName, timeoutCts.Token));
            if (UseBlockingResumeTimeoutWait(timeout))
            {
                await WaitForTaskOrTimeoutAsync(resumeTask, timeout, ct);
            }
            else
            {
                timeoutCts.CancelAfter(timeout);
                await resumeTask.WaitAsync(timeout, ct);
            }
            return (true, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            timeoutCts.Cancel();
            if (resumeTask is not null)
                ObserveProviderTaskException(resumeTask);
            throw;
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            if (resumeTask is not null)
                await ObserveProviderTaskAfterCancellationAsync(resumeTask);
            var error = $"timed out after {timeout}";
            _log.LogWarning(
                "Startup resume timed out for sandbox {VmName} (work item {WorkItemId}) after {Timeout}; clearing suspend bookkeeping so the item can recover via the stranded-item path",
                vmName, itemId, timeout);
            return (false, error, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            if (resumeTask is not null)
                await ObserveProviderTaskAfterCancellationAsync(resumeTask);
            var error = $"timed out after {timeout}";
            _log.LogWarning(
                "Startup resume timed out for sandbox {VmName} (work item {WorkItemId}) after {Timeout}; clearing suspend bookkeeping so the item can recover via the stranded-item path",
                vmName, itemId, timeout);
            return (false, error, null);
        }
        catch (OperationCanceledException ex)
        {
            var error = string.IsNullOrWhiteSpace(ex.Message) ? "provider cancelled resume" : ex.Message;
            _log.LogWarning(ex,
                "Startup resume was cancelled by the provider for sandbox {VmName} (work item {WorkItemId}); clearing suspend bookkeeping so the item can recover via the stranded-item path",
                vmName, itemId);
            return (false, error, null);
        }
        catch (SandboxProvisioningDeferredException ex)
        {
            _log.LogWarning(ex,
                "Startup resume deferred for sandbox {VmName} (work item {WorkItemId}) after transient provisioning failure",
                vmName, itemId);
            return (false, ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Startup resume failed for sandbox {VmName} (work item {WorkItemId}); clearing suspend bookkeeping so the item can recover via the stranded-item path",
                vmName, itemId);
            return (false, ex.Message, null);
        }
    }

    private async Task DeferProvisioningResumeAsync(
        WorkItem item,
        string vmName,
        SandboxProvisioningDeferredException provEx,
        CancellationToken ct)
    {
        var fresh = await _store.GetAsync(item.Id, ct);
        if (fresh is null)
        {
            AuditLog.SandboxResumedOnStartup(item.Id, vmName, success: false, error: provEx.Message);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cleared = fresh with
        {
            SuspendedVmName = null,
            SuspendedAt = null,
            AgentLogPath = null,
            UpdatedAt = now,
        };
        var updated = WorkItemRecoveryPolicy.BuildInfrastructureDeferredResumeState(cleared, now)
            ?? cleared;

        await _store.UpdateAsync(updated, ct);
        AuditLog.SandboxProvisioningDeferred(
            updated.Id,
            provEx.Provider,
            provEx.Operation,
            provEx.ErrorClass,
            updated.State.ToString(),
            provEx.RecheckIn);
        AuditLog.SandboxResumedOnStartup(item.Id, vmName, success: false, error: provEx.Message);
        _infrastructureDeferrals?.ScheduleInfrastructureDeferredRequeue(item.Id, provEx.RecheckIn, ct);
        _log.LogWarning(
            "Startup resume deferred work item {WorkItemId} after sandbox {VmName} hit transient provisioning failure ({Provider}/{Operation}, {ErrorClass}); resumeState={ResumeState}",
            item.Id, vmName, provEx.Provider, provEx.Operation, provEx.ErrorClass, updated.State);
    }

    private async Task<int?> TryWaitForAdoptionAsync(
        ISuspendingSandboxProvider suspending,
        WorkItemId itemId,
        string vmName,
        string agentLogPath,
        CancellationToken ct)
    {
        var adoptionDeadline = CurrentOptions().AdoptionDeadline;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(adoptionDeadline);

        Task<int?> adoptionTask;
        try
        {
            adoptionTask = RunLongRunningAsync(
                () => suspending.WaitForAdoptedAgentCompletionAsync(
                    vmName,
                    agentLogPath,
                    chunk =>
                    {
                        if (!string.IsNullOrEmpty(chunk))
                            _log.LogInformation("[adopted {VmName}] {Chunk}", vmName, chunk.TrimEnd());
                    },
                    adoptionDeadline,
                    timeoutCts.Token));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            timeoutCts.Cancel();
            throw;
        }
        catch (OperationCanceledException ex)
        {
            var error = string.IsNullOrWhiteSpace(ex.Message) ? "provider cancelled adoption" : ex.Message;
            _log.LogWarning(ex,
                "Startup adoption was cancelled by the provider for sandbox {VmName} (work item {WorkItemId}): {Error}; falling through to recovery",
                vmName, itemId, error);
            return null;
        }
        catch (Exception adoptionEx) when (adoptionEx is not OperationCanceledException)
        {
            _log.LogWarning(adoptionEx,
                "Startup adoption errored for sandbox {VmName} (work item {WorkItemId}); falling through to recovery",
                vmName, itemId);
            return null;
        }

        try
        {
            var adoptionExitCode = await adoptionTask.WaitAsync(timeoutCts.Token);
            if (adoptionExitCode is null)
            {
                _log.LogWarning(
                    "Startup adoption deadline elapsed for sandbox {VmName} (work item {WorkItemId}); the resumed agent has not signalled completion within {Deadline}. The work item will recover via the stranded-item path.",
                    vmName, itemId, adoptionDeadline);
            }
            return adoptionExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            timeoutCts.Cancel();
            throw;
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            ObserveProviderTaskException(adoptionTask);
            _log.LogWarning(
                "Startup adoption timed out for sandbox {VmName} (work item {WorkItemId}) after {Deadline}; falling through to recovery",
                vmName, itemId, adoptionDeadline);
            return null;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            ObserveProviderTaskException(adoptionTask);
            _log.LogWarning(
                "Startup adoption timed out for sandbox {VmName} (work item {WorkItemId}) after {Deadline}; falling through to recovery",
                vmName, itemId, adoptionDeadline);
            return null;
        }
        catch (OperationCanceledException ex)
        {
            var error = string.IsNullOrWhiteSpace(ex.Message) ? "provider cancelled adoption" : ex.Message;
            _log.LogWarning(ex,
                "Startup adoption was cancelled by the provider for sandbox {VmName} (work item {WorkItemId}): {Error}; falling through to recovery",
                vmName, itemId, error);
            return null;
        }
        catch (Exception adoptionEx) when (adoptionEx is not OperationCanceledException)
        {
            _log.LogWarning(adoptionEx,
                "Startup adoption errored for sandbox {VmName} (work item {WorkItemId}); falling through to recovery",
                vmName, itemId);
            return null;
        }
    }

    private async Task<bool> TryPromoteCheckpointAsync(
        ISuspendingSandboxProvider suspending,
        WorkItemId itemId,
        string vmName,
        string refName,
        CancellationToken ct)
    {
        var timeout = CurrentOptions().ResumeTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        Task<bool>? promoteTask = null;
        try
        {
            promoteTask = suspending.PushSuspendedVmCheckpointRefAsync(
                vmName,
                SandboxConventions.WorkDir,
                refName,
                $"codeybox: suspend-resume checkpoint {itemId}",
                timeoutCts.Token);
            var pushed = await promoteTask.WaitAsync(timeout, ct);
            if (pushed)
                return true;

            _log.LogWarning(
                "Failed to promote adopted-VM HEAD for sandbox {VmName} to preempt-checkpoint {RefName} for work item {WorkItemId}; falling through to stranded-item recovery (item will be marked Failed unless it has an earlier checkpoint)",
                vmName, refName, itemId);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            timeoutCts.Cancel();
            if (promoteTask is not null)
                ObserveProviderTaskException(promoteTask);
            throw;
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            if (promoteTask is not null)
                await ObserveProviderTaskAfterCancellationAsync(promoteTask);

            _log.LogWarning(
                "Startup checkpoint promotion timed out for sandbox {VmName} (work item {WorkItemId}) after {Timeout}; falling through to stranded-item recovery",
                vmName, itemId, timeout);
            return false;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            if (promoteTask is not null)
                await ObserveProviderTaskAfterCancellationAsync(promoteTask);

            _log.LogWarning(
                "Startup checkpoint promotion timed out for sandbox {VmName} (work item {WorkItemId}) after {Timeout}; falling through to stranded-item recovery",
                vmName, itemId, timeout);
            return false;
        }
        catch (OperationCanceledException promoteEx)
        {
            _log.LogWarning(promoteEx,
                "Startup checkpoint promotion was cancelled by the provider for sandbox {VmName} (work item {WorkItemId}); falling through to stranded-item recovery",
                vmName, itemId);
            return false;
        }
        catch (Exception promoteEx)
        {
            _log.LogWarning(promoteEx,
                "Promote of adopted-VM HEAD for sandbox {VmName} to preempt-checkpoint {RefName} threw for work item {WorkItemId}; falling through to stranded-item recovery",
                vmName, refName, itemId);
            return false;
        }
    }

    private static void ObserveProviderTaskException(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task ObserveProviderTaskAfterCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        catch (TimeoutException)
        {
            ObserveProviderTaskException(task);
        }
        catch (OperationCanceledException)
        {
            _log.LogDebug("Provider task observed cancellation after startup resume timeout/cancellation");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Provider task faulted after startup resume timeout/cancellation");
        }
    }

    private static bool UseBlockingResumeTimeoutWait(TimeSpan timeout) =>
        timeout <= TimeSpan.FromSeconds(5);

    private static async Task WaitForTaskOrTimeoutAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        if (task.IsCompleted)
        {
            await task.ConfigureAwait(false);
            return;
        }

        using var completed = new ManualResetEventSlim(false);
        using var cancellationRegistration = ct.Register(
            static state => ((ManualResetEventSlim)state!).Set(),
            completed);
        _ = task.ContinueWith(
            static (_, state) =>
            {
                try
                {
                    ((ManualResetEventSlim)state!).Set();
                }
                catch (ObjectDisposedException)
                {
                    // The timeout path already moved on; the provider task is
                    // observed separately so late completion is harmless.
                }
            },
            completed,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (!completed.Wait(timeout))
            throw new TimeoutException();
        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);

        await task.ConfigureAwait(false);
    }

    private static Task RunLongRunningAsync(Func<Task> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    private static Task<T> RunLongRunningAsync<T>(Func<Task<T>> work) =>
        Task.Factory.StartNew(
            work,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

    /// <summary>
    /// Must stay in lockstep with <c>PipelineRunner.PreemptRefFor</c>: the
    /// resumable agent runner only accepts checkpoint refs that match the
    /// expected per-work-item shape (see <c>PipelineRunner.ValidatePreemptCheckpoint</c>).
    /// </summary>
    internal static string PreemptCheckpointRefFor(WorkItemId id)
        => $"refs/heads/codeybox/preempt/{id}";
}

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

public interface IStartupSandboxResumeBarrier
{
    Task Completion { get; }
}

public sealed class StartupSandboxResumeBarrier : IStartupSandboxResumeBarrier
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    internal void MarkCompleted() => _completion.TrySetResult();
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
/// (sibling of <see cref="SandboxSuspendOnShutdownService.StoppingAsync"/>)
/// only when configured for blocking mode. The default background mode starts
/// the resume sweep from <see cref="StartAsync"/> and signals
/// <see cref="StartupSandboxResumeBarrier"/> when done, so the HTTP listener can
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
    /// <see cref="SandboxSuspendOnShutdownService.DefaultMaxParallelSuspends"/>
    /// so the resume side cannot flood multipassd worse than the suspend side
    /// already did at shutdown time.
    /// </summary>
    public const int DefaultMaxParallelResumes = SandboxSuspendOnShutdownService.DefaultMaxParallelSuspends;

    /// <summary>
    /// Default upper bound on how long we wait for an adopted in-VM agent
    /// process to finish post-resume. Long enough that a real LLM call can
    /// finish (typical work-phase agent invocations are minutes to tens of
    /// minutes), short enough that a wedged agent does not block the
    /// orchestrator boot indefinitely. Configurable via constructor.
    /// </summary>
    public static readonly TimeSpan DefaultAdoptionDeadline = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Default caller-side cap for a single persisted VM resume. The Multipass
    /// provider has its own launch/readiness limits, but the orchestrator also
    /// needs an outer guard for daemon/provider calls that ignore cancellation.
    /// </summary>
    public static readonly TimeSpan DefaultResumeTimeout = SuspendTimeoutPolicy.DefaultFloor;

    private readonly ISandboxProvider? _provider;
    private readonly IWorkItemStore _store;
    private readonly ILogger<SandboxResumeOnStartupService> _log;
    private readonly Func<SandboxStartupResumeOptions> _optionsAccessor;
    private readonly StartupSandboxResumeBarrier _barrier;
    private CancellationTokenSource? _backgroundCts;
    private Task? _resumeTask;
    private int _resumeStarted;

    public SandboxResumeOnStartupService(
        ISandboxProvider? provider,
        IWorkItemStore store,
        ILogger<SandboxResumeOnStartupService> log,
        int? maxParallel = null,
        TimeSpan? adoptionDeadline = null,
        TimeSpan? resumeTimeout = null,
        SandboxStartupResumeMode? mode = null,
        StartupSandboxResumeBarrier? barrier = null)
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
            barrier)
    {
    }

    public SandboxResumeOnStartupService(
        ISandboxProvider? provider,
        IWorkItemStore store,
        ILogger<SandboxResumeOnStartupService> log,
        Func<SandboxStartupResumeOptions> optionsAccessor,
        StartupSandboxResumeBarrier? barrier = null)
    {
        _provider = provider;
        _store = store;
        _log = log;
        _optionsAccessor = optionsAccessor;
        _barrier = barrier ?? new StartupSandboxResumeBarrier();
    }

    public Task StartAsync(CancellationToken ct)
    {
        var options = CurrentOptions();
        if (options.Mode != SandboxStartupResumeMode.Background)
            return Task.CompletedTask;

        return StartResumeOnceAsync(background: true, ct);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _backgroundCts?.Cancel();
        var task = _resumeTask;
        if (task is null)
            return;

        try { await task.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken ct)
    {
        var options = CurrentOptions();
        if (options.Mode != SandboxStartupResumeMode.Blocking)
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
        if (Interlocked.Exchange(ref _resumeStarted, 1) != 0)
            return background ? Task.CompletedTask : _resumeTask ?? Task.CompletedTask;

        if (background)
        {
            _backgroundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _resumeTask = Task.Run(
                () => ResumeAllAndSignalAsync(_backgroundCts.Token),
                CancellationToken.None);
            return Task.CompletedTask;
        }

        _resumeTask = ResumeAllAndSignalAsync(ct);
        return _resumeTask;
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
            _barrier.MarkCompleted();
        }
    }

    private SandboxStartupResumeOptions CurrentOptions()
    {
        var raw = _optionsAccessor();
        var maxParallel = raw.MaxParallelResumes > 0
            ? raw.MaxParallelResumes
            : DefaultMaxParallelResumes;
        var resumeTimeout = raw.ResumeTimeout > TimeSpan.Zero
            ? raw.ResumeTimeout
            : DefaultResumeTimeout;
        var adoptionDeadline = raw.AdoptionDeadline > TimeSpan.Zero
            ? raw.AdoptionDeadline
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
        foreach (var item in suspended)
        {
            await gate.WaitAsync(ct);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ResumeOneAsync(suspending, item, ct);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }
        await Task.WhenAll(tasks);
    }

    private async Task ResumeOneAsync(ISuspendingSandboxProvider suspending, WorkItem item, CancellationToken ct)
    {
        var vmName = item.SuspendedVmName!;
        var agentLogPath = item.AgentLogPath;
        var (resumeSucceeded, resumeError) = await TryResumeSandboxAsync(suspending, item.Id, vmName, ct);
        int? adoptionExitCode = null;
        var adopted = false;

        if (resumeSucceeded && !string.IsNullOrWhiteSpace(agentLogPath))
        {
            // Adopt: re-tail the in-VM agent log file until the wrapper's
            // .exit marker appears (or the deadline elapses). Streaming what
            // the agent emits post-resume to the orchestrator log captures
            // output the host stream lost when the previous process exited.
            try
            {
                var adoptionDeadline = CurrentOptions().AdoptionDeadline;
                adoptionExitCode = await suspending.WaitForAdoptedAgentCompletionAsync(
                    vmName,
                    agentLogPath!,
                    chunk =>
                    {
                        if (!string.IsNullOrEmpty(chunk))
                            _log.LogInformation("[adopted {VmName}] {Chunk}", vmName, chunk.TrimEnd());
                    },
                    adoptionDeadline,
                    ct);
                adopted = adoptionExitCode is not null;
                if (!adopted)
                {
                    _log.LogWarning(
                        "Startup adoption deadline elapsed for sandbox {VmName} (work item {WorkItemId}); the resumed agent has not signalled completion within {Deadline}. The work item will recover via the stranded-item path.",
                        vmName, item.Id, adoptionDeadline);
                }
            }
            catch (Exception adoptionEx) when (adoptionEx is not OperationCanceledException)
            {
                _log.LogWarning(adoptionEx,
                    "Startup adoption errored for sandbox {VmName} (work item {WorkItemId}); falling through to recovery",
                    vmName, item.Id);
            }
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
            try
            {
                var pushed = await suspending.PushSuspendedVmCheckpointRefAsync(
                    vmName,
                    SandboxConventions.WorkDir,
                    refName,
                    $"codeybox: suspend-resume checkpoint {item.Id}",
                    ct);
                if (pushed)
                {
                    promotedCheckpointRef = refName;
                }
                else
                {
                    _log.LogWarning(
                        "Failed to promote adopted-VM HEAD to preempt-checkpoint {RefName} for work item {WorkItemId}; falling through to stranded-item recovery (item will be marked Failed unless it has an earlier checkpoint)",
                        refName, item.Id);
                }
            }
            catch (Exception promoteEx) when (promoteEx is not OperationCanceledException)
            {
                _log.LogWarning(promoteEx,
                    "Promote of adopted-VM HEAD to preempt-checkpoint {RefName} threw for work item {WorkItemId}; falling through to stranded-item recovery",
                    refName, item.Id);
            }
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
            && fresh.State == WorkItemState.Working
            && string.IsNullOrWhiteSpace(fresh.PreemptCheckpoint))
        {
            updatedItem = updatedItem with
            {
                State = WorkItemState.Failed,
                LastError = $"startup resume failed for sandbox {vmName}: {resumeError ?? "unknown error"}",
                RecoveryAttempts = fresh.RecoveryAttempts + 1,
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
            };
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

    private async Task<(bool Succeeded, string? Error)> TryResumeSandboxAsync(
        ISuspendingSandboxProvider suspending,
        WorkItemId itemId,
        string vmName,
        CancellationToken ct)
    {
        var timeout = CurrentOptions().ResumeTimeout;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var resumeTask = Task.Run(async () =>
        {
            await suspending.ResumeSandboxAsync(vmName, timeoutCts.Token);
        }, CancellationToken.None);

        try
        {
            await resumeTask.WaitAsync(timeout, ct);
            return (true, null);
        }
        catch (TimeoutException)
        {
            timeoutCts.Cancel();
            ObserveTimedOutResumeTask(resumeTask, itemId, vmName);
            var error = $"timed out after {timeout}";
            _log.LogWarning(
                "Startup resume timed out for sandbox {VmName} (work item {WorkItemId}) after {Timeout}; clearing suspend bookkeeping so the item can recover via the stranded-item path",
                vmName, itemId, timeout);
            return (false, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            var error = $"timed out after {timeout}";
            _log.LogWarning(
                "Startup resume timed out for sandbox {VmName} (work item {WorkItemId}) after {Timeout}; clearing suspend bookkeeping so the item can recover via the stranded-item path",
                vmName, itemId, timeout);
            return (false, error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex,
                "Startup resume failed for sandbox {VmName} (work item {WorkItemId}); clearing suspend bookkeeping so the item can recover via the stranded-item path",
                vmName, itemId);
            return (false, ex.Message);
        }
    }

    private void ObserveTimedOutResumeTask(Task resumeTask, WorkItemId itemId, string vmName)
    {
        if (resumeTask.IsCompleted)
            return;

        resumeTask.ContinueWith(
            t => _log.LogWarning(
                t.Exception,
                "Timed-out startup resume task later faulted for sandbox {VmName} (work item {WorkItemId})",
                vmName,
                itemId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Must stay in lockstep with <c>PipelineRunner.PreemptRefFor</c>: the
    /// resumable agent runner only accepts checkpoint refs that match the
    /// expected per-work-item shape (see <c>PipelineRunner.ValidatePreemptCheckpoint</c>).
    /// </summary>
    internal static string PreemptCheckpointRefFor(WorkItemId id)
        => $"refs/heads/codeybox/preempt/{id}";
}

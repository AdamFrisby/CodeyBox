using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

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
/// so it runs BEFORE <see cref="OrchestratorService.ExecuteAsync"/> kicks
/// off the dispatch loop, the dead-worker reaper, and
/// <c>ReplayPendingAsync</c>. Sequencing matters: the leak reaper sees a
/// consistent picture (the VM is back to Running, the work item still
/// carries SuspendedVmName until adoption completes) and recovery doesn't
/// race the resume.</para>
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

    private readonly ISandboxProvider? _provider;
    private readonly IWorkItemStore _store;
    private readonly ILogger<SandboxResumeOnStartupService> _log;
    private readonly int _maxParallel;
    private readonly TimeSpan _adoptionDeadline;

    public SandboxResumeOnStartupService(
        ISandboxProvider? provider,
        IWorkItemStore store,
        ILogger<SandboxResumeOnStartupService> log,
        int? maxParallel = null,
        TimeSpan? adoptionDeadline = null)
    {
        _provider = provider;
        _store = store;
        _log = log;
        _maxParallel = maxParallel is > 0 ? maxParallel.Value : DefaultMaxParallelResumes;
        _adoptionDeadline = adoptionDeadline is { } d && d > TimeSpan.Zero
            ? d
            : DefaultAdoptionDeadline;
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken ct) => ResumeAllAsync(ct);

    /// <summary>
    /// Exposed for test fixtures that drive the resume cycle directly without
    /// spinning the full host lifecycle.
    /// </summary>
    internal Task ResumeAllForTestAsync(CancellationToken ct) => ResumeAllAsync(ct);

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

        using var gate = new SemaphoreSlim(_maxParallel, _maxParallel);
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
        var resumeSucceeded = true;
        string? resumeError = null;
        int? adoptionExitCode = null;
        var adopted = false;
        try
        {
            await suspending.ResumeSandboxAsync(vmName, ct);
        }
        catch (Exception ex)
        {
            resumeSucceeded = false;
            resumeError = ex.Message;
            _log.LogWarning(ex,
                "Startup resume failed for sandbox {VmName} (work item {WorkItemId}); clearing suspend bookkeeping so the item can recover via the stranded-item path",
                vmName, item.Id);
        }

        if (resumeSucceeded && !string.IsNullOrWhiteSpace(agentLogPath))
        {
            // Adopt: re-tail the in-VM agent log file until the wrapper's
            // .exit marker appears (or the deadline elapses). Streaming what
            // the agent emits post-resume to the orchestrator log captures
            // output the host stream lost when the previous process exited.
            try
            {
                adoptionExitCode = await suspending.WaitForAdoptedAgentCompletionAsync(
                    vmName,
                    agentLogPath!,
                    chunk =>
                    {
                        if (!string.IsNullOrEmpty(chunk))
                            _log.LogInformation("[adopted {VmName}] {Chunk}", vmName, chunk.TrimEnd());
                    },
                    _adoptionDeadline,
                    ct);
                adopted = adoptionExitCode is not null;
                if (!adopted)
                {
                    _log.LogWarning(
                        "Startup adoption deadline elapsed for sandbox {VmName} (work item {WorkItemId}); the resumed agent has not signalled completion within {Deadline}. The work item will recover via the stranded-item path.",
                        vmName, item.Id, _adoptionDeadline);
                }
            }
            catch (Exception adoptionEx) when (adoptionEx is not OperationCanceledException)
            {
                _log.LogWarning(adoptionEx,
                    "Startup adoption errored for sandbox {VmName} (work item {WorkItemId}); falling through to recovery",
                    vmName, item.Id);
            }
        }

        // Clear the suspended-bookkeeping AFTER the adoption attempt completes
        // (or after the resume itself failed). This way the leak reaper keeps
        // the suspended VM exempt for the entire window we were waiting for it
        // to come back — see SandboxLeakReaper.BuildSuspendedVmNameSetAsync.
        //
        // The work item's pipeline state is intentionally left untouched here:
        // the stranded-item recovery path (DeadWorkerReaper.SweepStrandedItemsAsync
        // + OrchestratorService.ReplayPendingAsync) takes the item forward
        // from this point. When adoption succeeded, the resumed agent has
        // already produced its commits inside the resumed VM and the
        // PreemptCheckpoint mechanism captures them; when adoption failed,
        // recovery falls back to re-running the iteration.
        var fresh = await _store.GetAsync(item.Id, ct);
        if (fresh is null)
        {
            AuditLog.SandboxResumedOnStartup(item.Id, vmName, resumeSucceeded, resumeError);
            return;
        }
        await _store.UpdateAsync(fresh with
        {
            SuspendedVmName = null,
            SuspendedAt = null,
            AgentLogPath = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, ct);
        AuditLog.SandboxResumedOnStartup(item.Id, vmName, resumeSucceeded, resumeError, adopted, adoptionExitCode);
    }
}

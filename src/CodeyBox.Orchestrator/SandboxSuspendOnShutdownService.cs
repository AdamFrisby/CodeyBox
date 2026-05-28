using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// R8-core: on graceful host shutdown, freeze every in-flight sandbox via
/// <see cref="ISuspendableSandbox.SuspendAsync"/> and persist the
/// <c>(workItemId → vmName, suspendedAt, agentLogPath)</c> mapping so the next
/// orchestrator process can <c>multipass start</c> the same VM and re-tail the
/// in-VM agent log (see <see cref="SandboxResumeOnStartupService"/> for the
/// resume half).
///
/// <para>This sits alongside — not on top of — the existing per-phase
/// preempt-checkpoint flow in <see cref="PipelineRunner"/>: that path commits a
/// git ref the pipeline can resume from, and is still the safety net when the
/// sandbox provider does not support suspend (process, bubblewrap). The
/// suspend path is strictly an improvement when both are available.</para>
///
/// <para>Implements <see cref="IHostedLifecycleService.StoppingAsync"/> rather
/// than registering a synchronous callback on
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/>. StoppingAsync
/// is awaited by the host before the BackgroundService cancellation token
/// fires, so the in-VM agent process is still running when multipass takes
/// its snapshot, AND the host honours the async signature instead of being
/// blocked on a sync-over-async fan-out.</para>
/// </summary>
public sealed class SandboxSuspendOnShutdownService : IHostedLifecycleService
{
    /// <summary>
    /// Cap on parallel <c>multipass suspend</c> calls. Suspend writes the VM's
    /// RAM to disk; running too many in parallel just contends on disk IO and
    /// stretches the SIGTERM-to-exit window. 8 matches the design spec.
    /// </summary>
    public const int DefaultMaxParallelSuspends = 8;

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
    /// total shutdown time. <c>Program.cs</c> raises that ceiling to cover the
    /// largest scaled suspend budget when the multipass provider is selected, so a
    /// healthy snapshot is not truncated. And because the (work item → VM) mapping
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

    private readonly ISandboxProvider _provider;
    private readonly IWorkItemStore _store;
    private readonly ILogger<SandboxSuspendOnShutdownService> _log;
    private readonly int _maxParallel;
    private readonly TimeSpan _perSuspendTimeout;
    private readonly TimeSpan _perGiBSuspendBudget;

    public SandboxSuspendOnShutdownService(
        ISandboxProvider provider,
        IWorkItemStore store,
        ILogger<SandboxSuspendOnShutdownService> log,
        int? maxParallel = null,
        TimeSpan? perSuspendTimeout = null,
        TimeSpan? perGiBSuspendBudget = null)
    {
        _provider = provider;
        _store = store;
        _log = log;
        _maxParallel = maxParallel is > 0 ? maxParallel.Value : DefaultMaxParallelSuspends;
        _perSuspendTimeout = perSuspendTimeout is { } t && t > TimeSpan.Zero ? t : DefaultPerSuspendTimeout;
        _perGiBSuspendBudget = perGiBSuspendBudget is { } g && g > TimeSpan.Zero ? g : DefaultPerGiBSuspendBudget;
    }

    /// <summary>
    /// Effective per-VM suspend timeout: the floor (<see cref="_perSuspendTimeout"/>)
    /// scaled up by RAM size when the sandbox reports it. A bigger VM has more
    /// RAM to flush to disk, so a uniform cap either truncates large VMs or wastes
    /// shutdown time waiting on small ones.
    /// </summary>
    internal TimeSpan SuspendTimeoutFor(ISuspendableSandbox sandbox) =>
        SuspendTimeoutPolicy.For(sandbox.MemoryBytes, _perSuspendTimeout, _perGiBSuspendBudget);

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
            // We do NOT thread the host shutdown token into the multipass suspend
            // calls — each suspend gets its own RAM-scaled per-VM timeout (see
            // SuspendTimeoutFor / SuspendOneAsync) so one stuck multipassd call
            // can't block the rest of the drain. The host still enforces
            // HostOptions.ShutdownTimeout overall; Program.cs sizes that ceiling to
            // cover the largest scaled suspend budget for the multipass provider so
            // a healthy snapshot is not truncated. If the host kills us before a
            // slow snapshot finishes, the (work item → VM) mapping persisted before
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
        if (_provider is not ISuspendingSandboxProvider suspending)
        {
            _log.LogDebug("Sandbox provider {Provider} does not support suspend; skipping suspend-on-shutdown sweep",
                _provider.Name);
            return;
        }

        var entries = suspending.SnapshotSuspendableActive();
        if (entries.Count == 0)
        {
            _log.LogInformation("Suspend-on-shutdown: no in-flight sandboxes to suspend");
            return;
        }

        _log.LogInformation("Suspend-on-shutdown: freezing {Count} in-flight sandbox(es) before exit", entries.Count);

        using var gate = new SemaphoreSlim(_maxParallel, _maxParallel);
        var tasks = new List<Task>(entries.Count);
        foreach (var (workItemId, sandbox) in entries)
        {
            await gate.WaitAsync();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await SuspendOneAsync(workItemId, sandbox);
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);
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

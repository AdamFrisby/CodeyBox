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
    /// Per-VM suspend timeout. Suspending a 4GB VM takes a few seconds; this
    /// cap exists so one stuck multipassd call cannot block the rest of the
    /// shutdown drain. The hosted service waits for the slower of (all
    /// suspends complete) or (cumulative ShutdownGrace expiry, applied by the
    /// host).
    /// </summary>
    public static readonly TimeSpan DefaultPerSuspendTimeout = TimeSpan.FromSeconds(30);

    private readonly ISandboxProvider _provider;
    private readonly IWorkItemStore _store;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SandboxSuspendOnShutdownService> _log;
    private readonly int _maxParallel;
    private readonly TimeSpan _perSuspendTimeout;

    public SandboxSuspendOnShutdownService(
        ISandboxProvider provider,
        IWorkItemStore store,
        IHostApplicationLifetime lifetime,
        ILogger<SandboxSuspendOnShutdownService> log,
        int? maxParallel = null,
        TimeSpan? perSuspendTimeout = null)
    {
        _provider = provider;
        _store = store;
        _lifetime = lifetime;
        _log = log;
        _maxParallel = maxParallel is > 0 ? maxParallel.Value : DefaultMaxParallelSuspends;
        _perSuspendTimeout = perSuspendTimeout is { } t && t > TimeSpan.Zero ? t : DefaultPerSuspendTimeout;
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
            // Suspend always uses CancellationToken.None for the multipass call
            // so the host's shutdown grace period doesn't truncate it mid-flight.
            // ct is honoured for the loop's early exit before we fan out.
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
        using var timeoutCts = new CancellationTokenSource(_perSuspendTimeout);
        try
        {
            await sandbox.SuspendAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning(
                "Suspend timed out after {Timeout} for work item {WorkItemId} sandbox {SandboxId}; the item will recover via the standard stranded-item path on next startup",
                _perSuspendTimeout, workItemId, sandbox.Id);
            return;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Suspend failed for work item {WorkItemId} sandbox {SandboxId}; the item will recover via the standard stranded-item path on next startup",
                workItemId, sandbox.Id);
            return;
        }

        // Persist (workItemId → vmName) so the startup handler can match a
        // resumed VM to the work item it belongs to. The DB write uses
        // CancellationToken.None: the host's shutdown drain might be racing,
        // and a SQLite UPDATE for a single row is fast enough to finish even
        // under shutdown pressure. Losing the row would leave the VM
        // suspended on disk with no orchestrator-side bookkeeping — the leak
        // reaper's PreemptRetention window keeps it around long enough for
        // operator inspection (multipass list shows it as Suspended).
        var item = await _store.GetAsync(workItemId, CancellationToken.None);
        if (item is null)
        {
            _log.LogWarning(
                "Suspended sandbox {SandboxId} for work item {WorkItemId}, but the item is no longer present in the store",
                sandbox.Id, workItemId);
            return;
        }
        var updated = item with
        {
            SuspendedVmName = sandbox.Id,
            SuspendedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _store.UpdateAsync(updated, CancellationToken.None);
        AuditLog.SandboxSuspendedOnShutdown(workItemId, sandbox.Id);
    }
}

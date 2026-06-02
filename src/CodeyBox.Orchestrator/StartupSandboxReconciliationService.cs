using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// R8.1 (incident 2026-05-29): on startup, sweep any managed sandboxes left in
/// a suspend-lifecycle (<c>Suspending</c>/<c>Suspended</c>) or transitional
/// state from a prior unclean shutdown and attempt to bring them back to a
/// clean state without blocking the HTTP listener from binding.
///
/// <para>Why early reconciliation matters: an orphaned <c>Suspending</c> VM
/// from a SIGKILLed prior process leaves a root-owned qemu process holding the
/// disk-image write-lock. <c>multipass delete --purge</c> (what the leak reaper
/// would eventually call) fails against that lock; the only known recovery is
/// a root <c>kill -9</c> of the qemu PID followed by purge. Recovering the
/// non-root-cleanable subset early via <c>multipass stop</c> (which releases
/// the lock cleanly when multipassd is responsive) prevents the leak path from
/// ever firing, and surfaces the genuinely unrecoverable cases as actionable
/// leak events instead of silent hangs.</para>
///
/// <para>Implemented as a background sweep from <see cref="StartAsync"/>. The
/// provider path may shell out to Multipass, so it must not run from
/// <see cref="IHostedLifecycleService.StartingAsync"/> where a wedged daemon
/// would keep Kestrel offline.</para>
/// </summary>
public sealed class StartupSandboxReconciliationService : IHostedLifecycleService
{
    private readonly ISandboxProvider? _provider;
    private readonly IWorkItemStore _store;
    private readonly ILogger<StartupSandboxReconciliationService> _log;
    private CancellationTokenSource? _backgroundCts;
    private Task? _reconcileTask;

    public StartupSandboxReconciliationService(
        ISandboxProvider? provider,
        IWorkItemStore store,
        ILogger<StartupSandboxReconciliationService> log)
    {
        _provider = provider;
        _store = store;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _backgroundCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _reconcileTask = Task.Run(
            () => ReconcileAllAsync(_backgroundCts.Token),
            CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _backgroundCts?.Cancel();
        var task = _reconcileTask;
        if (task is null)
            return;

        try { await task.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public Task StartedAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken ct) => Task.CompletedTask;

    public Task StartingAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Exposed for tests that drive the reconciler directly.</summary>
    internal Task ReconcileAllForTestAsync(CancellationToken ct) => ReconcileAllAsync(ct);

    internal async Task ReconcileAllAsync(CancellationToken ct)
    {
        if (_provider is not ISuspendingSandboxProvider suspending)
        {
            // Non-suspending providers (process / bubblewrap) have no
            // persistent-VM lifecycle to reconcile.
            return;
        }

        // Build the live SuspendedVmName set so the reconciler does NOT touch
        // VMs the resume handler is about to reattach. Same source of truth as
        // the leak reaper's BuildSuspendedVmNameSetAsync.
        var liveSuspended = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var item in _store.ListSuspendedAsync(ct))
        {
            if (string.IsNullOrWhiteSpace(item.SuspendedVmName)) continue;
            if (WorkItemDependencies.TerminalStates.Contains(item.State)) continue;
            liveSuspended.Add(item.SuspendedVmName!);
        }

        try
        {
            var unrecoverable = await suspending.ReconcileStuckSandboxesAsync(liveSuspended, ct);
            if (unrecoverable.Count == 0) return;

            _log.LogWarning(
                "Startup reconciler: {Count} orphaned sandbox(es) still wedged after recovery — operator intervention required: {Names}",
                unrecoverable.Count, string.Join(", ", unrecoverable));
            foreach (var name in unrecoverable)
                AuditLog.SandboxStartupReconcileFailed(name, "wedged after stop+purge recovery — root cleanup likely required");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup sandbox reconciliation threw; resume handler and leak reaper will handle the residual state");
        }
    }
}

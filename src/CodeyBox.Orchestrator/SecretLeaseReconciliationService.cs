using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Background sweep for secret-lease lifecycle. Each interval it revokes
/// leases whose work item is terminal — regardless of whether teardown
/// succeeded — and renews due leases of live items so a lease shorter than
/// the phase never silently expires mid-run. Lease state is re-read from
/// the durable store every sweep, so an orchestrator restart resumes
/// instead of orphaning live leases. Revocation failures stay loud: they
/// are error-logged with lease ids and left outstanding for the next sweep.
/// </summary>
public sealed class SecretLeaseReconciliationService : BackgroundService
{
    private readonly SecretLeaseManager _leases;
    private readonly ISecretLeaseStore _store;
    private readonly IWorkItemStore _workItems;
    private readonly Func<SecretLeasingOptions> _options;
    private readonly ILogger<SecretLeaseReconciliationService> _log;

    public SecretLeaseReconciliationService(
        SecretLeaseManager leases,
        ISecretLeaseStore store,
        IWorkItemStore workItems,
        Func<SecretLeasingOptions> options,
        ILogger<SecretLeaseReconciliationService> log)
    {
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _workItems = workItems ?? throw new ArgumentNullException(nameof(workItems));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = _options().SweepInterval;
                if (delay <= TimeSpan.Zero)
                    delay = new SecretLeasingOptions().SweepInterval;
                await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The sweep must never die: a failed pass retries next
                // interval. Lease ids are safe to log; values never are.
                _log.LogWarning(ex, "Secret lease reconciliation sweep failed; retrying on the next interval.");
                delay = _options().SweepInterval;
                if (delay <= TimeSpan.Zero)
                    delay = new SecretLeasingOptions().SweepInterval;
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Runs one reconcile pass. Public for tests: revokes terminal-item
    /// leases even when teardown failed, renews due live leases.
    /// </summary>
    public async Task<LeaseRevocationReport> SweepOnceAsync(CancellationToken ct = default)
    {
        // Fast path: no outstanding leases means no work-item reads.
        if ((await _store.ListOutstandingAsync(ct).ConfigureAwait(false)).Count == 0)
            return new LeaseRevocationReport();

        return await _leases.ReconcileAsync(
            async workItemId =>
            {
                var item = await _workItems.GetAsync(new WorkItemId(workItemId), ct).ConfigureAwait(false);
                return item?.State;
            },
            // The sweep does not know per-item deadlines; renewal is still
            // bounded by the absolute max lease lifetime, and terminal
            // revocation bounds it by the item's life.
            _ => (DateTimeOffset?)null,
            ct).ConfigureAwait(false);
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Daily background sweep that deletes <c>audit_reports</c> rows whose
/// <c>started_at</c> is older than the configured retention window.
/// Reuses the same <c>CodeyBox:AuditLog:RetainedDays</c> value used by the
/// rolling log files — no separate config knob.
/// </summary>
public sealed class AuditReportRetentionService : BackgroundService
{
    private readonly IAuditReportStore _store;
    private readonly int _retainedDays;
    private readonly ILogger<AuditReportRetentionService> _log;

    public AuditReportRetentionService(
        IAuditReportStore store,
        int retainedDays,
        ILogger<AuditReportRetentionService> log)
    {
        _store = store;
        _retainedDays = retainedDays;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        // Run once immediately at startup, then on the daily cadence.
        do
        {
            await RunSweepAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_retainedDays);
            var deleted = await _store.DeleteOlderThanAsync(cutoff, ct);
            if (deleted > 0)
                _log.LogInformation("AuditReportRetention: deleted {Count} rows older than {Cutoff:O}", deleted, cutoff);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AuditReportRetention: sweep failed");
        }
    }
}

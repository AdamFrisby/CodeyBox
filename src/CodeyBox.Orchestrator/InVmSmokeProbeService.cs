using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Drives <see cref="InVmSmokeProber"/> on a schedule: one sweep at startup,
/// then every <see cref="InVmSmokeOptions.SweepIntervalSeconds"/>. Runs in the
/// background so the first (VM-provisioning) sweep never blocks host startup.
/// Periodic sweeps are cheap — the per-baseline cache means a sweep only
/// provisions a VM when the baseline ref changed or the TTL expired, which also
/// gives excluded agents a self-healing path back into routing once their CLI
/// is fixed and a re-probe passes.
/// </summary>
public sealed class InVmSmokeProbeService : BackgroundService
{
    private readonly InVmSmokeProber _prober;
    private readonly InVmSmokeOptions _opts;
    private readonly ILogger<InVmSmokeProbeService> _log;

    public InVmSmokeProbeService(
        InVmSmokeProber prober,
        InVmSmokeOptions opts,
        ILogger<InVmSmokeProbeService> log)
    {
        _prober = prober;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_prober.Enabled)
            return;

        await SafeSweepAsync(stoppingToken);

        if (_opts.SweepIntervalSeconds <= 0)
            return;

        var interval = TimeSpan.FromSeconds(_opts.SweepIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) { return; }

            await SafeSweepAsync(stoppingToken);
        }
    }

    private async Task SafeSweepAsync(CancellationToken ct)
    {
        try
        {
            await _prober.ProbeAllAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "In-VM smoke sweep failed; will retry next interval");
        }
    }
}

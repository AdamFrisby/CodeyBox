using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Drives the in-VM smoke gate (<see cref="IInVmSmokeGate"/>) on a schedule: one
/// sweep at startup, then every <see cref="InVmSmokeOptions.SweepIntervalSeconds"/>.
/// Runs in the background so the first (VM-provisioning) sweep never blocks host
/// startup. Depends on the gate abstraction rather than the concrete prober so
/// the sweep path stays decoupled from the implementation.
///
/// <para>A passing agent is cheap to re-sweep — the per-baseline cache means a
/// sweep only provisions a VM when the baseline ref changed or the TTL expired.
/// A <em>failing</em> agent is never cached, so it is re-probed on every sweep
/// regardless of TTL; that is the self-healing path back into routing once its
/// CLI is fixed and a re-probe passes.</para>
/// </summary>
public sealed class InVmSmokeProbeService : BackgroundService
{
    private readonly IInVmSmokeGate _gate;
    private readonly InVmSmokeOptions _opts;
    private readonly ILogger<InVmSmokeProbeService> _log;

    public InVmSmokeProbeService(
        IInVmSmokeGate gate,
        InVmSmokeOptions opts,
        ILogger<InVmSmokeProbeService> log)
    {
        _gate = gate;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_gate.Enabled)
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
            await _gate.ProbeAllAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "In-VM smoke sweep failed; will retry next interval");
        }
    }
}

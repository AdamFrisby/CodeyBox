using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// One-shot startup sweep that reaps stale <c>codeybox-*</c> temp entries
/// abandoned by previous deployments and test runs. Runs once when the host
/// starts, then goes idle — it never fails startup: sweep faults are logged
/// and the host keeps coming up.
/// </summary>
public sealed class TempSweepStartupService : IHostedService
{
    private readonly Func<TempSweepOptions> _optionsAccessor;
    private readonly TempFileSweeper _sweeper;
    private readonly ILogger<TempSweepStartupService> _log;

    public TempSweepStartupService(
        Func<TempSweepOptions> optionsAccessor,
        TimeProvider? time = null,
        ILogger<TempSweepStartupService>? log = null,
        ILogger<TempFileSweeper>? sweeperLog = null)
        : this(optionsAccessor, new TempFileSweeper(time, sweeperLog), log)
    {
    }

    internal TempSweepStartupService(
        Func<TempSweepOptions> optionsAccessor,
        TempFileSweeper sweeper,
        ILogger<TempSweepStartupService>? log = null)
    {
        _optionsAccessor = optionsAccessor ?? throw new ArgumentNullException(nameof(optionsAccessor));
        _sweeper = sweeper ?? throw new ArgumentNullException(nameof(sweeper));
        _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TempSweepStartupService>.Instance;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _optionsAccessor();
        if (!options.Enabled)
        {
            _log.LogInformation("TempSweepStartupService: disabled via CodeyBox:TempSweep:Enabled=false; skipping startup sweep");
            return Task.CompletedTask;
        }

        try
        {
            var summary = _sweeper.Sweep(options, cancellationToken);
            _log.LogInformation(
                "TempSweepStartupService: startup sweep completed — {Removed} removed, {Scanned} scanned, {SkippedFresh} fresh, {SkippedSymlink} symlinks, {Errors} errors, truncated={Truncated}",
                summary.Removed, summary.Scanned, summary.SkippedFresh,
                summary.SkippedSymlink, summary.Errors, summary.Truncated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "TempSweepStartupService: startup sweep failed; continuing host startup");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

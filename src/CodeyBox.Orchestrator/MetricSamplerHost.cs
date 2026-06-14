using CodeyBox.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Hosted service that drives every registered <see cref="IMetricSampler"/>
/// on its own loop. The host re-reads <see cref="IMetricSampler.Interval"/>
/// and <see cref="IMetricSampler.Enabled"/> each cycle, so a sampler that
/// reflects hot-reloaded configuration changes cadence or pauses without a
/// host restart.
///
/// <para>One loop per sampler — a slow sampler does NOT block others. Per-loop
/// failures are logged and the loop continues; a sampler that throws
/// <see cref="OperationCanceledException"/> for the host's stopping token
/// terminates its own loop only.</para>
/// </summary>
public sealed class MetricSamplerHost : BackgroundService
{
    private readonly IReadOnlyList<IMetricSampler> _samplers;
    private readonly ILogger<MetricSamplerHost> _log;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Used when a sampler returns a non-positive <see cref="IMetricSampler.Interval"/>
    /// — we poll back later instead of busy-looping. Long enough to be cheap,
    /// short enough that re-enabling a sampler at runtime takes effect promptly.
    /// </summary>
    internal static readonly TimeSpan DisabledPollInterval = TimeSpan.FromMinutes(1);

    public MetricSamplerHost(
        IEnumerable<IMetricSampler> samplers,
        ILogger<MetricSamplerHost> log,
        TimeProvider? timeProvider = null)
    {
        _samplers = samplers.ToList();
        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_samplers.Count == 0)
        {
            _log.LogDebug("MetricSamplerHost: no samplers registered, exiting");
            return;
        }

        _log.LogInformation(
            "MetricSamplerHost: starting {Count} sampler loop(s): {Kinds}",
            _samplers.Count,
            string.Join(", ", _samplers.Select(s => s.Kind)));

        var loops = _samplers.Select(s => RunLoopAsync(s, stoppingToken)).ToArray();
        await Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(IMetricSampler sampler, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Re-read Enabled + Interval each tick so the sampler can react to
            // hot-reloaded configuration without restarting the loop.
            var enabled = SafeRead(() => sampler.Enabled, defaultValue: true, "Enabled", sampler.Kind);
            var interval = SafeRead(() => sampler.Interval, defaultValue: DisabledPollInterval, "Interval", sampler.Kind);

            var delay = enabled && interval > TimeSpan.Zero ? interval : DisabledPollInterval;

            try
            {
                await Task.Delay(delay, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (stoppingToken.IsCancellationRequested)
                return;
            if (!enabled || interval <= TimeSpan.Zero)
                continue;

            try
            {
                await sampler.SampleOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "MetricSamplerHost: sampler {Kind} threw; continuing on next tick",
                    sampler.Kind);
            }
        }
    }

    private T SafeRead<T>(Func<T> reader, T defaultValue, string propertyName, string kind)
    {
        try
        {
            return reader();
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                ex,
                "MetricSamplerHost: sampler {Kind} threw reading {Property}; falling back to default",
                kind,
                propertyName);
            return defaultValue;
        }
    }
}

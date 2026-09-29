using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Enforces the configured majordomo idle bound in production: wakes
/// periodically and tears the sandbox down once it has been idle longer than
/// <see cref="MajordomoSandboxOptions.IdleTimeout"/>. The next turn
/// transparently recreates it through
/// <see cref="MajordomoSandboxSession.GetOrCreateAsync"/>.
/// </summary>
/// <remarks>
/// The poll interval is derived from the live bound (one quarter of it, capped
/// so an 8-hour bound still reaps within minutes of expiry) — no separate
/// interval knob to drift out of sync with the bound it enforces. The bound
/// itself is re-read every iteration, so an operator edit applies without a
/// restart. Never touches the work-item dispatch path: no concurrency-gate
/// permit, no worker-pool mutation.
/// </remarks>
public sealed class MajordomoSandboxIdleService : BackgroundService
{
    /// <summary>
    /// Upper cap for the derived poll interval, so a very long idle bound
    /// still tears down within minutes of expiry rather than hours late.
    /// </summary>
    public static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(5);

    private readonly MajordomoSandboxSession _session;
    private readonly Func<MajordomoSandboxOptions> _optionsAccessor;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;

    public MajordomoSandboxIdleService(
        MajordomoSandboxSession session,
        Func<MajordomoSandboxOptions> optionsAccessor,
        TimeProvider? clock = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        _session = session;
        _optionsAccessor = optionsAccessor;
        _clock = clock ?? TimeProvider.System;
        _log = log ?? NullLogger.Instance;
    }

    /// <summary>
    /// One reap pass: tears down the sandbox when it has been idle past the
    /// configured bound. Returns true when a teardown happened.
    /// </summary>
    public Task<bool> CheckOnceAsync(CancellationToken ct = default) =>
        _session.NotifyIdleExpiredAsync(ct);

    /// <summary>
    /// Pure derivation of the poll interval from the live idle bound: one
    /// quarter of the bound so expiry is noticed promptly, capped at
    /// <see cref="MaxPollInterval"/> so very long bounds still reap within
    /// minutes of expiry.
    /// </summary>
    public static TimeSpan ComputePollInterval(TimeSpan idleBound)
    {
        if (idleBound <= TimeSpan.Zero)
            return MaxPollInterval;
        var quarter = TimeSpan.FromTicks(idleBound.Ticks / 4);
        return quarter > MaxPollInterval ? MaxPollInterval : quarter;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = ComputePollInterval(_optionsAccessor().IdleTimeout);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Majordomo idle service: failed to read options; retrying later.");
                delay = MaxPollInterval;
            }

            try
            {
                await Task.Delay(delay, _clock, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (await _session.NotifyIdleExpiredAsync(stoppingToken).ConfigureAwait(false))
                    _log.LogInformation("Majordomo idle service: tore down the idle sandbox.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Majordomo idle service: reap pass failed; will retry.");
            }
        }
    }
}

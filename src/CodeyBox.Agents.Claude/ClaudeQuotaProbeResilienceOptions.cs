namespace CodeyBox.Agents.Claude;

/// <summary>
/// Resilience tuning for <see cref="ClaudeQuotaProbe"/>: how many times to
/// retry a transient probe failure, and how long a stale last-known-good
/// snapshot may be retained before falling back to <c>AvailablePct=-1</c>.
///
/// <para>
/// Read on every probe call so values bound from <c>CodeyBox:QuotaRouter</c>
/// hot-reload through <see cref="Microsoft.Extensions.Options.IOptionsMonitor{T}"/>
/// without restarting the process. Keep this record immutable; the DI wiring
/// constructs a fresh instance from the current options snapshot on each read.
/// </para>
/// </summary>
public sealed record ClaudeQuotaProbeResilienceOptions
{
    public static TimeSpan DefaultMaxRetryDelay { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Additional attempts after the initial fetch fails transiently
    /// (network error / timeout / 5xx). Total attempts = 1 + MaxRetries.
    /// Default 2 (3 attempts total).
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Base backoff for between-retry sleeps; the actual delay doubles each
    /// attempt (RetryInitialDelay, 2x, 4x ...). Default 250 ms.
    /// </summary>
    public TimeSpan RetryInitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Ceiling for any between-retry sleep, including provider
    /// <c>Retry-After</c> values. Default 5 minutes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = DefaultMaxRetryDelay;

    /// <summary>
    /// Number of consecutive end-to-end probe failures (each end-to-end probe
    /// already includes the retries above) tolerated before the probe gives up
    /// on the retained last-known-good snapshot and reports
    /// <c>AvailablePct=-1</c>. Default 3.
    /// </summary>
    public int MaxConsecutiveFailures { get; init; } = 3;

    /// <summary>
    /// Maximum age of a retained last-known-good snapshot. Once the snapshot
    /// is older than this, the probe stops returning the stale value and
    /// reports <c>AvailablePct=-1</c> regardless of the consecutive-failure
    /// count. Default 5 minutes.
    /// </summary>
    public TimeSpan MaxStaleness { get; init; } = TimeSpan.FromMinutes(5);
}

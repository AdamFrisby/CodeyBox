using System.Net.Http.Headers;

namespace CodeyBox.Agents;

/// <summary>
/// Shared Retry-After handling for provider quota probes. Parses both
/// delta-seconds and HTTP-date forms and centralises the delay/cap contract so
/// provider-specific probes cannot drift.
/// </summary>
public static class HttpQuotaRetryPolicy
{
    public static TimeSpan? TryGetRetryAfterDelay(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var retryAfter = headers.RetryAfter;
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta <= TimeSpan.Zero ? TimeSpan.Zero : delta;

        if (retryAfter.Date is { } date)
        {
            var delay = date - now;
            return delay <= TimeSpan.Zero ? TimeSpan.Zero : delay;
        }

        return null;
    }

    public static DateTimeOffset? TryGetRetryAfterReset(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var delay = TryGetRetryAfterDelay(headers, now);
        if (delay is null)
            return null;

        return now + delay.Value;
    }

    /// <summary>
    /// Default ceiling applied when <paramref name="maxDelay"/> is not positive.
    /// Keeps a large (or malicious) provider <c>Retry-After</c> from wedging a
    /// probe that was constructed without explicit tuning.
    /// </summary>
    public static TimeSpan DefaultMaxRetryDelay { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Returns the larger of the computed exponential backoff and the provider's
    /// <c>Retry-After</c> delay, capped at <paramref name="maxDelay"/> so a large
    /// server value cannot wedge the probe. The cap applies to the total delay
    /// (not just the locally-computed exponential part): callers wait at least
    /// the server's <c>Retry-After</c> up to the cap, then retry. A zero or
    /// negative <paramref name="maxDelay"/> falls back to
    /// <see cref="DefaultMaxRetryDelay"/>.
    /// </summary>
    public static TimeSpan ComputeRetryDelay(
        TimeSpan exponentialDelay,
        TimeSpan? retryAfterDelay,
        TimeSpan maxDelay)
    {
        var cap = maxDelay > TimeSpan.Zero ? maxDelay : DefaultMaxRetryDelay;
        var cappedExponential = exponentialDelay > cap ? cap : exponentialDelay;
        var serverDelay = retryAfterDelay ?? TimeSpan.Zero;
        var cappedServer = serverDelay > cap ? cap : serverDelay;
        var delay = cappedExponential >= cappedServer
            ? cappedExponential
            : cappedServer;

        if (delay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return delay;
    }
}

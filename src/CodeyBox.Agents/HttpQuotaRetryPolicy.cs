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
    /// Returns the larger of the computed exponential backoff and the provider's
    /// <c>Retry-After</c> delay. <paramref name="maxDelay"/> caps only the
    /// locally-computed exponential delay: a provider's Retry-After value is
    /// never shortened, because retrying before it expires can prolong a rate
    /// limit. A zero or negative cap intentionally means "uncapped".
    /// </summary>
    public static TimeSpan ComputeRetryDelay(
        TimeSpan exponentialDelay,
        TimeSpan? retryAfterDelay,
        TimeSpan maxDelay)
    {
        var cappedExponential = maxDelay > TimeSpan.Zero && exponentialDelay > maxDelay
            ? maxDelay
            : exponentialDelay;
        var serverDelay = retryAfterDelay ?? TimeSpan.Zero;
        var delay = cappedExponential >= serverDelay
            ? cappedExponential
            : serverDelay;

        if (delay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        return delay;
    }
}

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Shared Retry-After parsing for credential-provider plugins. The pure
/// <c>Parse(response, now)</c> core keeps the decision logic testable with
/// an injected clock; the <see cref="TimeProvider"/> overload lets call
/// sites pass their existing clock without touching wall-clock time
/// directly. One implementation so backoff clamping cannot drift per
/// backend.
/// </summary>
public static class CredentialRetryAfter
{
    /// <summary>Maximum backoff surfaced from a Retry-After header, in seconds.</summary>
    public const int MaxBackoffSeconds = 3600;

    /// <summary>
    /// Parses the Retry-After header against an explicit <paramref name="now"/>
    /// (pure core: pass the injected clock's time in production, a fake
    /// clock's time in tests).
    /// </summary>
    public static int? Parse(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            if (response.Headers.RetryAfter?.Delta is TimeSpan delta)
                return (int)Math.Clamp(delta.TotalSeconds, 0, MaxBackoffSeconds);
            if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
                return (int)Math.Clamp((date - now).TotalSeconds, 0, MaxBackoffSeconds);
        }
        catch (FormatException)
        {
            // Malformed header: no backoff hint.
        }
        return null;
    }

    /// <summary>Parses the Retry-After header against the injected clock's current time.</summary>
    public static int? Parse(HttpResponseMessage response, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return Parse(response, clock.GetUtcNow());
    }
}

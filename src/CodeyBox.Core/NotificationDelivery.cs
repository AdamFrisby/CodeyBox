using Microsoft.Extensions.Logging;

namespace CodeyBox.Core;

/// <summary>
/// Platform-neutral transport policy shared by notification provider
/// plugins. Lives in Core (like <see cref="NotificationCorrelation"/>) so
/// every provider applies the same timeout floor and default — never a
/// per-plugin fork.
/// </summary>
public static class NotificationDelivery
{
    /// <summary>Default per-call timeout, in seconds, for provider REST
    /// posts. Used when the provider's configured timeout is absent or
    /// invalid.</summary>
    public const int DefaultPostTimeoutSeconds = 15;

    /// <summary>
    /// The effective per-call post timeout: the configured value when valid
    /// (&gt;= 1 second), else <see cref="DefaultPostTimeoutSeconds"/>. An
    /// invalid configured value is logged through <paramref name="log"/>,
    /// never silently overridden.
    /// </summary>
    public static TimeSpan PostTimeoutOrDefault(int configuredSeconds, ILogger? log = null)
    {
        if (configuredSeconds >= 1)
            return TimeSpan.FromSeconds(configuredSeconds);
        log?.LogWarning(
            "PostTimeoutSeconds={ConfiguredSeconds} is invalid (must be >= 1); using the default {DefaultSeconds}s",
            configuredSeconds, DefaultPostTimeoutSeconds);
        return TimeSpan.FromSeconds(DefaultPostTimeoutSeconds);
    }
}

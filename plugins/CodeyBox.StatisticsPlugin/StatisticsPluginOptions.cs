using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.StatisticsPlugin;

/// <summary>
/// Operator-configurable knobs for the statistics plugin. Bound from
/// <c>CodeyBox:Plugins:codeybox.statistics</c> via <c>IPluginHost.ScopedConfig</c>.
/// All fields are read each tick / each prune cycle so reload-token-driven
/// changes take effect without a host restart.
/// </summary>
public sealed record StatisticsPluginOptions
{
    /// <summary>
    /// Master switch for the quota sampler. When false the sampler's loop keeps
    /// running but never invokes the probes — flipping it back to true picks up
    /// on the next tick.
    /// </summary>
    public bool QuotaSamplerEnabled { get; init; } = true;

    /// <summary>
    /// Interval between quota probe snapshots. Default 15 minutes, matching the
    /// cadence of the stopgap external poller this plugin replaces.
    /// </summary>
    public TimeSpan QuotaSamplerInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Rows older than this are removed from the time-series during the
    /// hourly prune sweep. Default 30 days — long enough to spot a weekly cap
    /// shift across two reset boundaries and short enough to keep the SQLite
    /// file small at 15-minute cadence.
    /// </summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Absolute path to the statistics SQLite file. When null/empty the plugin
    /// falls back to <c>codeybox-stats.db</c> next to the orchestrator's
    /// <c>StateDatabasePath</c> so a fresh deployment lands the file in the
    /// same dataroot as the rest of the orchestrator's state.
    /// </summary>
    public string? DatabasePath { get; init; }

    /// <summary>
    /// Hard upper bound on rows returned by a single <c>QueryAsync</c> call.
    /// Filter <c>Limit</c> values above this are clamped — protects the API
    /// from a runaway client that asks for everything at once.
    /// </summary>
    public int MaxQueryRows { get; init; } = 50_000;

    public static StatisticsPluginOptions FromConfiguration(IConfigurationSection section)
    {
        if (section is null)
            return new StatisticsPluginOptions();

        var defaults = new StatisticsPluginOptions();
        return new StatisticsPluginOptions
        {
            QuotaSamplerEnabled = ReadBool(section, "QuotaSamplerEnabled", defaults.QuotaSamplerEnabled),
            QuotaSamplerInterval = ReadInterval(
                section,
                "QuotaSamplerIntervalSeconds",
                defaults.QuotaSamplerInterval,
                minimum: TimeSpan.FromSeconds(10)),
            Retention = ReadInterval(
                section,
                "RetentionHours",
                defaults.Retention,
                minimum: TimeSpan.FromHours(1),
                unit: TimeUnit.Hours),
            DatabasePath = section["DatabasePath"]?.Trim() is { Length: > 0 } path ? path : null,
            MaxQueryRows = ReadInt(section, "MaxQueryRows", defaults.MaxQueryRows, minimum: 1),
        };
    }

    private enum TimeUnit { Seconds, Hours }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return bool.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback, int minimum)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return fallback;
        return parsed < minimum ? minimum : parsed;
    }

    private static TimeSpan ReadInterval(
        IConfigurationSection section,
        string key,
        TimeSpan fallback,
        TimeSpan minimum,
        TimeUnit unit = TimeUnit.Seconds)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return fallback;
        var span = unit switch
        {
            TimeUnit.Hours => TimeSpan.FromHours(parsed),
            _ => TimeSpan.FromSeconds(parsed),
        };
        return span < minimum ? minimum : span;
    }
}

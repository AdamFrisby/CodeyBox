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

    /// <summary>
    /// Knobs for the banked reset-credit expiry tracker. Bound from the nested
    /// <c>ResetCreditExpiry</c> config sub-section.
    /// </summary>
    public ResetCreditExpiryOptions ResetCreditExpiry { get; init; } = new();

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
            ResetCreditExpiry = ResetCreditExpiryOptions.FromConfiguration(
                section.GetSection("ResetCreditExpiry")),
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

/// <summary>
/// Operator knobs for the banked reset-credit expiry tracker. Bound from
/// <c>CodeyBox:Plugins:codeybox.statistics:ResetCreditExpiry</c>. All values
/// are read each request so hot-reload applies without a host restart.
/// </summary>
public sealed record ResetCreditExpiryOptions
{
    /// <summary>Agent whose reset-credit count series is tracked. Codex is the only provider that exposes reset credits today.</summary>
    public string Agent { get; init; } = "codex";

    /// <summary>Provider-published credit lifetime. Codex publishes 30 days.</summary>
    public TimeSpan ExpiryPeriod { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Margin before the raw expiry at which the advisor should prompt. Default 24 hours.</summary>
    public TimeSpan SafetyBuffer { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// How far back the count series is read when a query supplies no explicit
    /// lower bound. Defaults to twice the expiry period so every live credit's
    /// grant sample is in range (bounded in practice by the sampler's
    /// retention window).
    /// </summary>
    public TimeSpan Lookback { get; init; } = TimeSpan.FromDays(60);

    /// <summary>Operator-seeded pre-observation credits (estimated expiries).</summary>
    public IReadOnlyList<SeededResetCreditOption> Seeds { get; init; } = Array.Empty<SeededResetCreditOption>();

    public static ResetCreditExpiryOptions FromConfiguration(IConfigurationSection section)
    {
        if (section is null)
            return new ResetCreditExpiryOptions();

        var defaults = new ResetCreditExpiryOptions();
        return new ResetCreditExpiryOptions
        {
            Agent = section["Agent"]?.Trim() is { Length: > 0 } agent ? agent : defaults.Agent,
            ExpiryPeriod = ReadSpanDays(section, "ExpiryPeriodDays", defaults.ExpiryPeriod, TimeSpan.FromHours(1)),
            SafetyBuffer = ReadSpanHours(section, "SafetyBufferHours", defaults.SafetyBuffer, TimeSpan.Zero),
            Lookback = ReadSpanDays(section, "LookbackDays", defaults.Lookback, TimeSpan.FromDays(1)),
            Seeds = ReadSeeds(section.GetSection("Seeds")),
        };
    }

    private static IReadOnlyList<SeededResetCreditOption> ReadSeeds(IConfigurationSection seedsSection)
    {
        var seeds = new List<SeededResetCreditOption>();
        foreach (var child in seedsSection.GetChildren())
        {
            var raw = child["EstimatedExpiresAt"];
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (!DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var expiresAt))
                continue;

            var label = child["Label"]?.Trim();
            seeds.Add(new SeededResetCreditOption
            {
                EstimatedExpiresAt = expiresAt,
                Label = string.IsNullOrEmpty(label) ? null : label,
            });
        }

        return seeds;
    }

    private static TimeSpan ReadSpanDays(IConfigurationSection section, string key, TimeSpan fallback, TimeSpan minimum)
        => ReadSpan(section, key, fallback, minimum, TimeSpan.FromDays(1));

    private static TimeSpan ReadSpanHours(IConfigurationSection section, string key, TimeSpan fallback, TimeSpan minimum)
        => ReadSpan(section, key, fallback, minimum, TimeSpan.FromHours(1));

    private static TimeSpan ReadSpan(IConfigurationSection section, string key, TimeSpan fallback, TimeSpan minimum, TimeSpan unit)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            return fallback;
        var span = parsed * unit;
        return span < minimum ? minimum : span;
    }
}

/// <summary>Config shape for one operator-seeded pre-observation reset credit.</summary>
public sealed record SeededResetCreditOption
{
    /// <summary>Estimated expiry of the pre-observation credit (ISO-8601).</summary>
    public required DateTimeOffset EstimatedExpiresAt { get; init; }

    /// <summary>Optional operator label describing the estimate's basis.</summary>
    public string? Label { get; init; }
}

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.QuotaResetNotifier;

/// <summary>
/// Operator-configurable knobs for the quota-reset notifier plugin. Bound from
/// <c>CodeyBox:Plugins:codeybox.quota-reset-notifier</c> via
/// <c>IPluginHost.ScopedConfig</c>. Every value is re-read each sampling tick
/// (and each <c>Enabled</c> / <c>Interval</c> read) so reload-token-driven
/// changes take effect without a host restart.
/// </summary>
public sealed record QuotaResetNotifierOptions
{
    /// <summary>
    /// Master switch. When false the sampler's loop keeps running but never
    /// evaluates advice or publishes events — flipping it back to true picks
    /// up on the next tick.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How often the plugin re-evaluates reset advice per watched agent.
    /// Default 15 minutes, matching the statistics plugin's quota sampler so
    /// advice is checked once per fresh snapshot.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Agents to watch. Defaults to <c>codex</c>; add <c>claude</c> once its
    /// 4.8 upgrade is proven. Empty = notify for none. Entries are matched
    /// case-insensitively against the advisor's agent names.
    /// </summary>
    public IReadOnlyList<string> Agents { get; init; } = new[] { "codex" };

    /// <summary>
    /// Minimum interval between <c>quota.reset_optimal</c> pings for the same
    /// agent. Suppresses repeat pings when the optimal window persists across
    /// ticks; a verdict for an already-pinged window is never re-sent
    /// regardless of this value. Default 24 hours.
    /// </summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on watched agents per tick — caps fan-out from a mis-edited list.</summary>
    internal const int MaxAgentsPerTick = 32;

    public static QuotaResetNotifierOptions FromConfiguration(IConfigurationSection section)
    {
        if (section is null)
            return new QuotaResetNotifierOptions();

        var defaults = new QuotaResetNotifierOptions();
        return new QuotaResetNotifierOptions
        {
            Enabled = ReadBool(section, "Enabled", defaults.Enabled),
            Interval = ReadIntervalSeconds(section, "IntervalSeconds", defaults.Interval, TimeSpan.FromSeconds(10)),
            Agents = ReadAgents(section.GetSection("Agents"), defaults.Agents),
            Cooldown = ReadIntervalSeconds(section, "CooldownSeconds", defaults.Cooldown, TimeSpan.Zero),
        };
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return bool.TryParse(raw, out var parsed) ? parsed : fallback;
    }

    private static TimeSpan ReadIntervalSeconds(
        IConfigurationSection section, string key, TimeSpan fallback, TimeSpan minimum)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            return fallback;
        var span = TimeSpan.FromSeconds(parsed);
        return span < minimum ? minimum : span;
    }

    private static IReadOnlyList<string> ReadAgents(IConfigurationSection agentsSection, IReadOnlyList<string> fallback)
    {
        var agents = new List<string>();
        foreach (var child in agentsSection.GetChildren())
        {
            var value = child.Value?.Trim();
            if (!string.IsNullOrEmpty(value) && !agents.Contains(value, StringComparer.OrdinalIgnoreCase))
                agents.Add(value);
        }

        // A present-but-empty Agents section is an explicit "watch none";
        // only fall back to the default when the section was absent entirely.
        return agentsSection.Exists() ? agents : new List<string>(fallback);
    }
}

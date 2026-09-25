using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.Core;

/// <summary>
/// Shared tolerant readers for plugin configuration sections: an absent or
/// unparsable value falls back to the declared default instead of throwing,
/// so a bad hot-reload never crashes a poll or post.
/// </summary>
public static class PluginConfigReaders
{
    /// <summary>Reads a bool value, or <paramref name="fallback"/> when absent/unparsable.</summary>
    public static bool ReadBool(IConfigurationSection section, string key, bool fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw.Trim(), out var parsed)
            ? fallback : parsed;
    }

    /// <summary>Reads an int value, or <paramref name="fallback"/> when absent/unparsable.</summary>
    public static int ReadInt(IConfigurationSection section, string key, int fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw)
            || !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? fallback : parsed;
    }

    /// <summary>Reads a trimmed non-empty string, or <paramref name="fallback"/> when absent/blank.</summary>
    public static string ReadNonEmpty(IConfigurationSection section, string key, string fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    /// <summary>Reads a child section into a trimmed string map.</summary>
    public static IReadOnlyDictionary<string, string> ReadMap(
        IConfigurationSection section, IEqualityComparer<string>? comparer = null)
    {
        var map = new Dictionary<string, string>(comparer ?? StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Key) && child.Value is not null)
                map[child.Key.Trim()] = child.Value.Trim();
        }
        return map;
    }

    /// <summary>Reads a child section into a trimmed string list, or <paramref name="fallback"/> when empty.</summary>
    public static IReadOnlyList<string> ReadList(
        IConfigurationSection section, IReadOnlyList<string> fallback)
    {
        var values = section.GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToList();
        return values.Count == 0 ? fallback : values;
    }
}

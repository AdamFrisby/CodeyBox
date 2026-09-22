using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.PluginSdk.Credentials;

/// <summary>
/// Shared configuration readers for credential-provider plugins. One
/// implementation so boolean/integer parsing, loopback detection, and
/// environment-variable-name validation cannot drift per backend.
/// </summary>
public static class CredentialOptions
{
    /// <summary>Reads an optional boolean; unparseable or absent values fall back.</summary>
    public static bool ReadBool(IConfigurationSection section, string key, bool fallback)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw.Trim(), out var parsed) ? fallback : parsed;
    }

    /// <summary>
    /// Reads an optional integer; absent values fall back, unparseable
    /// values fall back with a warning surfaced to the operator.
    /// </summary>
    public static int ReadInt(
        IConfigurationSection section, string key, int fallback, List<string>? warnings)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            warnings?.Add($"'{key}' value '{raw.Trim()}' is not an integer; using {fallback}.");
            return fallback;
        }
        return parsed;
    }

    /// <summary>Reads an optional non-empty string; absent values fall back.</summary>
    public static string ReadNonEmpty(IConfigurationSection section, string key, string fallback)
    {
        ArgumentNullException.ThrowIfNull(section);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(fallback);
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    /// <summary>True for loopback hosts allowed to serve plain HTTP (tests and local backends).</summary>
    public static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host, "::1", StringComparison.Ordinal)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for valid environment-variable names.</summary>
    public static bool IsEnvVarName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        if (!(value[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or '_'))
            return false;
        foreach (var c in value.AsSpan(1))
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                return false;
        }
        return true;
    }
}

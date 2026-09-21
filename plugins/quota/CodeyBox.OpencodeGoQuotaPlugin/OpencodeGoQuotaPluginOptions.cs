using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.OpencodeGoQuotaPlugin;

/// <summary>
/// Operator-configurable knobs for the opencode-go quota plugin. Bound from
/// <c>CodeyBox:Plugins:codeybox.opencode-go-quota</c>. Every value is re-read
/// on each probe call (and each <c>Handles</c> check) so config reloads take
/// effect without a host restart.
/// </summary>
public sealed record OpencodeGoQuotaPluginOptions
{
    /// <summary>
    /// Base URL of the opencode-go plan API. The <c>/usage</c> path is derived
    /// by appending <c>/usage</c> to this value (trailing slashes trimmed), so
    /// the host is never hardcoded in source. Default is the verified live
    /// endpoint root <c>https://opencode.ai/zen/go/v1</c>.
    /// </summary>
    public string ProviderBaseUrl { get; init; } = OpencodeGoQuotaProbe.DefaultProviderBaseUrl;

    /// <summary>
    /// Explicit override for the copilot BYOK provider base URL used by the
    /// <c>Handles</c> gate. When null/empty the plugin reads the live
    /// <c>CodeyBox:Copilot:Provider:BaseUrl</c> value instead. Exists so tests
    /// (and operators with per-deployment quirks) can pin the gate without
    /// touching the copilot runner's own configuration.
    /// </summary>
    public string? CopilotProviderBaseUrl { get; init; }

    /// <summary>
    /// Bearer token for <c>GET /usage</c> (<c>Authorization: Bearer</c>). Falls
    /// back to the <c>CODEYBOX_OPENCODE_GO_API_KEY</c> environment variable
    /// when unset. The secret never sits in a log line — neither the header
    /// nor the response body is logged.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>How long a fetched snapshot is served from memory. Default 60 s.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Per-request timeout for the <c>/usage</c> call. Default 10 s.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Consecutive failed fetches after which the probe logs at Warning
    /// (earlier failures stay at Debug). Default 3.
    /// </summary>
    public int SustainedFailureWarningThreshold { get; init; } = 3;

    /// <summary>Environment variable consulted when <see cref="ApiKey"/> is unset.</summary>
    public const string ApiKeyEnvironmentVariable = "CODEYBOX_OPENCODE_GO_API_KEY";

    internal static OpencodeGoQuotaPluginOptions FromConfiguration(IConfigurationSection scoped)
    {
        var defaults = new OpencodeGoQuotaPluginOptions();
        return new OpencodeGoQuotaPluginOptions
        {
            ProviderBaseUrl = ReadNonEmpty(scoped, "ProviderBaseUrl", defaults.ProviderBaseUrl),
            CopilotProviderBaseUrl = ReadNonEmptyOrNull(scoped, "CopilotProviderBaseUrl"),
            ApiKey = ReadNonEmptyOrNull(scoped, "ApiKey"),
            CacheTtl = ReadIntervalSeconds(scoped, "CacheTtlSeconds", defaults.CacheTtl),
            Timeout = ReadIntervalSeconds(scoped, "TimeoutSeconds", defaults.Timeout),
            SustainedFailureWarningThreshold = ReadInt(
                scoped,
                "SustainedFailureWarningThreshold",
                defaults.SustainedFailureWarningThreshold,
                minimum: 1),
        };
    }

    private static string ReadNonEmpty(IConfigurationSection section, string key, string fallback)
    {
        var raw = section[key]?.Trim();
        return string.IsNullOrEmpty(raw) ? fallback : raw;
    }

    private static string? ReadNonEmptyOrNull(IConfigurationSection section, string key)
    {
        var raw = section[key]?.Trim();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    private static TimeSpan ReadIntervalSeconds(IConfigurationSection section, string key, TimeSpan fallback)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return fallback;
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return fallback;
        var span = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromMinutes(5).TotalSeconds));
        return span < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : span;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback, int minimum)
    {
        var raw = section[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return fallback;
        return parsed < minimum ? minimum : parsed;
    }
}

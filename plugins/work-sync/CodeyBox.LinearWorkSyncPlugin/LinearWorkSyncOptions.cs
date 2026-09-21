using System.Globalization;
using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.LinearWorkSyncPlugin;

/// <summary>
/// Operator knobs for the Linear work-sync plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.linear-worksync</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every poll/post so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="TokenEnvVar"/>,
/// <see cref="OAuthClientSecretEnvVar"/>, <see cref="OAuthRefreshTokenEnvVar"/>
/// and <see cref="WebhookSecretEnvVar"/> name environment variables whose
/// values the operator provisions from the host credential chain (vault agent,
/// systemd credentials, container secrets). Only the names are configured.</para>
/// </summary>
public sealed record LinearWorkSyncOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.linear-worksync";

    /// <summary>Provider namespace recorded in <c>WorkItem.ExternalIds</c>.</summary>
    public const string ProviderNamespace = "linear";

    /// <summary>Master switch for this plugin. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>Linear GraphQL endpoint. Must be https; defaults to the public cloud.</summary>
    public string ApiUrl { get; init; } = "https://api.linear.app/graphql";

    /// <summary>Per-request timeout, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Which upstream field carries the ingestion signal. Default Assignee (service account).</summary>
    public WorkSignalKind SignalKind { get; init; } = WorkSignalKind.Assignee;

    /// <summary>
    /// Exact value that must be present for ingestion (ordinal-ignore-case exact
    /// match, never substring). For <c>Assignee</c> this is the Linear service-account
    /// user id (preferred) or email; for <c>Label</c> the label name; for
    /// <c>Status</c> the workflow-state name.
    /// </summary>
    public string SignalValue { get; init; } = string.Empty;

    /// <summary>
    /// Maps a Linear team key (e.g. <c>ENG</c>) to the CodeyBox project id that
    /// ingested issues belong to. Issues from unmapped teams are skipped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> TeamProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit CodeyBox-state-name to Linear-workflow-state-name declaration
    /// (e.g. <c>{ "Working": "In Progress", "Done": "Done" }</c>), parsed by
    /// <see cref="WorkStateMapping.Parse"/>. A state with no entry is reported
    /// as unmapped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> StateMapping { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Name of the environment variable holding the Linear API key or OAuth
    /// access token. The value is never read from configuration files.
    /// </summary>
    public string TokenEnvVar { get; init; } = "LINEAR_API_KEY";

    /// <summary>Linear OAuth token endpoint (https). Only used when OAuth env vars are set.</summary>
    public string OAuthTokenUrl { get; init; } = "https://api.linear.app/oauth/token";

    /// <summary>Name of the env var holding the Linear OAuth client id. Empty disables OAuth.</summary>
    public string OAuthClientIdEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the Linear OAuth client secret. Empty disables OAuth.</summary>
    public string OAuthClientSecretEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the Linear OAuth refresh token. Empty disables OAuth.</summary>
    public string OAuthRefreshTokenEnvVar { get; init; } = string.Empty;

    /// <summary>Public URL Linear delivers webhooks to. Required when <see cref="ManageWebhooks"/> is true.</summary>
    public string WebhookUrl { get; init; } = string.Empty;

    /// <summary>
    /// When true the plugin registers its webhook with Linear at startup
    /// (creating it when absent) and removes it on disposal. When false only
    /// polling is used — required for deployments without inbound traffic.
    /// </summary>
    public bool ManageWebhooks { get; init; }

    /// <summary>Name of the env var holding the Linear webhook signing secret.</summary>
    public string WebhookSecretEnvVar { get; init; } = "LINEAR_WEBHOOK_SECRET";

    /// <summary>Linear user logins identifying CodeyBox itself, for loop-guard attribution alongside the marker.</summary>
    public IReadOnlyList<string> ServiceLogins { get; init; } = ["codeybox[bot]"];

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; init; } = 100;

    /// <summary>GraphQL page size per request (1–100, default 50).</summary>
    public int PollPageSize { get; init; } = 50;

    /// <summary>Upper bound on ingested title/body length (chars) before use.</summary>
    public int MaxIngestedBodyChars { get; init; } = 64 * 1024;

    /// <summary>Upper bound on cached identifier-to-id resolutions.</summary>
    internal const int MaxIdCacheEntries = 1000;

    /// <summary>Builds the operator-configured ingestion signal.</summary>
    public WorkSignal RequiredSignal => new(SignalKind, SignalValue);

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults (disabled features, bounded numbers)
    /// and are surfaced via <paramref name="warnings"/> instead of throwing,
    /// so a bad hot-reload never crashes a poll.
    /// </summary>
    public static LinearWorkSyncOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new LinearWorkSyncOptions();
        if (section is null)
            return defaults;

        var signalKind = defaults.SignalKind;
        var rawKind = section["SignalKind"];
        if (!string.IsNullOrWhiteSpace(rawKind))
        {
            if (Enum.TryParse<WorkSignalKind>(rawKind.Trim(), ignoreCase: true, out var parsed))
                signalKind = parsed;
            else
                warnings?.Add($"unknown SignalKind '{rawKind}'; using '{defaults.SignalKind}'");
        }

        return new LinearWorkSyncOptions
        {
            Enabled = ReadBool(section, "Enabled", defaults.Enabled),
            ApiUrl = ReadNonEmpty(section, "ApiUrl", defaults.ApiUrl),
            TimeoutSeconds = Math.Clamp(ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds), 1, 300),
            SignalKind = signalKind,
            SignalValue = (section["SignalValue"] ?? defaults.SignalValue).Trim(),
            TeamProjectMap = ReadMap(section.GetSection("TeamProjectMap")),
            StateMapping = ReadMap(section.GetSection("StateMapping"), StringComparer.Ordinal),
            TokenEnvVar = ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            OAuthTokenUrl = ReadNonEmpty(section, "OAuthTokenUrl", defaults.OAuthTokenUrl),
            OAuthClientIdEnvVar = (section["OAuthClientIdEnvVar"] ?? string.Empty).Trim(),
            OAuthClientSecretEnvVar = (section["OAuthClientSecretEnvVar"] ?? string.Empty).Trim(),
            OAuthRefreshTokenEnvVar = (section["OAuthRefreshTokenEnvVar"] ?? string.Empty).Trim(),
            WebhookUrl = (section["WebhookUrl"] ?? string.Empty).Trim(),
            ManageWebhooks = ReadBool(section, "ManageWebhooks", defaults.ManageWebhooks),
            WebhookSecretEnvVar = ReadNonEmpty(section, "WebhookSecretEnvVar", defaults.WebhookSecretEnvVar),
            ServiceLogins = ReadList(section.GetSection("ServiceLogins"), defaults.ServiceLogins),
            MaxItemsPerPoll = Math.Clamp(ReadInt(section, "MaxItemsPerPoll", defaults.MaxItemsPerPoll), 1, 1000),
            PollPageSize = Math.Clamp(ReadInt(section, "PollPageSize", defaults.PollPageSize), 1, 100),
            MaxIngestedBodyChars = Math.Clamp(ReadInt(section, "MaxIngestedBodyChars", defaults.MaxIngestedBodyChars), 1024, 256 * 1024),
        };
    }

    private static bool ReadBool(IConfigurationSection section, string key, bool fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) || !bool.TryParse(raw.Trim(), out var parsed) ? fallback : parsed;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw)
            || !int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? fallback : parsed;
    }

    private static string ReadNonEmpty(IConfigurationSection section, string key, string fallback)
    {
        var raw = section[key];
        return string.IsNullOrWhiteSpace(raw) ? fallback : raw.Trim();
    }

    private static IReadOnlyDictionary<string, string> ReadMap(
        IConfigurationSection section, IComparer<string>? _ = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Key) && child.Value is not null)
                map[child.Key.Trim()] = child.Value.Trim();
        }
        return map;
    }

    private static IReadOnlyList<string> ReadList(IConfigurationSection section, IReadOnlyList<string> fallback)
    {
        var values = section.GetChildren()
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Cast<string>()
            .ToList();
        return values.Count == 0 ? fallback : values;
    }
}

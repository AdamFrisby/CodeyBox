using System.Globalization;
using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.JiraWorkSyncPlugin;

/// <summary>
/// Operator knobs for the Jira work-sync plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.jira-worksync</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every poll/post so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="TokenEnvVar"/>,
/// <see cref="UserEmailEnvVar"/>, <see cref="OAuthClientSecretEnvVar"/>,
/// <see cref="OAuthRefreshTokenEnvVar"/> and <see cref="WebhookSecretEnvVar"/>
/// name environment variables whose values the operator provisions from the
/// host credential chain (vault agent, systemd credentials, container
/// secrets). Only the names are configured.</para>
/// </summary>
public sealed record JiraWorkSyncOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.jira-worksync";

    /// <summary>Provider namespace recorded in <c>WorkItem.ExternalIds</c>.</summary>
    public const string ProviderNamespace = "jira";

    /// <summary>Master switch for this plugin. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Jira base URL (scheme + host, e.g. <c>https://acme.atlassian.net</c>).
    /// Self-hosted Server/Data Center instances set their own origin. Must be http(s).
    /// </summary>
    public string ApiBaseUrl { get; init; } = "https://example.atlassian.net";

    /// <summary>Per-request timeout, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Which upstream field carries the ingestion signal. Default Assignee (service account).</summary>
    public WorkSignalKind SignalKind { get; init; } = WorkSignalKind.Assignee;

    /// <summary>
    /// Exact value that must be present for ingestion (ordinal-ignore-case exact
    /// match, never substring). For <c>Assignee</c> this is the service-account
    /// account id (preferred) or email; for <c>Label</c> the label text; for
    /// <c>Status</c> the workflow status name (e.g. <c>To Do</c>).
    /// </summary>
    public string SignalValue { get; init; } = string.Empty;

    /// <summary>
    /// Maps a Jira project key (e.g. <c>PROJ</c>) to the CodeyBox project id that
    /// ingested issues belong to. Issues from unmapped projects are skipped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit CodeyBox-state-name to Jira status/transition-name declaration
    /// (e.g. <c>{ "Working": "In Progress", "Done": "Done" }</c>), parsed by
    /// <see cref="WorkStateMapping.Parse"/>. A state with no entry is reported
    /// as unmapped, never guessed. Even a mapped value is only applied when a
    /// transition with that name (or target status) is reachable from the
    /// issue's current state — Jira transitions are not free-form writes.
    /// </summary>
    public IReadOnlyDictionary<string, string> StateMapping { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Name of the environment variable holding the Jira API token (Cloud) or
    /// password (Server/DC). Sent as Basic auth together with
    /// <see cref="UserEmailEnvVar"/>. The value is never read from configuration files.
    /// </summary>
    public string TokenEnvVar { get; init; } = "JIRA_API_TOKEN";

    /// <summary>
    /// Name of the environment variable holding the Jira user email used for
    /// Basic auth with the API token. Empty disables Basic auth (OAuth only).
    /// </summary>
    public string UserEmailEnvVar { get; init; } = "JIRA_USER_EMAIL";

    /// <summary>Jira OAuth 3LO token endpoint (https). Only used when OAuth env vars are set.</summary>
    public string OAuthTokenUrl { get; init; } = "https://auth.atlassian.com/oauth/token";

    /// <summary>Name of the env var holding the OAuth client id. Empty disables OAuth.</summary>
    public string OAuthClientIdEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the OAuth client secret. Empty disables OAuth.</summary>
    public string OAuthClientSecretEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the OAuth refresh token. Empty disables OAuth.</summary>
    public string OAuthRefreshTokenEnvVar { get; init; } = string.Empty;

    /// <summary>
    /// Atlassian cloud id selecting the tenant for OAuth 3LO calls
    /// (<c>https://api.atlassian.com/ex/jira/{cloudId}</c>). Required when
    /// OAuth is configured; ignored for Basic auth.
    /// </summary>
    public string OAuthCloudId { get; init; } = string.Empty;

    /// <summary>
    /// Public URL Jira delivers webhooks to, including the shared-secret query
    /// token (e.g. <c>https://codeybox.example.com/webhooks/jira?token=…</c>).
    /// Jira Cloud webhooks are unsigned, so this token is the delivery
    /// authentication. Required when <see cref="ManageWebhooks"/> is true.
    /// </summary>
    public string WebhookUrl { get; init; } = string.Empty;

    /// <summary>
    /// When true the plugin registers its webhook with Jira at startup
    /// (creating it when absent, refreshing the 30-day expiry when present)
    /// and removes it on disposal. When false only polling is used — required
    /// for deployments without inbound traffic.
    /// </summary>
    public bool ManageWebhooks { get; init; }

    /// <summary>Name of the env var holding the webhook shared-secret token.</summary>
    public string WebhookSecretEnvVar { get; init; } = "JIRA_WEBHOOK_SECRET";

    /// <summary>Jira user display names/emails identifying CodeyBox itself, for loop-guard attribution alongside the marker.</summary>
    public IReadOnlyList<string> ServiceLogins { get; init; } = ["codeybox[bot]"];

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; init; } = 100;

    /// <summary>REST page size per request (1–100, default 50).</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>Upper bound on ingested title/body length (chars) before use.</summary>
    public int MaxIngestedBodyChars { get; init; } = 64 * 1024;

    /// <summary>Upper bound on cached key-to-key resolutions.</summary>
    internal const int MaxIdCacheEntries = 1000;

    /// <summary>Builds the operator-configured ingestion signal.</summary>
    public WorkSignal RequiredSignal => new(SignalKind, SignalValue);

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults (disabled features, bounded numbers)
    /// and are surfaced via <paramref name="warnings"/> instead of throwing,
    /// so a bad hot-reload never crashes a poll.
    /// </summary>
    public static JiraWorkSyncOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new JiraWorkSyncOptions();
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

        return new JiraWorkSyncOptions
        {
            Enabled = ReadBool(section, "Enabled", defaults.Enabled),
            ApiBaseUrl = ReadNonEmpty(section, "ApiBaseUrl", defaults.ApiBaseUrl).TrimEnd('/'),
            TimeoutSeconds = Math.Clamp(ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds), 1, 300),
            SignalKind = signalKind,
            SignalValue = (section["SignalValue"] ?? defaults.SignalValue).Trim(),
            ProjectMap = ReadMap(section.GetSection("ProjectMap")),
            StateMapping = ReadMap(section.GetSection("StateMapping"), StringComparer.Ordinal),
            TokenEnvVar = ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            UserEmailEnvVar = (section["UserEmailEnvVar"] ?? defaults.UserEmailEnvVar).Trim(),
            OAuthTokenUrl = ReadNonEmpty(section, "OAuthTokenUrl", defaults.OAuthTokenUrl),
            OAuthClientIdEnvVar = (section["OAuthClientIdEnvVar"] ?? string.Empty).Trim(),
            OAuthClientSecretEnvVar = (section["OAuthClientSecretEnvVar"] ?? string.Empty).Trim(),
            OAuthRefreshTokenEnvVar = (section["OAuthRefreshTokenEnvVar"] ?? string.Empty).Trim(),
            OAuthCloudId = (section["OAuthCloudId"] ?? string.Empty).Trim(),
            WebhookUrl = (section["WebhookUrl"] ?? string.Empty).Trim(),
            ManageWebhooks = ReadBool(section, "ManageWebhooks", defaults.ManageWebhooks),
            WebhookSecretEnvVar = ReadNonEmpty(section, "WebhookSecretEnvVar", defaults.WebhookSecretEnvVar),
            ServiceLogins = ReadList(section.GetSection("ServiceLogins"), defaults.ServiceLogins),
            MaxItemsPerPoll = Math.Clamp(ReadInt(section, "MaxItemsPerPoll", defaults.MaxItemsPerPoll), 1, 1000),
            PageSize = Math.Clamp(ReadInt(section, "PageSize", defaults.PageSize), 1, 100),
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

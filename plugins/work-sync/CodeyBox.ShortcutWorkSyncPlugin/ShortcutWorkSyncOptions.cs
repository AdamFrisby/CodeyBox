using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.ShortcutWorkSyncPlugin;

/// <summary>
/// Operator knobs for the Shortcut work-sync plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.shortcut-worksync</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every poll/post so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="TokenEnvVar"/>,
/// <see cref="OAuthClientSecretEnvVar"/>, <see cref="OAuthRefreshTokenEnvVar"/>
/// and <see cref="WebhookSecretEnvVar"/> name environment variables whose
/// values the operator provisions from the host credential chain (vault agent,
/// systemd credentials, container secrets). Only the names are configured.</para>
/// </summary>
public sealed record ShortcutWorkSyncOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.shortcut-worksync";

    /// <summary>Provider namespace recorded in <c>WorkItem.ExternalIds</c>.</summary>
    public const string ProviderNamespace = "shortcut";

    /// <summary>Master switch for this plugin. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>Shortcut REST API base URL (scheme + host, no trailing path).</summary>
    public string ApiBaseUrl { get; init; } = "https://api.app.shortcut.com";

    /// <summary>Per-request timeout, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Which upstream field carries the ingestion signal. Default Label.</summary>
    public WorkSignalKind SignalKind { get; init; } = WorkSignalKind.Label;

    /// <summary>
    /// Exact value that must be present for ingestion (ordinal-ignore-case exact
    /// match, never substring). For <c>Label</c> the label name (e.g. <c>codeybox</c>);
    /// for <c>Assignee</c> the member UUID (preferred), email, or mention name of the
    /// service account; for <c>Status</c> the workflow-state name (e.g. <c>Unstarted</c>).
    /// </summary>
    public string SignalValue { get; init; } = string.Empty;

    /// <summary>
    /// Maps a Shortcut project id (numeric, as a string, e.g. <c>"12"</c>) to the
    /// CodeyBox project id that ingested stories belong to. Stories from unmapped
    /// projects are skipped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit CodeyBox-state-name to Shortcut-workflow-state-name declaration
    /// (e.g. <c>{ "Working": "In Progress", "Done": "Done" }</c>), parsed by
    /// <see cref="WorkStateMapping.Parse"/>. A state with no entry is reported
    /// as unmapped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> StateMapping { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// When true, signalled epics each ingest as ONE work item
    /// (<c>sc-epic-{id}</c>). When false (default) only stories ingest and
    /// signalled epics are ignored. Epics never fan out into per-story items.
    /// </summary>
    public bool IngestEpics { get; init; }

    /// <summary>
    /// Name of the environment variable holding the Shortcut API token.
    /// Sent as <c>Shortcut-Token</c>. The value is never read from configuration files.
    /// </summary>
    public string TokenEnvVar { get; init; } = "SHORTCUT_API_TOKEN";

    /// <summary>Shortcut OAuth token endpoint (https). Only used when OAuth env vars are set.</summary>
    public string OAuthTokenUrl { get; init; } = "https://app.shortcut.com/oauth/token";

    /// <summary>Name of the env var holding the Shortcut OAuth client id. Empty disables OAuth.</summary>
    public string OAuthClientIdEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the Shortcut OAuth client secret. Empty disables OAuth.</summary>
    public string OAuthClientSecretEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the Shortcut OAuth refresh token. Empty disables OAuth.</summary>
    public string OAuthRefreshTokenEnvVar { get; init; } = string.Empty;

    /// <summary>
    /// Public URL Shortcut delivers webhooks to. Shortcut webhooks are created in the
    /// Shortcut UI (Settings → API → Outgoing Webhooks); there is no management API,
    /// so <see cref="ManageWebhooks"/> only validates this URL and logs — it never
    /// registers anything. Deployments without inbound traffic leave webhooks
    /// unconfigured and poll.
    /// </summary>
    public string WebhookUrl { get; init; } = string.Empty;

    /// <summary>
    /// When true the plugin validates <see cref="WebhookUrl"/> at startup and logs
    /// the UI-managed registration expectation. It performs no API calls: Shortcut
    /// exposes no webhook-management endpoint. When false only polling is used.
    /// </summary>
    public bool ManageWebhooks { get; init; }

    /// <summary>
    /// Name of the env var holding the webhook signing secret. Shortcut's outgoing
    /// webhooks carry no signature; set this only when a signing proxy in front of
    /// CodeyBox adds an HMAC-SHA256 hex signature under <see cref="WebhookSignatureHeader"/>.
    /// </summary>
    public string WebhookSecretEnvVar { get; init; } = "SHORTCUT_WEBHOOK_SECRET";

    /// <summary>
    /// Request header carrying the HMAC-SHA256 hex signature when a signing proxy
    /// fronts CodeyBox. Ignored unless the secret env var is set.
    /// </summary>
    public string WebhookSignatureHeader { get; init; } = "X-Shortcut-Signature";

    /// <summary>Shortcut member identities (mention name or email) identifying CodeyBox itself, for loop-guard attribution alongside the marker.</summary>
    public IReadOnlyList<string> ServiceLogins { get; init; } = ["codeybox[bot]"];

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; init; } = 100;

    /// <summary>REST page size per request (1–100, default 50).</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>Upper bound on ingested title/body length (chars) before use.</summary>
    public int MaxIngestedBodyChars { get; init; } = 64 * 1024;

    /// <summary>How long resolved member (assignee) lookups are cached, in minutes (1–1440, default 15).</summary>
    public int MemberCacheMinutes { get; init; } = 15;

    /// <summary>Upper bound on cached member resolutions.</summary>
    internal const int MaxMemberCacheEntries = 1000;

    /// <summary>Builds the operator-configured ingestion signal.</summary>
    public WorkSignal RequiredSignal => new(SignalKind, SignalValue);

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults (disabled features, bounded numbers)
    /// and are surfaced via <paramref name="warnings"/> instead of throwing,
    /// so a bad hot-reload never crashes a poll.
    /// </summary>
    public static ShortcutWorkSyncOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new ShortcutWorkSyncOptions();
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

        return new ShortcutWorkSyncOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ApiBaseUrl = PluginConfigReaders.ReadNonEmpty(section, "ApiBaseUrl", defaults.ApiBaseUrl).TrimEnd('/'),
            TimeoutSeconds = Math.Clamp(PluginConfigReaders.ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds), 1, 300),
            SignalKind = signalKind,
            SignalValue = (section["SignalValue"] ?? defaults.SignalValue).Trim(),
            ProjectMap = PluginConfigReaders.ReadMap(section.GetSection("ProjectMap")),
            StateMapping = PluginConfigReaders.ReadMap(section.GetSection("StateMapping"), StringComparer.Ordinal),
            IngestEpics = PluginConfigReaders.ReadBool(section, "IngestEpics", defaults.IngestEpics),
            TokenEnvVar = PluginConfigReaders.ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            OAuthTokenUrl = PluginConfigReaders.ReadNonEmpty(section, "OAuthTokenUrl", defaults.OAuthTokenUrl),
            OAuthClientIdEnvVar = (section["OAuthClientIdEnvVar"] ?? string.Empty).Trim(),
            OAuthClientSecretEnvVar = (section["OAuthClientSecretEnvVar"] ?? string.Empty).Trim(),
            OAuthRefreshTokenEnvVar = (section["OAuthRefreshTokenEnvVar"] ?? string.Empty).Trim(),
            WebhookUrl = (section["WebhookUrl"] ?? string.Empty).Trim(),
            ManageWebhooks = PluginConfigReaders.ReadBool(section, "ManageWebhooks", defaults.ManageWebhooks),
            WebhookSecretEnvVar = PluginConfigReaders.ReadNonEmpty(section, "WebhookSecretEnvVar", defaults.WebhookSecretEnvVar),
            WebhookSignatureHeader = PluginConfigReaders.ReadNonEmpty(section, "WebhookSignatureHeader", defaults.WebhookSignatureHeader),
            ServiceLogins = PluginConfigReaders.ReadList(section.GetSection("ServiceLogins"), defaults.ServiceLogins),
            MaxItemsPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxItemsPerPoll", defaults.MaxItemsPerPoll), 1, 1000),
            PageSize = Math.Clamp(PluginConfigReaders.ReadInt(section, "PageSize", defaults.PageSize), 1, 100),
            MaxIngestedBodyChars = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxIngestedBodyChars", defaults.MaxIngestedBodyChars), 1024, 256 * 1024),
            MemberCacheMinutes = Math.Clamp(PluginConfigReaders.ReadInt(section, "MemberCacheMinutes", defaults.MemberCacheMinutes), 1, 1440),
        };
    }
}
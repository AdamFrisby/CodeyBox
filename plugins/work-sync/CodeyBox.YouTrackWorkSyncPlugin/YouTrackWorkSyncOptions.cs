using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.YouTrackWorkSyncPlugin;

/// <summary>
/// Operator knobs for the YouTrack work-sync plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.youtrack-worksync</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every poll/post so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="TokenEnvVar"/>,
/// <see cref="OAuthClientSecretEnvVar"/> and <see cref="WebhookSecretEnvVar"/>
/// name environment variables whose values the operator provisions from the
/// host credential chain (vault agent, systemd credentials, container
/// secrets). Only the names are configured.</para>
/// <para>YouTrack's project and field model is per-instance configurable, so
/// the field names that carry the state and assignee signals (<see
/// cref="StateFieldName"/>, <see cref="AssigneeFieldName"/>) are operator
/// declarations, not assumptions.</para>
/// </summary>
public sealed record YouTrackWorkSyncOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.youtrack-worksync";

    /// <summary>Provider namespace recorded in <c>WorkItem.ExternalIds</c>.</summary>
    public const string ProviderNamespace = "youtrack";

    /// <summary>Master switch for this plugin. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// YouTrack base URL (scheme + host, e.g. <c>https://acme.youtrack.cloud</c>
    /// or a self-hosted origin). Must be https — the bearer token and OAuth
    /// client secret ride on these requests, so <c>http://</c> is rejected
    /// unless <see cref="AllowUnsafeHttp"/> is explicitly set. The REST API is
    /// reached at <c>{ApiBaseUrl}/api</c>. Required when <see cref="Enabled"/>
    /// is set — there is deliberately no placeholder default, so enabling the
    /// plugin without an URL fails loudly instead of sending the bearer
    /// credential to an operator-unintended host.
    /// </summary>
    public string ApiBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Dev-only opt-in allowing plaintext <c>http://</c> endpoints (<see
    /// cref="ApiBaseUrl"/>, <see cref="OAuthTokenUrl"/>). Off by default:
    /// without it a cleartext URL fails fast because it would send the
    /// permanent token and OAuth client secret unencrypted.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>Per-request timeout, in seconds (1–300, default 30). Applied per request, so edits hot-reload.</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Upper bound on a single REST response body, in bytes (1 MiB–256 MiB,
    /// default 32 MiB). Enforced before buffering; a larger response fails
    /// the request rather than exhausting memory.
    /// </summary>
    public int MaxResponseBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Which upstream field carries the ingestion signal. Default Assignee (service account).</summary>
    public WorkSignalKind SignalKind { get; init; } = WorkSignalKind.Assignee;

    /// <summary>
    /// Exact value that must be present for ingestion (ordinal-ignore-case exact
    /// match, never substring). For <c>Assignee</c> this is the service-account
    /// <c>login</c> — the only immutable user identifier; display names are
    /// user-editable and never matched. For <c>Label</c> the tag name; for
    /// <c>Status</c> the state value (e.g. <c>To Review</c>).
    /// </summary>
    public string SignalValue { get; init; } = string.Empty;

    /// <summary>
    /// Maps a YouTrack project short name (e.g. <c>PROJ</c>) to the CodeyBox
    /// project id that ingested issues belong to. Issues from unmapped
    /// projects are skipped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Name of the YouTrack custom field carrying the state (default
    /// <c>State</c>). Used in command queries (<c>{field} {value}</c>) and to
    /// match <c>changedFields</c> entries in webhook payloads. YouTrack state
    /// fields are per-project configurable — declare what the instance uses.
    /// </summary>
    public string StateFieldName { get; init; } = "State";

    /// <summary>
    /// Name of the YouTrack custom field carrying the assignee (default
    /// <c>Assignee</c>). Used to read the assignee signal on issues and to
    /// match <c>changedFields</c> entries in webhook payloads.
    /// </summary>
    public string AssigneeFieldName { get; init; } = "Assignee";

    /// <summary>
    /// Name of the environment variable holding the YouTrack permanent token
    /// (<c>perm:…</c>), sent as <c>Authorization: Bearer</c>. The value is
    /// never read from configuration files. Ignored when OAuth is fully
    /// configured.
    /// </summary>
    public string TokenEnvVar { get; init; } = "YOUTRACK_TOKEN";

    /// <summary>
    /// Hub OAuth token endpoint for the client-credentials flow. Empty derives
    /// <c>{ApiBaseUrl}/hub/api/rest/oauth2/token</c>, which covers both
    /// self-hosted and cloud instances (YouTrack Cloud embeds Hub). Only used
    /// when the OAuth env vars are set. Must be https — the client secret is
    /// posted to it — unless <see cref="AllowUnsafeHttp"/> is explicitly set.
    /// </summary>
    public string OAuthTokenUrl { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the Hub OAuth service id (client id). Empty disables OAuth.</summary>
    public string OAuthClientIdEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the Hub OAuth service secret. Empty disables OAuth.</summary>
    public string OAuthClientSecretEnvVar { get; init; } = string.Empty;

    /// <summary>
    /// OAuth scope for the client-credentials grant (the Hub service id of the
    /// YouTrack service, e.g. <c>0-0-0-0-0</c>). Empty omits the scope
    /// parameter.
    /// </summary>
    public string OAuthScope { get; init; } = string.Empty;

    /// <summary>
    /// Name of the HTTP header the YouTrack Webhook Triggers app stamps the
    /// shared webhook token into on every delivery. Default
    /// <c>X-YouTrack-Token</c> — match whatever the operator entered in the
    /// app's settings.
    /// </summary>
    public string WebhookTokenHeader { get; init; } = "X-YouTrack-Token";

    /// <summary>
    /// Name of the env var holding the shared webhook token the Webhook
    /// Triggers app sends (min 32 chars YouTrack-side). Deliveries are
    /// authenticated by comparing the <see cref="WebhookTokenHeader"/> value
    /// against this secret with a constant-time comparison.
    /// </summary>
    public string WebhookSecretEnvVar { get; init; } = "YOUTRACK_WEBHOOK_TOKEN";

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; init; } = 100;

    /// <summary>REST page size per request ($top, 1–500, default 50).</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Maximum pages fetched per project per poll (1–1000, default 100).
    /// Bounds the request stream even when upstream keeps returning full
    /// pages of items that fail to parse — <see cref="MaxItemsPerPoll"/> alone
    /// counts only successfully parsed candidates.
    /// </summary>
    public int MaxPagesPerPoll { get; init; } = 100;

    /// <summary>Upper bound on ingested title/body length (chars) before use.</summary>
    public int MaxIngestedBodyChars { get; init; } = 64 * 1024;

    /// <summary>Builds the operator-configured ingestion signal.</summary>
    public WorkSignal RequiredSignal => new(SignalKind, SignalValue);

    /// <summary>
    /// Resolves the OAuth token endpoint: the explicit
    /// <see cref="OAuthTokenUrl"/> when set, else the embedded-Hub path under
    /// <see cref="ApiBaseUrl"/>.
    /// </summary>
    public string ResolvedOAuthTokenUrl =>
        !string.IsNullOrWhiteSpace(OAuthTokenUrl)
            ? OAuthTokenUrl.Trim()
            : $"{ApiBaseUrl.TrimEnd('/')}/hub/api/rest/oauth2/token";

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults (disabled features, bounded numbers)
    /// and are surfaced via <paramref name="warnings"/> instead of throwing,
    /// so a bad hot-reload never crashes a poll.
    /// </summary>
    public static YouTrackWorkSyncOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new YouTrackWorkSyncOptions();
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

        return new YouTrackWorkSyncOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ApiBaseUrl = PluginConfigReaders.ReadNonEmpty(section, "ApiBaseUrl", defaults.ApiBaseUrl).TrimEnd('/'),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
            TimeoutSeconds = Math.Clamp(PluginConfigReaders.ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds), 1, 300),
            SignalKind = signalKind,
            SignalValue = (section["SignalValue"] ?? defaults.SignalValue).Trim(),
            ProjectMap = PluginConfigReaders.ReadMap(section.GetSection("ProjectMap")),
            StateFieldName = PluginConfigReaders.ReadNonEmpty(section, "StateFieldName", defaults.StateFieldName),
            AssigneeFieldName = PluginConfigReaders.ReadNonEmpty(section, "AssigneeFieldName", defaults.AssigneeFieldName),
            TokenEnvVar = PluginConfigReaders.ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            OAuthTokenUrl = (section["OAuthTokenUrl"] ?? defaults.OAuthTokenUrl).Trim(),
            OAuthClientIdEnvVar = (section["OAuthClientIdEnvVar"] ?? string.Empty).Trim(),
            OAuthClientSecretEnvVar = (section["OAuthClientSecretEnvVar"] ?? string.Empty).Trim(),
            OAuthScope = (section["OAuthScope"] ?? string.Empty).Trim(),
            WebhookTokenHeader = PluginConfigReaders.ReadNonEmpty(section, "WebhookTokenHeader", defaults.WebhookTokenHeader),
            WebhookSecretEnvVar = PluginConfigReaders.ReadNonEmpty(section, "WebhookSecretEnvVar", defaults.WebhookSecretEnvVar),
            MaxResponseBytes = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxResponseBytes", defaults.MaxResponseBytes), 1024 * 1024, 256 * 1024 * 1024),
            MaxItemsPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxItemsPerPoll", defaults.MaxItemsPerPoll), 1, 1000),
            PageSize = Math.Clamp(PluginConfigReaders.ReadInt(section, "PageSize", defaults.PageSize), 1, 500),
            MaxPagesPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxPagesPerPoll", defaults.MaxPagesPerPoll), 1, 1000),
            MaxIngestedBodyChars = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxIngestedBodyChars", defaults.MaxIngestedBodyChars), 1024, 256 * 1024),
        };
    }
}

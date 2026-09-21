using System.Globalization;
using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.PlaneWorkSyncPlugin;

/// <summary>
/// Operator knobs for the Plane work-sync plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.plane-worksync</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every poll/post so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="TokenEnvVar"/>,
/// <see cref="OAuthClientSecretEnvVar"/>, <see cref="OAuthRefreshTokenEnvVar"/>
/// and <see cref="WebhookSecretEnvVar"/> name environment variables whose
/// values the operator provisions from the host credential chain (vault agent,
/// systemd credentials, container secrets). Only the names are configured.</para>
/// </summary>
public sealed record PlaneWorkSyncOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.plane-worksync";

    /// <summary>Provider namespace recorded in <c>WorkItem.ExternalIds</c>.</summary>
    public const string ProviderNamespace = "plane";

    /// <summary>Master switch for this plugin. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Plane API base URL (scheme + host, no trailing path). Defaults to Plane Cloud.
    /// Self-hosted instances set this to their own origin. Must be http(s).
    /// </summary>
    public string ApiBaseUrl { get; init; } = "https://api.plane.so";

    /// <summary>
    /// Plane workspace slug owning the mapped projects. Required for every API call.
    /// </summary>
    public string WorkspaceSlug { get; init; } = string.Empty;

    /// <summary>Which upstream field carries the ingestion signal. Default Label.</summary>
    public WorkSignalKind SignalKind { get; init; } = WorkSignalKind.Label;

    /// <summary>
    /// Exact value that must be present for ingestion (ordinal-ignore-case exact
    /// match, never substring). For <c>Label</c> the label name (e.g. <c>codeybox</c>);
    /// for <c>Assignee</c> the service/bot account user id (preferred) or email;
    /// for <c>Status</c> the state name (e.g. <c>Triage</c>) or state group
    /// (e.g. <c>unstarted</c>).
    /// </summary>
    public string SignalValue { get; init; } = string.Empty;

    /// <summary>
    /// Maps a Plane project id (UUID) to the CodeyBox project id that ingested
    /// work items belong to. Issues from unmapped projects are skipped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit CodeyBox-state-name to Plane-state-name declaration
    /// (e.g. <c>{ "Working": "In Progress", "Done": "Completed" }</c>), parsed by
    /// <see cref="WorkStateMapping.Parse"/>. A state with no entry is reported
    /// as unmapped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> StateMapping { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Name of the environment variable holding the Plane personal access token.
    /// Sent as <c>X-API-Key</c>. The value is never read from configuration files.
    /// </summary>
    public string TokenEnvVar { get; init; } = "PLANE_API_KEY";

    /// <summary>Plane OAuth token endpoint (https). Only used when OAuth env vars are set.</summary>
    public string OAuthTokenUrl { get; init; } = "https://api.plane.so/api/v1/oauth/token/";

    /// <summary>Name of the env var holding the Plane OAuth client id. Empty disables OAuth.</summary>
    public string OAuthClientIdEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the Plane OAuth client secret. Empty disables OAuth.</summary>
    public string OAuthClientSecretEnvVar { get; init; } = string.Empty;

    /// <summary>Name of the env var holding the Plane OAuth refresh token. Empty disables OAuth.</summary>
    public string OAuthRefreshTokenEnvVar { get; init; } = string.Empty;

    /// <summary>Public URL Plane delivers webhooks to. Required when <see cref="ManageWebhooks"/> is true.</summary>
    public string WebhookUrl { get; init; } = string.Empty;

    /// <summary>
    /// When true the plugin attempts to register its webhook with Plane at startup
    /// and removes it on disposal. Plane webhooks are workspace-level and normally
    /// created in the UI; the REST lifecycle is best-effort and degrades to
    /// polling when the instance lacks the capability. Deployments without
    /// inbound traffic leave this off and poll.
    /// </summary>
    public bool ManageWebhooks { get; init; }

    /// <summary>Title for the managed webhook registration.</summary>
    public string WebhookTitle { get; init; } = "CodeyBox work sync";

    /// <summary>Name of the env var holding the Plane webhook signing secret.</summary>
    public string WebhookSecretEnvVar { get; init; } = "PLANE_WEBHOOK_SECRET";

    /// <summary>Plane user logins identifying CodeyBox itself, for loop-guard attribution alongside the marker.</summary>
    public IReadOnlyList<string> ServiceLogins { get; init; } = ["codeybox[bot]"];

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; init; } = 100;

    /// <summary>REST page size per request (1–100, default 50).</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>Per-request timeout, in seconds (1–300, default 30).</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Upper bound on ingested title/body length (chars) before use.</summary>
    public int MaxIngestedBodyChars { get; init; } = 64 * 1024;

    /// <summary>
    /// Issue-collection path segment under a project. Plane renamed
    /// <c>issues</c> to <c>work-items</c> in newer releases; the default covers
    /// current self-hosted versions and the client falls back to the other
    /// spelling on 404 so mixed-version fleets keep working.
    /// </summary>
    public string IssuesPath { get; init; } = "issues";

    /// <summary>Maximum pages scanned when resolving a human key to an issue UUID.</summary>
    public int MaxResolvePages { get; init; } = 5;

    /// <summary>Upper bound on cached project-identifier and issue-uuid resolutions.</summary>
    internal const int MaxIdCacheEntries = 1000;

    /// <summary>Builds the operator-configured ingestion signal.</summary>
    public WorkSignal RequiredSignal => new(SignalKind, SignalValue);

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults (disabled features, bounded numbers)
    /// and are surfaced via <paramref name="warnings"/> instead of throwing,
    /// so a bad hot-reload never crashes a poll.
    /// </summary>
    public static PlaneWorkSyncOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new PlaneWorkSyncOptions();
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

        return new PlaneWorkSyncOptions
        {
            Enabled = ReadBool(section, "Enabled", defaults.Enabled),
            ApiBaseUrl = ReadNonEmpty(section, "ApiBaseUrl", defaults.ApiBaseUrl).TrimEnd('/'),
            WorkspaceSlug = (section["WorkspaceSlug"] ?? defaults.WorkspaceSlug).Trim(),
            SignalKind = signalKind,
            SignalValue = (section["SignalValue"] ?? defaults.SignalValue).Trim(),
            ProjectMap = ReadMap(section.GetSection("ProjectMap")),
            StateMapping = ReadMap(section.GetSection("StateMapping"), StringComparer.Ordinal),
            TokenEnvVar = ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            OAuthTokenUrl = ReadNonEmpty(section, "OAuthTokenUrl", defaults.OAuthTokenUrl),
            OAuthClientIdEnvVar = (section["OAuthClientIdEnvVar"] ?? string.Empty).Trim(),
            OAuthClientSecretEnvVar = (section["OAuthClientSecretEnvVar"] ?? string.Empty).Trim(),
            OAuthRefreshTokenEnvVar = (section["OAuthRefreshTokenEnvVar"] ?? string.Empty).Trim(),
            WebhookUrl = (section["WebhookUrl"] ?? string.Empty).Trim(),
            ManageWebhooks = ReadBool(section, "ManageWebhooks", defaults.ManageWebhooks),
            WebhookTitle = ReadNonEmpty(section, "WebhookTitle", defaults.WebhookTitle),
            WebhookSecretEnvVar = ReadNonEmpty(section, "WebhookSecretEnvVar", defaults.WebhookSecretEnvVar),
            ServiceLogins = ReadList(section.GetSection("ServiceLogins"), defaults.ServiceLogins),
            MaxItemsPerPoll = Math.Clamp(ReadInt(section, "MaxItemsPerPoll", defaults.MaxItemsPerPoll), 1, 1000),
            PageSize = Math.Clamp(ReadInt(section, "PageSize", defaults.PageSize), 1, 100),
            TimeoutSeconds = Math.Clamp(ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds), 1, 300),
            MaxIngestedBodyChars = Math.Clamp(ReadInt(section, "MaxIngestedBodyChars", defaults.MaxIngestedBodyChars), 1024, 256 * 1024),
            IssuesPath = ReadNonEmpty(section, "IssuesPath", defaults.IssuesPath).Trim().Trim('/'),
            MaxResolvePages = Math.Clamp(ReadInt(section, "MaxResolvePages", defaults.MaxResolvePages), 1, 20),
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

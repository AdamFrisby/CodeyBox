using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.OpenProjectWorkSyncPlugin;

/// <summary>
/// Operator knobs for the OpenProject work-sync plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.openproject-worksync</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every poll/post so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="TokenEnvVar"/> names an
/// environment variable whose value the operator provisions from the host
/// credential chain (vault agent, systemd credentials, container secrets).
/// Only the name is configured.</para>
/// <para>OpenProject work packages carry no labels or tags, so the ingestion
/// signal is an assignment to the service account (its immutable numeric user
/// id) or a status name (admin-defined per instance). A label-kind signal is
/// configuration without meaning here and never matches — the poll warns
/// about it rather than silently ingesting nothing.</para>
/// </summary>
public sealed record OpenProjectWorkSyncOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.openproject-worksync";

    /// <summary>Provider namespace recorded in <c>WorkItem.ExternalIds</c>.</summary>
    public const string ProviderNamespace = "openproject";

    /// <summary>
    /// Oldest OpenProject release the adapter is pinned against. The plugin
    /// speaks REST API v3 only (HAL+JSON work packages, <c>lockVersion</c>
    /// optimistic locking, activity comments); instances older than this are
    /// unsupported.
    /// </summary>
    public const string MinSupportedVersion = "13.0";

    /// <summary>Master switch for this plugin. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// OpenProject base URL (scheme + host, e.g.
    /// <c>https://openproject.example.com</c>). Must be https — the API token
    /// rides on these requests, so <c>http://</c> is rejected unless
    /// <see cref="AllowUnsafeHttp"/> is explicitly set. The REST API is reached
    /// at <c>{ApiBaseUrl}/api/v3</c>. Required when <see cref="Enabled"/> is
    /// set — there is deliberately no placeholder default, so enabling the
    /// plugin without an URL fails loudly instead of sending the credential
    /// to an operator-unintended host.
    /// </summary>
    public string ApiBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Dev-only opt-in allowing plaintext <c>http://</c> endpoints. Off by
    /// default: without it a cleartext URL fails fast because it would send
    /// the API token unencrypted.
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

    /// <summary>Which upstream field carries the ingestion signal. Default Assignee (service account user id).</summary>
    public WorkSignalKind SignalKind { get; init; } = WorkSignalKind.Assignee;

    /// <summary>
    /// Exact value that must be present for ingestion (ordinal-ignore-case exact
    /// match, never substring). For <c>Assignee</c> this is the service
    /// account's numeric user id (e.g. <c>42</c> — the immutable identifier;
    /// display names are user-editable and never matched). For
    /// <c>Status</c> the status name (e.g. <c>In progress</c>). For
    /// <c>Label</c> there is no OpenProject field — the signal never matches.
    /// </summary>
    public string SignalValue { get; init; } = string.Empty;

    /// <summary>
    /// Maps an OpenProject project key (numeric project id or identifier slug,
    /// e.g. <c>42</c> or <c>my-project</c>) to the CodeyBox project id that
    /// ingested work packages belong to. Packages from unmapped projects are
    /// skipped, never guessed. One plugin instance serves one OpenProject
    /// host, so numeric work-package ids are stable within the mapping.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Name of the environment variable holding the OpenProject API token,
    /// sent as <c>Authorization: Bearer</c> per the official API introduction.
    /// The value is never read from configuration files.
    /// </summary>
    public string TokenEnvVar { get; init; } = "OPENPROJECT_TOKEN";

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; init; } = 100;

    /// <summary>REST page size per request (1–500, default 50).</summary>
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

    /// <summary>
    /// Maximum activities scanned when checking for an already-posted comment
    /// (idempotency reconcile) or question tag (10–5000, default 200).
    /// Newest activities are scanned first.
    /// </summary>
    public int MaxActivitiesScanned { get; init; } = 200;

    /// <summary>Maximum retries on HTTP 429/503 rate limiting (0–10, default 3).</summary>
    public int MaxRateLimitRetries { get; init; } = 3;

    /// <summary>Upper bound on the delay honoured from Retry-After, in seconds (1–300, default 30).</summary>
    public int MaxRateLimitDelaySeconds { get; init; } = 30;

    /// <summary>Builds the operator-configured ingestion signal.</summary>
    public WorkSignal RequiredSignal => new(SignalKind, SignalValue);

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults (disabled features, bounded numbers)
    /// and are surfaced via <paramref name="warnings"/> instead of throwing,
    /// so a bad hot-reload never crashes a poll.
    /// </summary>
    public static OpenProjectWorkSyncOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new OpenProjectWorkSyncOptions();
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

        return new OpenProjectWorkSyncOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ApiBaseUrl = PluginConfigReaders.ReadNonEmpty(section, "ApiBaseUrl", defaults.ApiBaseUrl).TrimEnd('/'),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
            TimeoutSeconds = Math.Clamp(PluginConfigReaders.ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds), 1, 300),
            SignalKind = signalKind,
            SignalValue = (section["SignalValue"] ?? defaults.SignalValue).Trim(),
            ProjectMap = PluginConfigReaders.ReadMap(section.GetSection("ProjectMap")),
            TokenEnvVar = PluginConfigReaders.ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            MaxResponseBytes = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxResponseBytes", defaults.MaxResponseBytes), 1024 * 1024, 256 * 1024 * 1024),
            MaxItemsPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxItemsPerPoll", defaults.MaxItemsPerPoll), 1, 1000),
            PageSize = Math.Clamp(PluginConfigReaders.ReadInt(section, "PageSize", defaults.PageSize), 1, 500),
            MaxPagesPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxPagesPerPoll", defaults.MaxPagesPerPoll), 1, 1000),
            MaxIngestedBodyChars = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxIngestedBodyChars", defaults.MaxIngestedBodyChars), 1024, 256 * 1024),
            MaxActivitiesScanned = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxActivitiesScanned", defaults.MaxActivitiesScanned), 10, 5000),
            MaxRateLimitRetries = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxRateLimitRetries", defaults.MaxRateLimitRetries), 0, 10),
            MaxRateLimitDelaySeconds = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxRateLimitDelaySeconds", defaults.MaxRateLimitDelaySeconds), 1, 300),
        };
    }
}

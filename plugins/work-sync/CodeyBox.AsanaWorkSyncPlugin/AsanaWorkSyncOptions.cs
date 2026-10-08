using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.AsanaWorkSyncPlugin;

/// <summary>
/// Operator knobs for the Asana work-sync plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.asana-worksync</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every poll/post so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="TokenEnvVar"/> names the
/// environment variable holding the Asana personal access token (or OAuth
/// access token); the value is provisioned from the host credential chain
/// (vault agent, systemd credentials, container secrets). Only the name is
/// configured.</para>
/// <para>Asana identifies every resource by an immutable numeric GID. Project
/// mapping keys are project GIDs (not names, which are editable); the
/// ingestion key is the task GID.</para>
/// </summary>
public sealed record AsanaWorkSyncOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.asana-worksync";

    /// <summary>Provider namespace recorded in <c>WorkItem.ExternalIds</c>.</summary>
    public const string ProviderNamespace = "asana";

    /// <summary>
    /// Pinned Asana REST API surface this plugin speaks: <c>/api/1.0</c> on
    /// <c>https://app.asana.com</c> (verified against
    /// <c>https://developers.asana.com/reference/gettasks</c>). Authentication
    /// is <c>Authorization: Bearer</c> for both personal access tokens and
    /// OAuth access tokens.
    /// </summary>
    public const string ApiVersion = "1.0";

    /// <summary>Asana's maximum REST page size; bounds <see cref="PageSize"/> and <see cref="DedupScanLimit"/>.</summary>
    internal const int MaxApiPageSize = 100;

    /// <summary>Master switch for this plugin. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Asana API origin including the version path (default
    /// <c>https://app.asana.com/api/1.0</c>, composed from <see
    /// cref="ApiVersion"/> so the two cannot drift). Must be https — the
    /// bearer token rides on these requests, so <c>http://</c> is rejected
    /// unless <see cref="AllowUnsafeHttp"/> is explicitly set AND the host is
    /// loopback. Required when <see cref="Enabled"/> is set — there is
    /// deliberately no placeholder default beyond the public cloud endpoint,
    /// so a custom origin is always an explicit operator choice.
    /// </summary>
    public string ApiBaseUrl { get; init; } = "https://app.asana.com/api/" + ApiVersion;

    /// <summary>
    /// Dev-only opt-in allowing plaintext <c>http://</c> origins (<see
    /// cref="ApiBaseUrl"/>), and only for loopback hosts — the opt-in can
    /// never send the bearer token off-box in cleartext. Off by default:
    /// without it a cleartext URL fails fast because it would send the
    /// bearer token unencrypted.
    /// </summary>
    public bool AllowUnsafeHttp { get; init; }

    /// <summary>Per-request timeout, in seconds (1–300, default 30). Applied per attempt, so edits hot-reload.</summary>
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
    /// match, never substring). For <c>Assignee</c> prefer the assignee's
    /// immutable user <c>gid</c>; a display <c>name</c> also matches exactly
    /// but is user-editable. For <c>Label</c> the tag gid (preferred) or tag
    /// name; for <c>Status</c> a section name (e.g. <c>Ready for CodeyBox</c>)
    /// or <c>completed</c>. A name-valued signal delegates the ingestion
    /// gate to whoever can rename that upstream user, tag, or section —
    /// GID values are not spoofable that way and are always preferred.
    /// </summary>
    public string SignalValue { get; init; } = string.Empty;

    /// <summary>
    /// Maps an Asana project <c>gid</c> (numeric, immutable) to the CodeyBox
    /// project id that ingested tasks belong to. Tasks from unmapped projects
    /// are skipped, never guessed. Non-numeric keys are misconfiguration and
    /// are skipped loudly at poll time.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Explicit operator declaration mapping a caller-resolved external status
    /// (from the host <c>CodeyBox:WorkSync:StateMapping</c>) to an Asana
    /// custom-field write in <c>fieldGid:enumGid</c> form (both numeric GIDs).
    /// A terminal status with no entry here and no other recognised meaning is
    /// reported <c>UnmappedState</c>, never guessed. Custom fields are the only
    /// field writes this plugin performs, and only through this map.
    /// </summary>
    public IReadOnlyDictionary<string, string> StatusCustomFieldMap { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Name of the environment variable holding the Asana personal access token
    /// (primary) or OAuth access token. Sent as <c>Authorization:
    /// Bearer</c>. The value is never read from configuration files. Asana
    /// offers no client-credentials grant, so there is no in-process OAuth
    /// refresh: rotate by updating the host environment.
    /// </summary>
    public string TokenEnvVar { get; init; } = "ASANA_TOKEN";

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; init; } = 100;

    /// <summary>REST page size per request (<c>limit</c>, 1–100, default 50 — the API maximum is 100).</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Maximum pages fetched per project per poll (1–1000, default 100).
    /// Bounds the request stream even when upstream keeps returning
    /// non-terminating pages — <see cref="MaxItemsPerPoll"/> alone counts
    /// only successfully parsed candidates.
    /// </summary>
    public int MaxPagesPerPoll { get; init; } = 100;

    /// <summary>
    /// Only tasks modified within this many hours are polled
    /// (<c>modified_since</c>, 0–8760, default 168). Zero disables the filter;
    /// the paging caps still bound the walk.
    /// </summary>
    public int ModifiedSinceHours { get; init; } = 168;

    /// <summary>Maximum retry attempts after the first try on 429/5xx (0–10, default 3). Zero disables retries.</summary>
    public int RetryMaxAttempts { get; init; } = 3;

    /// <summary>Base delay between retries, in milliseconds (0–30000, default 500). Doubled per attempt.</summary>
    public int RetryBaseDelayMs { get; init; } = 500;

    /// <summary>Upper bound on any single retry delay, in seconds (1–300, default 30). Caps backoff and Retry-After.</summary>
    public int RetryMaxDelaySeconds { get; init; } = 30;

    /// <summary>Maximum recent stories scanned for duplicate comments (1–100, default 50 — one API page).</summary>
    public int DedupScanLimit { get; init; } = 50;

    /// <summary>Upper bound on ingested title/body length (chars) before use.</summary>
    public int MaxIngestedBodyChars { get; init; } = 64 * 1024;

    /// <summary>
    /// Builds the operator-configured ingestion signal.
    /// </summary>
    public WorkSignal RequiredSignal => new(SignalKind, SignalValue);

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults (disabled features, bounded numbers)
    /// and are surfaced via <paramref name="warnings"/> instead of throwing,
    /// so a bad hot-reload never crashes a poll.
    /// </summary>
    public static AsanaWorkSyncOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new AsanaWorkSyncOptions();
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

        return new AsanaWorkSyncOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ApiBaseUrl = PluginConfigReaders.ReadNonEmpty(section, "ApiBaseUrl", defaults.ApiBaseUrl).TrimEnd('/'),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
            TimeoutSeconds = Math.Clamp(PluginConfigReaders.ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds), 1, 300),
            SignalKind = signalKind,
            SignalValue = (section["SignalValue"] ?? defaults.SignalValue).Trim(),
            ProjectMap = PluginConfigReaders.ReadMap(section.GetSection("ProjectMap")),
            StatusCustomFieldMap = PluginConfigReaders.ReadMap(
                section.GetSection("StatusCustomFieldMap"), StringComparer.Ordinal),
            TokenEnvVar = PluginConfigReaders.ReadNonEmpty(section, "TokenEnvVar", defaults.TokenEnvVar),
            MaxResponseBytes = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxResponseBytes", defaults.MaxResponseBytes), 1024 * 1024, 256 * 1024 * 1024),
            MaxItemsPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxItemsPerPoll", defaults.MaxItemsPerPoll), 1, 1000),
            PageSize = Math.Clamp(PluginConfigReaders.ReadInt(section, "PageSize", defaults.PageSize), 1, MaxApiPageSize),
            MaxPagesPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxPagesPerPoll", defaults.MaxPagesPerPoll), 1, 1000),
            ModifiedSinceHours = Math.Clamp(PluginConfigReaders.ReadInt(section, "ModifiedSinceHours", defaults.ModifiedSinceHours), 0, 8760),
            RetryMaxAttempts = Math.Clamp(PluginConfigReaders.ReadInt(section, "RetryMaxAttempts", defaults.RetryMaxAttempts), 0, 10),
            RetryBaseDelayMs = Math.Clamp(PluginConfigReaders.ReadInt(section, "RetryBaseDelayMs", defaults.RetryBaseDelayMs), 0, 30000),
            RetryMaxDelaySeconds = Math.Clamp(PluginConfigReaders.ReadInt(section, "RetryMaxDelaySeconds", defaults.RetryMaxDelaySeconds), 1, 300),
            DedupScanLimit = Math.Clamp(PluginConfigReaders.ReadInt(section, "DedupScanLimit", defaults.DedupScanLimit), 1, MaxApiPageSize),
            MaxIngestedBodyChars = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxIngestedBodyChars", defaults.MaxIngestedBodyChars), 1024, 256 * 1024),
        };
    }

    /// <summary>
    /// Parses an explicit <see cref="StatusCustomFieldMap"/> value in
    /// <c>fieldGid:enumGid</c> form. Returns false (never throws for shape)
    /// when the value is not two numeric GIDs.
    /// </summary>
    internal static bool TryParseCustomFieldMapping(
        string mapping, out string fieldGid, out string enumGid)
    {
        fieldGid = string.Empty;
        enumGid = string.Empty;
        if (string.IsNullOrWhiteSpace(mapping))
            return false;
        var parts = mapping.Split(':');
        if (parts.Length != 2)
            return false;
        var field = parts[0].Trim();
        var option = parts[1].Trim();
        if (!AsanaGids.IsGid(field) || !AsanaGids.IsGid(option))
            return false;
        fieldGid = field;
        enumGid = option;
        return true;
    }
}

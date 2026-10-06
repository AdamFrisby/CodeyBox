using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.RedmineWorkSyncPlugin;

/// <summary>
/// Operator knobs for the Redmine work-sync plugin, bound from
/// <c>CodeyBox:Plugins:codeybox.redmine-worksync</c>. Every operational value
/// lives here — never as a literal in source — and the section is re-read on
/// every poll/post so edits take effect without a host restart.
/// <para>Secrets never appear here: <see cref="ApiKeyEnvVar"/> names the
/// environment variable holding the Redmine API key, provisioned from the
/// host credential chain (vault agent, systemd credentials, container
/// secrets). Only the name is configured.</para>
/// <para>Redmine projects are keyed by their URL-safe <c>identifier</c> (not
/// the display name, which is editable); statuses are matched by exact name
/// against <c>GET /issue_statuses.json</c> at post time, so workflow renames
/// fail loudly instead of writing a guessed state.</para>
/// </summary>
public sealed record RedmineWorkSyncOptions
{
    /// <summary>Plugin ID used in <c>CodeyBox:Plugins:&lt;id&gt;</c>.</summary>
    public const string PluginId = "codeybox.redmine-worksync";

    /// <summary>Provider namespace recorded in <c>WorkItem.ExternalIds</c>.</summary>
    public const string ProviderNamespace = "redmine";

    /// <summary>Master switch for this plugin. Default false: off unless an operator enables it.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Redmine base URL (scheme + host, e.g. <c>https://redmine.example.com</c>).
    /// Must be https — the API key rides on these requests, so <c>http://</c>
    /// is rejected unless <see cref="AllowUnsafeHttp"/> is explicitly set.
    /// Required when <see cref="Enabled"/> is set — there is deliberately no
    /// placeholder default, so enabling the plugin without a URL fails loudly
    /// instead of sending the API key to an operator-unintended host. Use the
    /// canonical URL (no redirect): redirect responses are refused rather
    /// than followed with credentials.
    /// </summary>
    public string ApiBaseUrl { get; init; } = string.Empty;

    /// <summary>
    /// Dev-only opt-in allowing plaintext <c>http://</c> endpoints. Off by
    /// default: without it a cleartext URL fails fast because it would send
    /// the API key unencrypted.
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

    /// <summary>Which upstream field carries the ingestion signal. Default Status.</summary>
    public WorkSignalKind SignalKind { get; init; } = WorkSignalKind.Status;

    /// <summary>
    /// Exact value that must be present for ingestion (ordinal-ignore-case
    /// exact match, never substring). For <c>Status</c> the issue status
    /// name (e.g. <c>Ready for CodeyBox</c>); for <c>Assignee</c> the
    /// assignee display name; for <c>Label</c> a custom-field value (Redmine
    /// has no native labels — every scalar custom-field value is surfaced
    /// as a label-kind signal).
    /// </summary>
    public string SignalValue { get; init; } = string.Empty;

    /// <summary>
    /// Maps a Redmine project <c>identifier</c> (the URL-safe key, e.g.
    /// <c>my-app</c>) to the CodeyBox project id that ingested issues belong
    /// to. Issues from unmapped projects are skipped, never guessed.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProjectMap { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Name of the environment variable holding the Redmine API key, sent as
    /// <c>X-Redmine-API-Key</c> (header — never a <c>?key=</c> query string,
    /// which would leak it into access logs). The value is never read from
    /// configuration files.
    /// </summary>
    public string ApiKeyEnvVar { get; init; } = "REDMINE_API_KEY";

    /// <summary>Maximum external items accepted from a single poll. Enforced before buffering.</summary>
    public int MaxItemsPerPoll { get; init; } = 100;

    /// <summary>REST page size per request (limit, 1–100 — Redmine caps limit at 100; default 50).</summary>
    public int PageSize { get; init; } = 50;

    /// <summary>
    /// Maximum pages fetched per project per poll (1–1000, default 100).
    /// Bounds the request stream even when upstream keeps returning full
    /// pages of items that fail to parse — <see cref="MaxItemsPerPoll"/>
    /// alone counts only successfully parsed candidates.
    /// </summary>
    public int MaxPagesPerPoll { get; init; } = 100;

    /// <summary>Upper bound on ingested title/body length (chars) before use.</summary>
    public int MaxIngestedBodyChars { get; init; } = 64 * 1024;

    /// <summary>
    /// Maximum attempts per idempotent (GET) request (1–5, default 3).
    /// Retries cover 429/502/503/504 with the upstream <c>Retry-After</c>
    /// hint honored; other statuses fail fast. PUTs are never retried — a
    /// note write of uncertain outcome reconciles the issue journal before
    /// any repetition, so a retried note cannot duplicate.
    /// </summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Base delay between retries, in milliseconds (0–10 000, default 500; doubled per attempt).</summary>
    public int RetryBaseDelayMs { get; init; } = 500;

    /// <summary>Builds the operator-configured ingestion signal.</summary>
    public WorkSignal RequiredSignal => new(SignalKind, SignalValue);

    /// <summary>
    /// Binds options from the plugin's scoped configuration section. Invalid
    /// values fall back to safe defaults (disabled features, bounded numbers)
    /// and are surfaced via <paramref name="warnings"/> instead of throwing,
    /// so a bad hot-reload never crashes a poll.
    /// </summary>
    public static RedmineWorkSyncOptions FromConfiguration(
        IConfigurationSection section, List<string>? warnings = null)
    {
        var defaults = new RedmineWorkSyncOptions();
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

        return new RedmineWorkSyncOptions
        {
            Enabled = PluginConfigReaders.ReadBool(section, "Enabled", defaults.Enabled),
            ApiBaseUrl = PluginConfigReaders.ReadNonEmpty(section, "ApiBaseUrl", defaults.ApiBaseUrl).TrimEnd('/'),
            AllowUnsafeHttp = PluginConfigReaders.ReadBool(section, "AllowUnsafeHttp", defaults.AllowUnsafeHttp),
            TimeoutSeconds = Math.Clamp(PluginConfigReaders.ReadInt(section, "TimeoutSeconds", defaults.TimeoutSeconds), 1, 300),
            SignalKind = signalKind,
            SignalValue = (section["SignalValue"] ?? defaults.SignalValue).Trim(),
            ProjectMap = PluginConfigReaders.ReadMap(section.GetSection("ProjectMap")),
            ApiKeyEnvVar = PluginConfigReaders.ReadNonEmpty(section, "ApiKeyEnvVar", defaults.ApiKeyEnvVar),
            MaxResponseBytes = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxResponseBytes", defaults.MaxResponseBytes), 1024 * 1024, 256 * 1024 * 1024),
            MaxItemsPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxItemsPerPoll", defaults.MaxItemsPerPoll), 1, 1000),
            PageSize = Math.Clamp(PluginConfigReaders.ReadInt(section, "PageSize", defaults.PageSize), 1, 100),
            MaxPagesPerPoll = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxPagesPerPoll", defaults.MaxPagesPerPoll), 1, 1000),
            MaxIngestedBodyChars = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxIngestedBodyChars", defaults.MaxIngestedBodyChars), 1024, 256 * 1024),
            MaxAttempts = Math.Clamp(PluginConfigReaders.ReadInt(section, "MaxAttempts", defaults.MaxAttempts), 1, 5),
            RetryBaseDelayMs = Math.Clamp(PluginConfigReaders.ReadInt(section, "RetryBaseDelayMs", defaults.RetryBaseDelayMs), 0, 10000),
        };
    }
}

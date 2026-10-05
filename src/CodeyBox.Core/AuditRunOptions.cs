using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeyBox.Core;

/// <summary>
/// Hot-reloadable operator knobs for standalone audit runs. Disabled by
/// default: queue implementation only — nothing executes on the live host
/// until the operator explicitly enables it and configures a supported
/// sandbox provider with real installed tools.
/// </summary>
public sealed class AuditRunOptions
{
    public const string SectionName = "CodeyBox:AuditRuns";

    /// <summary>Master switch. Default false.</summary>
    public bool Enabled { get; set; }

    /// <summary>Max concurrently executing runs. Default 2.</summary>
    public int MaxConcurrentRuns { get; set; } = 2;

    /// <summary>Max queued (non-terminal) runs. Default 50.</summary>
    public int MaxQueuedRuns { get; set; } = 50;

    /// <summary>Per-auditor wall-clock timeout in seconds. Default 600.</summary>
    public int PerAuditorTimeoutSeconds { get; set; } = 600;

    /// <summary>Total run timeout in seconds. Default 3600.</summary>
    public int TotalRunTimeoutSeconds { get; set; } = 3600;

    /// <summary>Max captured log chars per auditor (pre-redaction bound is applied before buffering). Default 200_000.</summary>
    public int MaxLogCharsPerAuditor { get; set; } = 200_000;

    /// <summary>Max artifact bytes per artifact. Default 5 MiB.</summary>
    public long MaxArtifactBytes { get; set; } = 5L * 1024 * 1024;

    /// <summary>Max artifacts per run. Default 16.</summary>
    public int MaxArtifactsPerRun { get; set; } = 16;

    /// <summary>Max raw-output chars persisted per auditor report (mirrors IAuditReportStore cap). Default 256 KiB chars.</summary>
    public int MaxRawOutputChars { get; set; } = 256 * 1024;

    /// <summary>Retention days for completed runs and their artifacts. Default 30.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Max attempts per auditor (bounded retries). Default 2.</summary>
    public int MaxAttemptsPerAuditor { get; set; } = 2;

    /// <summary>
    /// Named standalone profiles mapping to explicit auditor IDs. A request
    /// selecting a profile resolves to exactly these IDs; unknown names are
    /// rejected rather than falling back to the project's default panel.
    /// Empty by default — explicit auditor IDs are then the only mechanism.
    /// </summary>
    public Dictionary<string, List<string>> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsValid(AuditRunOptions opts) =>
        opts.MaxConcurrentRuns is >= 1 and <= 32
        && opts.MaxQueuedRuns is >= 1 and <= 1000
        && opts.PerAuditorTimeoutSeconds is >= 10 and <= 7200
        && opts.TotalRunTimeoutSeconds is >= 60 and <= 86400
        && opts.MaxLogCharsPerAuditor is >= 1000 and <= 2_000_000
        && opts.MaxArtifactBytes is >= 1024 and <= 100L * 1024 * 1024
        && opts.MaxArtifactsPerRun is >= 1 and <= 128
        && opts.MaxRawOutputChars is >= 1024 and <= 1024 * 1024
        && opts.RetentionDays is >= 1 and <= 365
        && opts.MaxAttemptsPerAuditor is >= 1 and <= 5;

    /// <summary>Stable digest of the resolved config frozen into provenance.</summary>
    public static string ComputeConfigDigest(AuditRunOptions opts, IReadOnlyList<string> auditorIds)
    {
        var payload = JsonSerializer.Serialize(new
        {
            opts.MaxConcurrentRuns,
            opts.PerAuditorTimeoutSeconds,
            opts.TotalRunTimeoutSeconds,
            opts.MaxLogCharsPerAuditor,
            opts.MaxArtifactBytes,
            opts.MaxArtifactsPerRun,
            Auditors = auditorIds.OrderBy(a => a, StringComparer.Ordinal).ToArray(),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)))[..16].ToLowerInvariant();
    }
}

/// <summary>
/// Pure validation for standalone audit-run selection and refs.
/// </summary>
public static class AuditRunValidation
{
    public const int MaxRefLength = 256;
    public const int MaxAuditorsPerRun = 32;

    /// <summary>
    /// Accepts full 40-hex SHAs directly. Anything else is invalid for the
    /// built-in resolver; a richer resolver (branch -&gt; SHA via the project
    /// repository policy) can be plugged in behind
    /// <c>IStandaloneAuditRefResolver</c>.
    /// </summary>
    public static bool IsExplicitSha(string? value) =>
        value is not null
        && value.Length == 40
        && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

    public static string? ValidateRef(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return $"{fieldName} is required.";
        var trimmed = value.Trim();
        if (trimmed.Length > MaxRefLength)
            return $"{fieldName} exceeds {MaxRefLength} characters.";
        if (trimmed.Contains("..", StringComparison.Ordinal)
            || trimmed.StartsWith('/') || trimmed.StartsWith('-')
            || trimmed.Any(c => char.IsWhiteSpace(c) || c is ';' or '&' or '|' or '$' or '`' or '\''))
            return $"{fieldName} contains characters outside the allowed ref alphabet.";
        return null;
    }

    public static string? ValidateSelection(AuditRunSelection selection)
    {
        if (selection.IsExplicit && selection.IsProfile)
            return "Provide either explicit auditor IDs or a named profile, not both.";
        if (!selection.IsExplicit && !selection.IsProfile)
            return "Provide either explicit auditor IDs or a named profile.";
        if (selection.IsExplicit)
        {
            if (selection.AuditorIds.Count > MaxAuditorsPerRun)
                return $"At most {MaxAuditorsPerRun} auditors per run.";
            if (selection.AuditorIds.Any(string.IsNullOrWhiteSpace))
                return "Auditor IDs must be non-empty.";
            if (selection.AuditorIds.Distinct(StringComparer.Ordinal).Count() != selection.AuditorIds.Count)
                return "Auditor IDs must be unique.";
        }
        return null;
    }

    /// <summary>
    /// Classifies one selected auditor against standalone-run support rules.
    /// Tool-only contract: LLM auditors and agent-credential requirements are
    /// Unsupported with exact reasons; never silently omitted.
    /// </summary>
    public static (bool Supported, AuditRunUnsupportedReason Reason, string? Detail) ClassifyAuditor(
        IAuditor auditor, bool diffBased, bool hasBaseSha, bool auditorEnabled)
    {
        if (!auditorEnabled)
            return (false, AuditRunUnsupportedReason.DisabledAuditor, $"Auditor '{auditor.Name}' is disabled.");
        if (string.Equals(auditor.Kind, "llm", StringComparison.OrdinalIgnoreCase))
            return (false, AuditRunUnsupportedReason.LlmAuditor,
                $"Auditor '{auditor.Name}' is LLM-driven and not supported in standalone tool-only runs.");
        if ((auditor.Required & AuditCapabilities.AgentCredentials) != 0)
            return (false, AuditRunUnsupportedReason.AgentCredentialsRequired,
                $"Auditor '{auditor.Name}' requires agent credentials, which standalone runs never mount.");
        if (auditor is IRequiresPassedBuildTestGate && diffBased && !hasBaseSha)
            return (false, AuditRunUnsupportedReason.MissingExplicitDiffBase,
                $"Auditor '{auditor.Name}' needs an explicit base ref for diff review.");
        if (IsDiffOnlyAuditor(auditor) && diffBased && !hasBaseSha)
            return (false, AuditRunUnsupportedReason.MissingExplicitDiffBase,
                $"Auditor '{auditor.Name}' is diff-based and requires an explicit base ref.");
        return (true, AuditRunUnsupportedReason.None, null);
    }

    private static bool IsDiffOnlyAuditor(IAuditor auditor) =>
        string.Equals(auditor.Kind, "diff-pattern", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Bounds a log before buffering: callers must enforce the cap on the
    /// streaming side. Pure helper truncates with a marker.
    /// </summary>
    public static string TruncateBounded(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxChars)
            return value ?? string.Empty;
        return value[..maxChars] + "[...truncated]";
    }

    /// <summary>Redacts bearer tokens and secret-looking assignments from captured logs.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var lines = value.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lower = line.ToLowerInvariant();
            if (lower.Contains("bearer ") || lower.Contains("api_key") || lower.Contains("apikey")
                || lower.Contains("secret") || lower.Contains("password") || lower.Contains("token="))
                lines[i] = "[redacted]";
        }
        return string.Join('\n', lines);
    }

    /// <summary>Stable idempotency body hash over project/ref/base/selection.</summary>
    public static string BodyHash(AuditRunCreateRequest request)
    {
        var payload = JsonSerializer.Serialize(new
        {
            Project = request.ProjectId.Trim().ToLowerInvariant(),
            Ref = request.Ref.Trim(),
            Base = request.BaseRef?.Trim() ?? string.Empty,
            Auditors = request.Selection.AuditorIds.OrderBy(a => a, StringComparer.Ordinal).ToArray(),
            request.Selection.Profile,
            Repo = request.RepositoryOverride?.Trim() ?? string.Empty,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}

using CodeyBox.Core;
using Microsoft.Extensions.Configuration;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// One consistent configuration mechanism for every external-tool auditor:
/// severity threshold, rule selection, extra arguments, paths to exclude, and
/// execution bounds. Bind from the plugin's scoped config section
/// (<c>CodeyBox:Plugins:&lt;plugin-id&gt;</c>) with <see cref="Bind"/>, or
/// construct programmatically. Auditors should resolve these at run time
/// (via a <c>Func&lt;ExternalToolAuditorOptions&gt;</c>) so operator edits
/// apply without a host restart.
/// </summary>
public sealed class ExternalToolAuditorOptions
{
    public const int DefaultTimeoutSeconds = 300;
    public const int DefaultMaxOutputBytesPerStream = 1024 * 1024;
    public const int DefaultMaxFindings = 1000;

    /// <summary>Maximum distinct extra arguments an operator may append. Enforced before execution.</summary>
    public const int MaxExtraArguments = 256;

    /// <summary>Ceiling applied to <see cref="Timeout"/> at bind and exec time, in seconds.</summary>
    public const int MaxTimeoutSeconds = 3600;

    /// <summary>Floor and ceiling applied to <see cref="MaxOutputBytesPerStream"/> at bind and exec time.</summary>
    public const int MinCapturedOutputBytes = 4096;
    public const int MaxCapturedOutputBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Per-invocation ceiling for the tool. When exceeded the run is reported
    /// as infrastructure (the check could not complete), never as a pass.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>Per-stream output capture cap. Excess is discarded and reported as truncated.</summary>
    public int MaxOutputBytesPerStream { get; set; } = DefaultMaxOutputBytesPerStream;

    /// <summary>Upper bound on findings returned from one run. Excess findings are dropped and the truncation is reported.</summary>
    public int MaxFindings { get; set; } = DefaultMaxFindings;

    /// <summary>
    /// Exit codes that mean "the tool ran and its output is the verdict"
    /// (e.g. <c>{0, 1}</c> for scanners that exit 1 when findings exist).
    /// Any other exit means "the tool could not run" and is reported as
    /// infrastructure. Defaults to <c>{0}</c> only: a tool whose convention is
    /// unknown fails loudly instead of being guessed.
    /// </summary>
    public IReadOnlySet<int> FindingsExitCodes { get; set; } = new HashSet<int> { 0 };

    /// <summary>Findings below this severity (after mapping) are dropped.</summary>
    public AuditSeverity MinimumSeverity { get; set; } = AuditSeverity.Info;

    /// <summary>When non-empty, only these rule ids (exact match) are reported.</summary>
    public IReadOnlySet<string> IncludedRules { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Rule ids (exact match) that are never reported.</summary>
    public IReadOnlySet<string> ExcludedRules { get; set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Repository-relative paths to exclude. An entry excludes on exact match,
    /// or — when it ends with <c>/</c> — everything beneath that directory.
    /// Never substring matching.
    /// </summary>
    public IReadOnlyList<string> ExcludePaths { get; set; } = [];

    /// <summary>Operator-supplied arguments appended after the author's. Passed as argv entries, never through a shell.</summary>
    public IReadOnlyList<string> ExtraArguments { get; set; } = [];

    /// <summary>
    /// Binds operational knobs from a configuration section. Recognized keys:
    /// <c>TimeoutSeconds</c>, <c>MaxOutputBytesPerStream</c>,
    /// <c>MaxFindings</c>, <c>FindingsExitCodes</c> (comma-separated ints),
    /// <c>MinimumSeverity</c> (<c>info</c>/<c>warning</c>/<c>error</c>),
    /// <c>IncludedRules</c>, <c>ExcludedRules</c>, <c>ExcludePaths</c> and
    /// <c>ExtraArguments</c> (comma-separated). Unrecognized keys are ignored;
    /// malformed values fall back to <paramref name="defaults"/> (or built-in
    /// defaults) rather than failing the bind.
    /// </summary>
    public static ExternalToolAuditorOptions Bind(
        IConfigurationSection section,
        ExternalToolAuditorOptions? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        defaults ??= new ExternalToolAuditorOptions();
        var bound = new ExternalToolAuditorOptions
        {
            Timeout = defaults.Timeout,
            MaxOutputBytesPerStream = defaults.MaxOutputBytesPerStream,
            MaxFindings = defaults.MaxFindings,
            FindingsExitCodes = defaults.FindingsExitCodes,
            MinimumSeverity = defaults.MinimumSeverity,
            IncludedRules = defaults.IncludedRules,
            ExcludedRules = defaults.ExcludedRules,
            ExcludePaths = defaults.ExcludePaths,
            ExtraArguments = defaults.ExtraArguments,
        };

        var timeoutSeconds = ReadInt(section["TimeoutSeconds"]);
        if (timeoutSeconds.HasValue && timeoutSeconds.Value > 0)
            bound.Timeout = TimeSpan.FromSeconds(Math.Min(timeoutSeconds.Value, MaxTimeoutSeconds));

        var maxOutput = ReadInt(section["MaxOutputBytesPerStream"]);
        if (maxOutput.HasValue && maxOutput.Value >= MinCapturedOutputBytes)
            bound.MaxOutputBytesPerStream = Math.Min(maxOutput.Value, MaxCapturedOutputBytes);

        var maxFindings = ReadInt(section["MaxFindings"]);
        if (maxFindings.HasValue && maxFindings.Value > 0)
            bound.MaxFindings = Math.Min(maxFindings.Value, 100_000);

        var exitCodes = SplitCommaSeparatedList(section["FindingsExitCodes"])
            .Select(ReadInt)
            .Where(code => code.HasValue)
            .Select(code => code!.Value)
            .ToHashSet();
        if (exitCodes.Count > 0)
            bound.FindingsExitCodes = exitCodes;

        if (!string.IsNullOrWhiteSpace(section["MinimumSeverity"]))
            bound.MinimumSeverity = section["MinimumSeverity"]!.Trim().ToLowerInvariant() switch
            {
                "info" => AuditSeverity.Info,
                "warning" or "warn" => AuditSeverity.Warning,
                "error" or "err" => AuditSeverity.Error,
                _ => defaults.MinimumSeverity,
            };

        var included = SplitCommaSeparatedList(section["IncludedRules"]).ToHashSet(StringComparer.Ordinal);
        if (included.Count > 0)
            bound.IncludedRules = included;

        var excluded = SplitCommaSeparatedList(section["ExcludedRules"]).ToHashSet(StringComparer.Ordinal);
        if (excluded.Count > 0)
            bound.ExcludedRules = excluded;

        var excludePaths = SplitCommaSeparatedList(section["ExcludePaths"]);
        if (excludePaths.Count > 0)
            bound.ExcludePaths = excludePaths;

        var extraArguments = SplitCommaSeparatedList(section["ExtraArguments"]);
        if (extraArguments.Count > 0)
            bound.ExtraArguments = extraArguments.Take(MaxExtraArguments + 1).ToList();

        return bound;
    }

    /// <summary>
    /// Splits a comma-separated scoped-config value into trimmed, non-empty
    /// entries — the shared convention for list-valued auditor keys
    /// (<c>ExcludePaths</c>, <c>ExtraArguments</c>, plugin-specific lists).
    /// </summary>
    public static List<string> SplitCommaSeparatedList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => item.Length > 0)
                .ToList();

    private static int? ReadInt(string? value)
        => int.TryParse(value?.Trim(), out var parsed) ? parsed : null;
}

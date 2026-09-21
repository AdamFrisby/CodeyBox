using CodeyBox.Core;

namespace CodeyBox.PluginSdk.Tools;

/// <summary>
/// One declared mapping from an external tool's severity vocabulary to
/// CodeyBox's <see cref="AuditSeverity"/>. Every wrapped tool goes through a
/// mapping — the tool's raw strings (e.g. <c>"high"</c>, <c>"medium"</c>,
/// <c>"note"</c>) are never passed through as findings, so a severity means
/// the same thing regardless of which scanner produced it.
/// </summary>
public sealed class ExternalToolSeverityMapping
{
    private readonly Dictionary<string, AuditSeverity> _levels;

    /// <summary>
    /// Severity used when the tool reports a level absent from the map
    /// (including a missing level). Declared up front rather than guessed.
    /// </summary>
    public AuditSeverity DefaultSeverity { get; }

    public ExternalToolSeverityMapping(
        IReadOnlyDictionary<string, AuditSeverity> levels,
        AuditSeverity defaultSeverity = AuditSeverity.Warning)
    {
        ArgumentNullException.ThrowIfNull(levels);
        _levels = new Dictionary<string, AuditSeverity>(levels, StringComparer.OrdinalIgnoreCase);
        DefaultSeverity = defaultSeverity;
    }

    /// <summary>
    /// Shared default covering the vocabulary most scanners emit
    /// (critical/high/medium/low, error/warning/info, fail/note). Unknown
    /// levels map to <see cref="AuditSeverity.Warning"/>.
    /// </summary>
    public static ExternalToolSeverityMapping Default { get; } = new(
        new Dictionary<string, AuditSeverity>(StringComparer.OrdinalIgnoreCase)
        {
            ["critical"] = AuditSeverity.Error,
            ["high"] = AuditSeverity.Error,
            ["error"] = AuditSeverity.Error,
            ["fail"] = AuditSeverity.Error,
            ["failure"] = AuditSeverity.Error,
            ["medium"] = AuditSeverity.Warning,
            ["moderate"] = AuditSeverity.Warning,
            ["warning"] = AuditSeverity.Warning,
            ["warn"] = AuditSeverity.Warning,
            ["low"] = AuditSeverity.Info,
            ["info"] = AuditSeverity.Info,
            ["informational"] = AuditSeverity.Info,
            ["note"] = AuditSeverity.Info,
            ["none"] = AuditSeverity.Info,
        },
        AuditSeverity.Warning);

    /// <summary>Maps a tool-reported level to <see cref="AuditSeverity"/>.</summary>
    public AuditSeverity Map(string? level)
        => string.IsNullOrWhiteSpace(level) || !_levels.TryGetValue(level.Trim(), out var severity)
            ? DefaultSeverity
            : severity;
}

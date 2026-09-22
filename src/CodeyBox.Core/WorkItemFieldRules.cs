namespace CodeyBox.Core;

/// <summary>
/// The per-field validation and normalisation rules for work-item fields —
/// the single definition of "what a valid field is" for every queue-facing
/// surface: REST create/PATCH (<c>WorkItemCreationService</c>,
/// <c>WorkItemEndpoints</c>), suggestion promotion
/// (<c>SuggestionEndpoints</c>), task-template checks
/// (<c>TaskTemplateRegistry</c>), and the majordomo tool contract. Each
/// <c>Normalize*</c> helper returns the canonical value plus a null error on
/// success, or a null value plus a caller-presentable error string; each
/// <c>Check*</c> helper returns the error or null. Surfaces adapt the error
/// to their own idiom (a 400 <c>IResult</c>, an <see cref="ArgumentException"/>).
///
/// <para>
/// Lookups against live state — the agent registry, the agent-class catalog,
/// project audit profiles, the knob registry, dependency resolution — are
/// deliberately NOT here: they are execution-time concerns layered on top of
/// these shape rules by each entry point.
/// </para>
/// </summary>
public static class WorkItemFieldRules
{
    /// <summary>Required title: non-whitespace, no option-like/control characters, bounded length.</summary>
    public static (string? Value, string? Error) NormalizeTitle(string? value, string field = "title")
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, $"{field} is required");
        if (CheckNoOptionLikeOrControl(value, field) is { } guardError)
            return (null, guardError);
        if (value.Length > WorkItemLimits.MaxTitleLength)
            return (null, $"{field} must be <= {WorkItemLimits.MaxTitleLength} chars");
        return (value, null);
    }

    /// <summary>Required prompt: non-whitespace, bounded length.</summary>
    public static (string? Value, string? Error) NormalizePrompt(string? value, string field = "prompt")
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, $"{field} is required");
        if (value.Length > WorkItemLimits.MaxPromptLength)
            return (null, $"{field} must be <= {WorkItemLimits.MaxPromptLength} chars");
        return (value, null);
    }

    /// <summary>
    /// Optional agent-class reference: null means unset; a provided value is
    /// trimmed and must be non-empty, bounded, and free of control characters.
    /// Class existence is checked at execution against the live router catalog.
    /// </summary>
    public static (string? Value, string? Error) NormalizeAgentClassId(string? value, string field = "agentClassId")
        => NormalizeOptionalLabel(value, field, WorkItemLimits.MaxAgentClassIdLength);

    /// <summary>
    /// Optional audit-profile name: null means unset; a provided value is
    /// trimmed and must be non-empty, bounded, and free of control characters.
    /// Profile existence is checked at execution against the project's audit config.
    /// </summary>
    public static (string? Value, string? Error) NormalizeAuditorProfile(string? value, string field = "auditorProfile")
        => NormalizeOptionalLabel(value, field, WorkItemLimits.MaxAuditorProfileLength);

    /// <summary>
    /// Optional audit-complexity label: null or whitespace normalises to unset;
    /// a non-blank value is trimmed and must be bounded and free of
    /// option-like/control characters.
    /// </summary>
    public static (string? Value, string? Error) NormalizeAuditComplexity(string? value, string field = "auditComplexity")
    {
        if (string.IsNullOrWhiteSpace(value))
            return (null, null);
        var trimmed = value.Trim();
        if (trimmed.Length > WorkItemLimits.MaxAuditComplexityLength)
            return (null, $"{field} must be <= {WorkItemLimits.MaxAuditComplexityLength} chars");
        if (CheckNoOptionLikeOrControl(trimmed, field) is { } guardError)
            return (null, guardError);
        return (trimmed, null);
    }

    /// <summary>
    /// Required-capability tags: trims entries, drops nulls/empties,
    /// de-duplicates case-insensitively, rejects over-length tags and control
    /// characters. Null input normalises to the empty list.
    /// </summary>
    public static (IReadOnlyList<string>? Value, string? Error) NormalizeRequiredCapabilities(
        IReadOnlyList<string>? value,
        string field = "requiredCapabilities")
    {
        if (value is null)
            return ([], null);
        if (value.Count > WorkItemLimits.MaxRequiredCapabilities)
            return (null, $"{field} may contain at most {WorkItemLimits.MaxRequiredCapabilities} entries");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(value.Count);
        foreach (var entry in value)
        {
            var tag = entry?.Trim();
            if (string.IsNullOrEmpty(tag)) continue;
            if (tag.Length > WorkItemLimits.MaxCapabilityLength)
                return (null, $"{field} entry '{Validation.DescribeUntrustedValue(tag)}' exceeds {WorkItemLimits.MaxCapabilityLength} chars");
            if (tag.Any(char.IsControl))
                return (null, $"{field} entries must not contain control characters");
            if (seen.Add(tag)) result.Add(tag);
        }
        return (result, null);
    }

    /// <summary>Dependency-list size bound.</summary>
    public static string? CheckDependsOnCount(int count, string field = "dependsOn")
        => count > WorkItemLimits.MaxDependsOn
            ? $"{field} must contain at most {WorkItemLimits.MaxDependsOn} entries"
            : null;

    /// <summary>
    /// Namespaced external identifiers: namespace keys and values pass
    /// <see cref="Validation.ValidateExternalIdNamespace"/> /
    /// <see cref="Validation.ValidateExternalId"/>; null values are rejected
    /// (deletion is the dedicated PATCH surface's job, not create's). The map
    /// is copied case-insensitively so namespace casing collapses the same way
    /// on every entry point. Null input normalises to the empty map.
    /// </summary>
    public static (IReadOnlyDictionary<string, string>? Value, string? Error) NormalizeExternalIds(
        IReadOnlyDictionary<string, string>? value,
        string field = "externalIds")
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value is null)
            return (copy, null);
        if (value.Count > WorkItemLimits.MaxExternalIds)
            return (null, $"{field} may contain at most {WorkItemLimits.MaxExternalIds} entries per work item");
        foreach (var (ns, id) in value)
        {
            // The namespace is untrusted input echoed into the error label —
            // strip control characters and bound it before interpolating.
            var nsLabel = Validation.DescribeUntrustedValue(ns);
            if (CheckExternalIdNamespace(ns, $"{field} key '{nsLabel}'") is { } nsError)
                return (null, nsError);
            if (id is null)
                return (null, $"{field}['{nsLabel}'] must not be null");
            if (CheckExternalId(id, $"{field}['{nsLabel}']") is { } idError)
                return (null, idError);
            copy[ns] = id;
        }
        return (copy, null);
    }

    /// <summary>Knob-override map size bound.</summary>
    public static string? CheckKnobOverrideCount(int count, string field = "knobs")
        => count > WorkItemLimits.MaxKnobOverrides
            ? $"{field} may contain at most {WorkItemLimits.MaxKnobOverrides} entries"
            : null;

    /// <summary>
    /// Per-item knob overrides: shape rules only — bounded count, non-empty
    /// bounded keys, non-null bounded values, no control characters — copied
    /// into a case-insensitive map so key casing collapses the same way on
    /// every entry point. Key existence and value canonicalisation are the
    /// knob registry's job and happen at execution.
    /// </summary>
    public static (IReadOnlyDictionary<string, string>? Value, string? Error) NormalizeKnobOverrides(
        IReadOnlyDictionary<string, string>? value,
        string field = "knobs")
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value is null)
            return (copy, null);
        if (CheckKnobOverrideCount(value.Count, field) is { } countError)
            return (null, countError);
        foreach (var (key, knobValue) in value)
        {
            if (CheckKnobOverrideEntry(key, knobValue, field) is { } entryError)
                return (null, entryError);
            copy[key] = knobValue;
        }
        return (copy, null);
    }

    /// <summary>
    /// Shape rules for one knob-override entry: non-empty bounded key without
    /// control characters, non-null bounded value without control characters.
    /// </summary>
    private static string? CheckKnobOverrideEntry(string? key, string? knobValue, string field = "knobs")
    {
        if (string.IsNullOrWhiteSpace(key))
            return $"{field} key must not be empty";
        if (key.Length > WorkItemLimits.MaxKnobKeyLength || key.Any(char.IsControl))
            return $"{field} keys must be <= {WorkItemLimits.MaxKnobKeyLength} chars with no control characters";
        if (knobValue is null)
            return $"knob '{key}' value must not be null";
        if (knobValue.Length > WorkItemLimits.MaxKnobValueLength || knobValue.Any(char.IsControl))
            return $"{field}['{key}'] must be <= {WorkItemLimits.MaxKnobValueLength} chars with no control characters";
        return null;
    }

    /// <summary>Global scheduling-priority bounds.</summary>
    public static string? CheckPriorityBounds(int priority)
        => priority < WorkItemLimits.MinPriority || priority > WorkItemLimits.MaxPriority
            ? $"priority must be within [{WorkItemLimits.MinPriority}, {WorkItemLimits.MaxPriority}]"
            : null;

    /// <summary>Per-project priority ceiling, applied on top of the global bounds.</summary>
    public static string? CheckProjectPriorityCeiling(int priority, Project project)
        => project.MaxPriority is { } maxPriority && priority > maxPriority
            ? $"priority {priority} exceeds project '{project.Id}' maxPriority {maxPriority}"
            : null;

    /// <summary>Per-item audit iteration cap bounds.</summary>
    public static string? CheckAuditMaxIterations(int value)
        => value <= 0
            ? "auditMaxIterations must be greater than 0"
            : value > ProjectAudit.MaxIterationBudget
                ? $"auditMaxIterations must be <= {ProjectAudit.MaxIterationBudget}"
                : null;

    /// <summary>Work-branch/base-branch distinctness invariant.</summary>
    public static string? CheckDistinctBranches(string? baseBranch, string? workBranch)
        => baseBranch is not null && workBranch is not null
            && string.Equals(baseBranch, workBranch, StringComparison.Ordinal)
                ? "workBranch must differ from baseBranch"
                : null;

    /// <summary>Per-item work-phase timeout bounds.</summary>
    public static string? CheckWorkTimeout(TimeSpan value)
    {
        var min = TimeSpan.FromMinutes(WorkTimeoutPolicy.MinMinutes);
        var max = TimeSpan.FromMinutes(WorkTimeoutPolicy.MaxMinutes);
        return value < min || value > max
            ? $"workTimeout must be within [{min}, {max}]"
            : null;
    }

    /// <summary>Per-item merge-phase timeout bounds.</summary>
    public static string? CheckMergeTimeout(TimeSpan value)
    {
        var min = TimeSpan.FromMinutes(WorkItemLimits.MinMergeTimeoutMinutes);
        var max = TimeSpan.FromMinutes(WorkItemLimits.MaxMergeTimeoutMinutes);
        return value < min || value > max
            ? $"mergeTimeout must be within [{min}, {max}]"
            : null;
    }

    /// <summary>Minimum model-quality score bounds.</summary>
    public static string? CheckMinModelScore(int value)
        => value < WorkItemLimits.MinModelScoreFloor || value > WorkItemLimits.MinModelScoreCeiling
            ? $"minModelScore must be within [{WorkItemLimits.MinModelScoreFloor}, {WorkItemLimits.MinModelScoreCeiling}]"
            : null;

    /// <summary>Clamps a minutes value into the per-item work-timeout window.</summary>
    public static TimeSpan ClampWorkTimeoutMinutes(int minutes)
        => TimeSpan.FromMinutes(WorkTimeoutPolicy.ClampMinutes(minutes));

    /// <summary>Clamps a minutes value into the per-item merge-timeout window.</summary>
    public static TimeSpan ClampMergeTimeoutMinutes(int minutes)
        => TimeSpan.FromMinutes(Math.Clamp(minutes, WorkItemLimits.MinMergeTimeoutMinutes, WorkItemLimits.MaxMergeTimeoutMinutes));

    /// <summary>Clamps a score into the minimum model-quality window.</summary>
    public static int ClampMinModelScore(int value)
        => Math.Clamp(value, WorkItemLimits.MinModelScoreFloor, WorkItemLimits.MinModelScoreCeiling);

    private static (string? Value, string? Error) NormalizeOptionalLabel(string? value, string field, int maxLength)
    {
        if (value is null) return (null, null);
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return (null, $"{field} must not be empty");
        if (trimmed.Length > maxLength)
            return (null, $"{field} must be <= {maxLength} chars");
        if (trimmed.Any(char.IsControl))
            return (null, $"{field} must not contain control characters");
        return (trimmed, null);
    }

    private static string? CheckNoOptionLikeOrControl(string value, string field)
        => CaptureValidationError(() => Validation.ValidateNoOptionLikeOrControl(value, field));

    private static string? CheckExternalIdNamespace(string value, string field)
        => CaptureValidationError(() => Validation.ValidateExternalIdNamespace(value, field));

    private static string? CheckExternalId(string value, string field)
        => CaptureValidationError(() => Validation.ValidateExternalId(value, field));

    private static string? CaptureValidationError(Action check)
    {
        try
        {
            check();
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }
}

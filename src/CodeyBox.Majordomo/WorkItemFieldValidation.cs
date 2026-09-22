using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Per-field argument validation shared by <see cref="NewWorkItemSpec"/> and
/// <see cref="WorkItemPatch"/> so a field means the same thing on create and
/// on edit. Bounds mirror the REST API's limits (<see cref="WorkItemLimits"/>,
/// <see cref="WorkTimeoutPolicy"/>, <see cref="ProjectAudit.MaxIterationBudget"/>)
/// but REJECT out-of-range values instead of clamping them: a wrong call must
/// fail at the contract, not be silently reinterpreted downstream.
/// </summary>
internal static class WorkItemFieldValidation
{
    /// <summary>Upper bound on per-item knob overrides carried by one call.</summary>
    internal const int MaxKnobOverrides = 32;

    /// <summary>Upper bound on a single knob key or value length.</summary>
    internal const int MaxKnobValueLength = 1024;

    /// <summary>Upper bound for short label fields (auditorProfile).</summary>
    internal const int MaxLabelLength = 200;

    internal static string Title(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("title is required", nameof(value));
        Validation.ValidateNoOptionLikeOrControl(value, "title");
        if (value.Length > WorkItemLimits.MaxTitleLength)
            throw new ArgumentException($"title must be <= {WorkItemLimits.MaxTitleLength} chars", nameof(value));
        return value;
    }

    internal static string Prompt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("prompt is required", nameof(value));
        if (value.Length > WorkItemLimits.MaxPromptLength)
            throw new ArgumentException("prompt must be <= 64KB", nameof(value));
        return value;
    }

    internal static string? AgentClassId(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("agentClassId must not be empty", nameof(value));
        if (trimmed.Length > WorkItemLimits.MaxAgentClassIdLength)
            throw new ArgumentException($"agentClassId must be <= {WorkItemLimits.MaxAgentClassIdLength} chars", nameof(value));
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("agentClassId must not contain control characters", nameof(value));
        return trimmed;
    }

    internal static string? AuditorProfile(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("auditorProfile must not be empty", nameof(value));
        if (trimmed.Length > MaxLabelLength)
            throw new ArgumentException($"auditorProfile must be <= {MaxLabelLength} chars", nameof(value));
        if (trimmed.Any(char.IsControl))
            throw new ArgumentException("auditorProfile must not contain control characters", nameof(value));
        return trimmed;
    }

    internal static string? Branch(string? value, string fieldName)
    {
        if (value is null) return null;
        Validation.ValidateBranchName(value, fieldName);
        return value;
    }

    internal static int Priority(int value)
    {
        if (value < WorkItemLimits.MinPriority || value > WorkItemLimits.MaxPriority)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"priority must be within [{WorkItemLimits.MinPriority}, {WorkItemLimits.MaxPriority}]");
        return value;
    }

    internal static int MinModelScore(int value)
    {
        if (value < WorkItemLimits.MinModelScoreFloor || value > WorkItemLimits.MinModelScoreCeiling)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"minModelScore must be within [{WorkItemLimits.MinModelScoreFloor}, {WorkItemLimits.MinModelScoreCeiling}]");
        return value;
    }

    internal static TimeSpan WorkTimeout(TimeSpan value)
    {
        var min = TimeSpan.FromMinutes(WorkTimeoutPolicy.MinMinutes);
        var max = TimeSpan.FromMinutes(WorkTimeoutPolicy.MaxMinutes);
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"workTimeout must be within [{min}, {max}]");
        return value;
    }

    internal static TimeSpan MergeTimeout(TimeSpan value)
    {
        var min = TimeSpan.FromMinutes(WorkItemLimits.MinMergeTimeoutMinutes);
        var max = TimeSpan.FromMinutes(WorkItemLimits.MaxMergeTimeoutMinutes);
        if (value < min || value > max)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"mergeTimeout must be within [{min}, {max}]");
        return value;
    }

    internal static int AuditMaxIterations(int value)
    {
        if (value <= 0 || value > ProjectAudit.MaxIterationBudget)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"auditMaxIterations must be within [1, {ProjectAudit.MaxIterationBudget}]");
        return value;
    }

    internal static string? AuditComplexity(string? value)
    {
        if (value is null) return null;
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            throw new ArgumentException("auditComplexity must not be empty", nameof(value));
        if (trimmed.Length > WorkItemLimits.MaxAuditComplexityLength)
            throw new ArgumentException($"auditComplexity must be <= {WorkItemLimits.MaxAuditComplexityLength} chars", nameof(value));
        Validation.ValidateNoOptionLikeOrControl(trimmed, "auditComplexity");
        return trimmed;
    }

    /// <summary>
    /// Normalizes like the REST API: trims entries, drops empties,
    /// de-duplicates case-insensitively, rejects over-length tags and control
    /// characters.
    /// </summary>
    internal static IReadOnlyList<string> RequiredCapabilities(IReadOnlyList<string>? value)
    {
        if (value is null) return [];
        if (value.Count > WorkItemLimits.MaxRequiredCapabilities)
            throw new ArgumentException(
                $"requiredCapabilities may contain at most {WorkItemLimits.MaxRequiredCapabilities} entries", nameof(value));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(value.Count);
        foreach (var entry in value)
        {
            var tag = entry?.Trim();
            if (string.IsNullOrEmpty(tag)) continue;
            if (tag.Length > WorkItemLimits.MaxCapabilityLength)
                throw new ArgumentException(
                    $"requiredCapabilities entries must be <= {WorkItemLimits.MaxCapabilityLength} chars", nameof(value));
            if (tag.Any(char.IsControl))
                throw new ArgumentException("requiredCapabilities entries must not contain control characters", nameof(value));
            if (seen.Add(tag)) result.Add(tag);
        }
        return result;
    }

    internal static IReadOnlyList<WorkItemId> DependsOn(IReadOnlyList<WorkItemId>? value)
    {
        if (value is null) return [];
        if (value.Count > WorkItemLimits.MaxDependsOn)
            throw new ArgumentException(
                $"dependsOn must contain at most {WorkItemLimits.MaxDependsOn} entries", nameof(value));
        return [.. value];
    }

    internal static IReadOnlyDictionary<string, string> ExternalIds(
        IReadOnlyDictionary<string, string>? value)
    {
        if (value is null) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (value.Count > WorkItemLimits.MaxExternalIds)
            throw new ArgumentException(
                $"externalIds may contain at most {WorkItemLimits.MaxExternalIds} entries", nameof(value));
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ns, id) in value)
        {
            Validation.ValidateExternalIdNamespace(ns, "externalIds key");
            if (id is null)
                throw new ArgumentException($"externalIds['{ns}'] must not be null", nameof(value));
            Validation.ValidateExternalId(id, $"externalIds['{ns}']");
            copy[ns] = id;
        }
        return copy;
    }

    internal static IReadOnlyDictionary<string, string> Knobs(
        IReadOnlyDictionary<string, string>? value)
    {
        if (value is null) return new Dictionary<string, string>(StringComparer.Ordinal);
        if (value.Count > MaxKnobOverrides)
            throw new ArgumentException(
                $"knobs may contain at most {MaxKnobOverrides} entries", nameof(value));
        var copy = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, knobValue) in value)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("knobs keys must not be empty", nameof(value));
            if (key.Length > WorkItemLimits.MaxCapabilityLength || key.Any(char.IsControl))
                throw new ArgumentException(
                    $"knobs keys must be <= {WorkItemLimits.MaxCapabilityLength} chars with no control characters", nameof(value));
            if (knobValue is null || knobValue.Length > MaxKnobValueLength || knobValue.Any(char.IsControl))
                throw new ArgumentException(
                    $"knobs['{key}'] must be non-null, <= {MaxKnobValueLength} chars, and contain no control characters", nameof(value));
            copy[key] = knobValue;
        }
        return copy;
    }
}

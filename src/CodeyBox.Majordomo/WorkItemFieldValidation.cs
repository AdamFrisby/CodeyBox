using System.Collections.Immutable;
using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Adapter from <see cref="WorkItemFieldRules"/> — the single definition of
/// "what a valid work-item field is", shared with the REST API — to this
/// contract's idiom: a wrong call throws <see cref="ArgumentException"/> at
/// construction instead of being silently reinterpreted downstream (the REST
/// surface clamps some ranges; here they are rejected).
/// </summary>
internal static class WorkItemFieldValidation
{
    internal static string Title(string? value)
        => OrThrow(WorkItemFieldRules.NormalizeTitle(value), "title")!;

    internal static string Prompt(string? value)
        => OrThrow(WorkItemFieldRules.NormalizePrompt(value), "prompt")!;

    internal static string? AgentClassId(string? value)
        => OrThrow(WorkItemFieldRules.NormalizeAgentClassId(value), "agentClassId");

    internal static string? AuditorProfile(string? value)
        => OrThrow(WorkItemFieldRules.NormalizeAuditorProfile(value), "auditorProfile");

    internal static string? Branch(string? value, string fieldName)
    {
        if (value is null) return null;
        Validation.ValidateBranchName(value, fieldName);
        return value;
    }

    internal static int Priority(int value)
        => OrThrowRange(WorkItemFieldRules.CheckPriorityBounds(value), value, "priority");

    internal static int MinModelScore(int value)
        => OrThrowRange(WorkItemFieldRules.CheckMinModelScore(value), value, "minModelScore");

    internal static TimeSpan WorkTimeout(TimeSpan value)
        => OrThrowRange(WorkItemFieldRules.CheckWorkTimeout(value), value, "workTimeout");

    internal static TimeSpan MergeTimeout(TimeSpan value)
        => OrThrowRange(WorkItemFieldRules.CheckMergeTimeout(value), value, "mergeTimeout");

    internal static int AuditMaxIterations(int value)
        => OrThrowRange(WorkItemFieldRules.CheckAuditMaxIterations(value), value, "auditMaxIterations");

    internal static string? AuditComplexity(string? value)
        => OrThrow(WorkItemFieldRules.NormalizeAuditComplexity(value), "auditComplexity");

    // The collection adapters re-wrap the shared rules' output in
    // ImmutableArray/ImmutableDictionary: the validated contract collections
    // must not be rewritable through a mutable cast of the exposed
    // IReadOnly* surface.

    internal static IReadOnlyList<string> RequiredCapabilities(IReadOnlyList<string>? value)
        => ImmutableArray.CreateRange(
            OrThrow(WorkItemFieldRules.NormalizeRequiredCapabilities(value), "requiredCapabilities")!);

    internal static IReadOnlyList<WorkItemId> DependsOn(IReadOnlyList<WorkItemId>? value)
    {
        if (value is null) return ImmutableArray<WorkItemId>.Empty;
        if (WorkItemFieldRules.CheckDependsOnCount(value.Count) is { } error)
            throw new ArgumentException(error, "dependsOn");
        return ImmutableArray.CreateRange(value);
    }

    internal static IReadOnlyDictionary<string, string> ExternalIds(
        IReadOnlyDictionary<string, string>? value)
        => ImmutableDictionary.CreateRange(
            StringComparer.OrdinalIgnoreCase,
            OrThrow(WorkItemFieldRules.NormalizeExternalIds(value), "externalIds")!);

    internal static IReadOnlyDictionary<string, string> Knobs(
        IReadOnlyDictionary<string, string>? value)
        => ImmutableDictionary.CreateRange(
            StringComparer.OrdinalIgnoreCase,
            OrThrow(WorkItemFieldRules.NormalizeKnobOverrides(value), "knobs")!);

    private static T? OrThrow<T>((T? Value, string? Error) result, string paramName)
        => result.Error is null
            ? result.Value
            : throw new ArgumentException(result.Error, paramName);

    private static T OrThrowRange<T>(string? error, T value, string paramName)
        => error is null
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, error);
}

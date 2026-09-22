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
        => OrThrow(WorkItemFieldRules.NormalizeTitle(value), nameof(value))!;

    internal static string Prompt(string? value)
        => OrThrow(WorkItemFieldRules.NormalizePrompt(value), nameof(value))!;

    internal static string? AgentClassId(string? value)
        => OrThrow(WorkItemFieldRules.NormalizeAgentClassId(value), nameof(value));

    internal static string? AuditorProfile(string? value)
        => OrThrow(WorkItemFieldRules.NormalizeAuditorProfile(value), nameof(value));

    internal static string? Branch(string? value, string fieldName)
    {
        if (value is null) return null;
        Validation.ValidateBranchName(value, fieldName);
        return value;
    }

    internal static int Priority(int value)
        => OrThrowRange(WorkItemFieldRules.CheckPriorityBounds(value), value, nameof(value));

    internal static int MinModelScore(int value)
        => OrThrowRange(WorkItemFieldRules.CheckMinModelScore(value), value, nameof(value));

    internal static TimeSpan WorkTimeout(TimeSpan value)
        => OrThrowRange(WorkItemFieldRules.CheckWorkTimeout(value), value, nameof(value));

    internal static TimeSpan MergeTimeout(TimeSpan value)
        => OrThrowRange(WorkItemFieldRules.CheckMergeTimeout(value), value, nameof(value));

    internal static int AuditMaxIterations(int value)
        => OrThrowRange(WorkItemFieldRules.CheckAuditMaxIterations(value), value, nameof(value));

    internal static string? AuditComplexity(string? value)
        => OrThrow(WorkItemFieldRules.NormalizeAuditComplexity(value), nameof(value));

    internal static IReadOnlyList<string> RequiredCapabilities(IReadOnlyList<string>? value)
        => OrThrow(WorkItemFieldRules.NormalizeRequiredCapabilities(value), nameof(value))!;

    internal static IReadOnlyList<WorkItemId> DependsOn(IReadOnlyList<WorkItemId>? value)
    {
        if (value is null) return [];
        if (WorkItemFieldRules.CheckDependsOnCount(value.Count) is { } error)
            throw new ArgumentException(error, nameof(value));
        return [.. value];
    }

    internal static IReadOnlyDictionary<string, string> ExternalIds(
        IReadOnlyDictionary<string, string>? value)
        => OrThrow(WorkItemFieldRules.NormalizeExternalIds(value), nameof(value))!;

    internal static IReadOnlyDictionary<string, string> Knobs(
        IReadOnlyDictionary<string, string>? value)
        => OrThrow(WorkItemFieldRules.NormalizeKnobOverrides(value), nameof(value))!;

    private static T? OrThrow<T>((T? Value, string? Error) result, string paramName)
        => result.Error is null
            ? result.Value
            : throw new ArgumentException(result.Error, paramName);

    private static T OrThrowRange<T>(string? error, T value, string paramName)
        => error is null
            ? value
            : throw new ArgumentOutOfRangeException(paramName, value, error);
}

using System.Globalization;

namespace CodeyBox.Core;

/// <summary>
/// Where the work-phase wall-clock budget in force for a dispatch came from.
/// Reported in timeout failure messages so an operator knows which knob to turn.
/// </summary>
public enum WorkTimeoutSource
{
    /// <summary>Per-item <c>WorkTimeout</c>, set at creation, by PATCH, or by a retry request.</summary>
    Item,
    /// <summary>Per-project <c>WorkTimeoutMinutes</c> (project value wins over <c>Defaults</c>).</summary>
    Project,
    /// <summary>Global default <c>CodeyBox:DefaultWorkTimeoutMinutes</c>.</summary>
    Default,
}

/// <summary>
/// Resolution rules for the work-phase wall-clock budget (also applied per
/// rework iteration). Precedence is per-item, then per-project, then the
/// global default — so a repository whose items are routinely long sets one
/// project value instead of stamping every item, while a single hard item can
/// still carry its own budget.
///
/// <para>
/// Bounds: the 1..480 minute clamp from the per-item create/PATCH surfaces is
/// enforced here for the configuration surfaces (project and global values),
/// so a typo cannot disable the timeout entirely. A per-item
/// <see cref="WorkItem.WorkTimeout"/> passes through untouched: the API entry
/// points already clamp it, and the pipeline's absolute fallback cap
/// (<c>PhaseAbsoluteTimeoutMultiplier</c>) still bounds the whole chain.
/// </para>
/// </summary>
public static class WorkTimeoutPolicy
{
    /// <summary>Shipped global default in minutes. The defect this policy fixes
    /// is that the value was unreachable, not that 240 is wrong — do not raise it here.</summary>
    public const int DefaultMinutes = 240;

    /// <summary>Lower bound in minutes for every configuration surface.</summary>
    public const int MinMinutes = 1;

    /// <summary>Upper bound in minutes for every configuration surface.</summary>
    public const int MaxMinutes = 480;

    /// <summary>Clamps a configured minute value to <see cref="MinMinutes"/>..<see cref="MaxMinutes"/>.</summary>
    public static int ClampMinutes(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);

    /// <summary>
    /// Resolves the effective work-phase budget and records which layer supplied it.
    /// Pure: safe to call at every dispatch so config reloads take effect for
    /// subsequently dispatched work without a restart.
    /// </summary>
    /// <param name="itemTimeout">Per-item override; null means inherit.</param>
    /// <param name="projectMinutes">Per-project override in minutes; null means inherit.</param>
    /// <param name="defaultMinutes">Configured global default in minutes; null means the shipped default.</param>
    public static (TimeSpan Budget, WorkTimeoutSource Source) Resolve(
        TimeSpan? itemTimeout,
        int? projectMinutes,
        int? defaultMinutes)
    {
        if (itemTimeout is { } explicitBudget)
            return (explicitBudget, WorkTimeoutSource.Item);
        if (projectMinutes is { } project)
            return (TimeSpan.FromMinutes(ClampMinutes(project)), WorkTimeoutSource.Project);
        return (TimeSpan.FromMinutes(ClampMinutes(defaultMinutes ?? DefaultMinutes)), WorkTimeoutSource.Default);
    }

    /// <summary>Human-readable duration for timeout messages ("240 minutes", "90 seconds", "250 ms").</summary>
    public static string FormatBudget(TimeSpan budget)
    {
        if (budget.TotalMinutes >= 1)
            return $"{FormatNumber(budget.TotalMinutes)} minutes";
        if (budget.TotalSeconds >= 1)
            return $"{FormatNumber(budget.TotalSeconds)} seconds";
        return $"{FormatNumber(budget.TotalMilliseconds)} ms";
    }

    private static string FormatNumber(double value)
    {
        var rounded = Math.Round(value, 2);
        return rounded == Math.Floor(rounded)
            ? ((long)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds the <c>timeout:work</c> failure message: names the budget that was
    /// in force, where that value came from, and how to raise it. Callers pass
    /// the values returned by <see cref="Resolve"/> for the failed item.
    /// </summary>
    public static string FormatTimeoutError(
        string phase,
        WorkItemId itemId,
        TimeSpan budget,
        WorkTimeoutSource source,
        string? projectId)
    {
        var origin = source switch
        {
            WorkTimeoutSource.Item => "per-item WorkTimeoutMinutes",
            WorkTimeoutSource.Project => $"project '{projectId ?? "?"}' WorkTimeoutMinutes",
            _ => $"global default CodeyBox:DefaultWorkTimeoutMinutes ({FormatBudget(budget)})",
        };
        return $"phase '{phase}' exceeded work timeout of {FormatBudget(budget)} (source: {origin}; work item {itemId}). "
            + "To raise it: POST /workitems/{id}/retry with {\"workTimeoutMinutes\": N} (1-480 minutes), "
            + "or set per-project WorkTimeoutMinutes, or raise CodeyBox:DefaultWorkTimeoutMinutes.";
    }
}

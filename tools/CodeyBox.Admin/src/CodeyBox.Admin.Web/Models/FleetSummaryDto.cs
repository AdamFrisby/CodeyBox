namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local copy of the orchestrator's fleet summary shape.
/// Intentionally separate from CodeyBox.Core — coupling is REST + JSON only.
/// </summary>
public sealed class FleetSummaryDto
{
    public string ProjectId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int QueuedCount { get; set; }
    public int InFlightCount { get; set; }
    public string? CurrentPhase { get; set; }
    public List<string> RecentOutcomes { get; set; } = [];
    public bool IsPaused { get; set; }
    public bool HasRecentFailures { get; set; }
    public string? PausedReason { get; set; }
    public double? MonthlySpendUsd { get; set; }
    public double? MonthlyBudgetUsd { get; set; }
    public string BudgetThresholdState { get; set; } = "unknown";

    /// <summary>
    /// Derived status indicator color for the UI dot.
    /// Red if paused or ≥3 of the last 5 outcomes are failures.
    /// Blue if in-flight. Yellow if only queued. Grey if idle.
    /// </summary>
    public string StatusColor =>
        IsPaused || HasRecentFailures ? "red" :
        InFlightCount > 0 ? "blue" :
        QueuedCount > 0 ? "yellow" :
        "grey";

    /// <summary>Budget bar CSS class based on threshold state.</summary>
    public string BudgetBarCss =>
        BudgetThresholdState == "critical" ? "budget-full" :
        BudgetThresholdState == "warning" ? "budget-warn" :
        "";

    public double BudgetPct =>
        MonthlyBudgetUsd is > 0 && MonthlySpendUsd.HasValue
            ? Math.Min(100, MonthlySpendUsd.Value / MonthlyBudgetUsd.Value * 100)
            : 0;
}

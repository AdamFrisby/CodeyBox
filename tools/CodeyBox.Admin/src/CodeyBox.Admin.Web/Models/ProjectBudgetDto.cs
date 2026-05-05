namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Response shape of GET /projects/{id}/budget.
/// </summary>
public sealed class ProjectBudgetDto
{
    public string ProjectId { get; set; } = "";
    public decimal MonthlyBudgetUsd { get; set; }
    public decimal CurrentSpendUsd { get; set; }
    public double Pct { get; set; }
    public int WarningThresholdPct { get; set; } = 80;
    public int HardCapPct { get; set; } = 100;
    /// <summary>One of: ok | warning | exceeded</summary>
    public string ThresholdState { get; set; } = "ok";
    public DateTimeOffset WindowStart { get; set; }
    public DateTimeOffset WindowEnd { get; set; }
    public ProjectQueueStateDto ProjectQueue { get; set; } = new();
}

public sealed class ProjectQueueStateDto
{
    public bool Paused { get; set; }
    public DateTimeOffset? PausedAt { get; set; }
    public string? PausedReason { get; set; }
}

namespace CodeyBox.Admin.Web.Models;

/// <summary>Response shape for GET /queue/status.</summary>
public sealed class QueueStatusDto
{
    public string State { get; set; } = "Running";
    public DateTimeOffset? PausedAt { get; set; }
    public string? PausedReason { get; set; }

    public bool IsPaused => State == "Paused";
}

/// <summary>Response shape for GET /projects/{id}/budget/usage.</summary>
public sealed class BudgetUsageDto
{
    public int LastHour { get; set; }
    public int Last24h { get; set; }
    public int CurrentlyInFlight { get; set; }
    public BudgetLimitsDto Limits { get; set; } = new();
}

public sealed class BudgetLimitsDto
{
    public int PerHour { get; set; }
    public int PerDay { get; set; }
    public int Concurrent { get; set; }
}

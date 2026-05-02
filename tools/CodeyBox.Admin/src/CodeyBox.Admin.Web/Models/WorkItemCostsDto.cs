using System.Text.Json;

namespace CodeyBox.Admin.Web.Models;

/// <summary>Response shape for GET /workitems/{id}/costs.</summary>
public sealed class WorkItemCostsDto
{
    public string WorkItemId { get; set; } = "";
    public CostTotalsDto Totals { get; set; } = new();
    public Dictionary<string, JsonElement> ByPhase { get; set; } = [];
    public List<AgentCostBreakdownDto> ByAgent { get; set; } = [];
}

public sealed class CostTotalsDto
{
    public int InputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double EstimatedUsd { get; set; }
}

public sealed class AgentCostBreakdownDto
{
    public string Agent { get; set; } = "";
    public string? ModelId { get; set; }
    public int InputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double EstimatedUsd { get; set; }
}

/// <summary>Response shape for GET /projects/{id}/costs.</summary>
public sealed class ProjectCostsDto
{
    public string ProjectId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public CostTotalsDto Totals { get; set; } = new();
    public List<AgentCostBreakdownDto> ByAgent { get; set; } = [];
    public List<WorkItemCostSummaryDto> ByWorkItem { get; set; } = [];
}

public sealed class WorkItemCostSummaryDto
{
    public string WorkItemId { get; set; } = "";
    public int InputTokens { get; set; }
    public int CachedInputTokens { get; set; }
    public int OutputTokens { get; set; }
    public double EstimatedUsd { get; set; }
}

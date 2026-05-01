using System.Text.Json;

namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Response shape for GET /workitems/{id}/timings.
/// Intentionally separate from CodeyBox.Core — REST + JSON coupling only.
/// </summary>
public sealed class WorkItemTimingsDto
{
    public string WorkItemId { get; set; } = "";
    public long TotalDurationMs { get; set; }
    public Dictionary<string, JsonElement> ByPhase { get; set; } = [];
    public List<TopStepDto> TopSteps { get; set; } = [];
}

public sealed class TopStepDto
{
    public string Step { get; set; } = "";
    public long TotalMs { get; set; }
    public int Count { get; set; }
}

/// <summary>Response shape for GET /workitems/timings/aggregate.</summary>
public sealed class AggregateTimingsDto
{
    public int WorkItemCount { get; set; }
    public List<StepStatDto> StepStats { get; set; } = [];
}

public sealed class StepStatDto
{
    public string Phase { get; set; } = "";
    public string Step { get; set; } = "";
    public int Count { get; set; }
    public long MedianMs { get; set; }
    public long P95Ms { get; set; }
}

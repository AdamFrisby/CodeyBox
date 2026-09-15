namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local copy of the orchestrator's agent-history response shape
/// (<c>GET /workitems/{id}/agent-history</c>) and agent-stream file list
/// (<c>GET /workitems/{id}/agent-streams</c>).
/// Intentionally separate from CodeyBox.Core — REST + JSON only.
/// </summary>
public sealed class WorkItemAgentHistoryDto
{
    public string WorkItemId { get; set; } = "";
    public string? WorkAgent { get; set; }
    public List<AgentInvolvementEntryDto> AgentHistory { get; set; } = [];
}

public sealed class AgentInvolvementEntryDto
{
    public string Id { get; set; } = "";
    public string AgentKind { get; set; } = "";
    public string? ModelId { get; set; }
    public string Phase { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? Iteration { get; set; }
    public string? Outcome { get; set; }
    public string? AgentInstanceId { get; set; }
}

public sealed class AgentStreamFileDto
{
    public string FileName { get; set; } = "";
    public string Phase { get; set; } = "";
    public int? Iteration { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

namespace CodeyBox.Admin.Web.Models;

/// <summary>Request body for POST /workitems/{id}/replay.</summary>
public sealed class ReplayWorkItemRequest
{
    public string? Agent { get; set; }
    public string? ModelId { get; set; }
    public string? AgentClassId { get; set; }
    public string? WorkBranch { get; set; }
}

/// <summary>Response shape for GET /workitems/{id}/replays.</summary>
public sealed class WorkItemReplaysDto
{
    public WorkItemDto Source { get; set; } = new();
    public List<WorkItemDto> Replays { get; set; } = [];
}

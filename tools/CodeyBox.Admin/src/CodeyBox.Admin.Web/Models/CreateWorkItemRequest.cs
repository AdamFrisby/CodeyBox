namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Request body for POST /workitems.
/// Locally defined — no dependency on CodeyBox.Core types.
/// </summary>
public sealed class CreateWorkItemRequest
{
    public string ProjectId { get; set; } = "";
    public string? ExternalId { get; set; }
    public Dictionary<string, string>? ExternalIds { get; set; }
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string? Agent { get; set; }
    public string? BaseBranch { get; set; }
    public string? WorkBranch { get; set; }
    public bool PushUpstream { get; set; } = true;
    public List<string> DependsOn { get; set; } = [];
}

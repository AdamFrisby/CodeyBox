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
    public string? AuditorProfile { get; set; }
    public string? AgentClassId { get; set; }
    public string? BaseBranch { get; set; }
    public string? WorkBranch { get; set; }
    public bool PushUpstream { get; set; } = true;
    public List<string> DependsOn { get; set; } = [];
    public int? Priority { get; set; }
    public int? MinModelScore { get; set; }
    public List<string>? RequiredCapabilities { get; set; }
    public int? AuditMaxIterations { get; set; }
    public string? AuditComplexity { get; set; }
    public Dictionary<string, string>? Knobs { get; set; }
    public string? ReleaseId { get; set; }
}

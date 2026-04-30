namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local copy of the orchestrator's project response shape.
/// Intentionally separate from CodeyBox.Core — coupling is REST + JSON only.
/// </summary>
public sealed class ProjectDto
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RepositoryUrl { get; set; } = "";
    public string? DefaultBaseBranch { get; set; }
    public string DefaultAgent { get; set; } = "claude";
}

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
    /// <summary>
    /// Project-level default agent class. Null when the project routes by
    /// agent only.
    /// </summary>
    public string? DefaultAgentClass { get; set; }
    /// <summary>
    /// Project-level default audit iteration budget. 0 means the endpoint did
    /// not report one (older shape) — the journey treats it as unknown.
    /// </summary>
    public int AuditMaxIterations { get; set; }
    /// <summary>Audit languages configured for the project (may be empty).</summary>
    public List<string> AuditLanguages { get; set; } = [];
    /// <summary>Audit types configured for the project (may be empty).</summary>
    public List<string> AuditTypes { get; set; } = [];
}

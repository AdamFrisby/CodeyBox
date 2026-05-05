namespace CodeyBox.Admin.Web.Models;

public sealed class ReleaseAuditIterationDto
{
    public int Iteration { get; set; }
    public int MaxIterations { get; set; }
    public int TotalFindings { get; set; }
    public int BlockingFindings { get; set; }
    public List<ReleaseAuditFindingDto> Findings { get; set; } = [];
    public string? RemediationWorkItemId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReleaseAuditFindingDto
{
    public string AuditorName { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Location { get; set; }
}

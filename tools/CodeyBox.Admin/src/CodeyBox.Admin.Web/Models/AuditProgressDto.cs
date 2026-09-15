namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local copy of the orchestrator's audit-progress response shapes
/// (<c>GET /workitems/{id}/audit-progress</c>).
/// Intentionally separate from CodeyBox.Orchestrator — REST + JSON only.
/// </summary>
public sealed class AuditProgressListDto
{
    public string WorkItemId { get; set; } = "";
    public List<AuditProgressRowDto> Progress { get; set; } = [];
}

public sealed class AuditProgressRowDto
{
    public string Id { get; set; } = "";
    public string WorkItemId { get; set; } = "";
    public string WorkAttemptKey { get; set; } = "";
    public int Iteration { get; set; }
    public int MaxIterations { get; set; }
    public string Status { get; set; } = "complete";
    public int BlockingFindings { get; set; }
    public int NonBlockingFindings { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string? WorkBranchTip { get; set; }
    public List<string> ScheduledAuditors { get; set; } = [];
    public List<string> CompletedAuditors { get; set; } = [];
    public List<string> BlockingFindingIds { get; set; } = [];
}

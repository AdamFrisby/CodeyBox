namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local copy of the orchestrator's audit-reports response shape.
/// Intentionally separate from CodeyBox.Core — coupling is REST + JSON only.
/// </summary>
public sealed class AuditReportsDto
{
    public string WorkItemId { get; set; } = "";
    public List<AuditReportIterationDto> Iterations { get; set; } = [];
}

public sealed class AuditReportIterationDto
{
    public string Target { get; set; } = "code";
    public int Iteration { get; set; }
    public int BlockingCount { get; set; }
    public int NonBlockingCount { get; set; }
    public List<AuditReportAuditorDto> Auditors { get; set; } = [];
}

public sealed class AuditReportAuditorDto
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string WorstSeverity { get; set; } = "none";
    public long DurationMs { get; set; }
    public List<AuditReportFindingDto> Findings { get; set; } = [];
    public bool RawOutputAvailable { get; set; }
    public AuditReportTestSelectionDto? TestSelection { get; set; }
}

public sealed class AuditReportTestSelectionDto
{
    public string Mode { get; set; } = "";
    public string Selector { get; set; } = "";
    public List<string> Layers { get; set; } = [];
    public int SelectedCount { get; set; }
    public int TotalCount { get; set; }
    public double EstimatedSavedFraction { get; set; }
    public string Assessment { get; set; } = "";
    public List<string> Fallbacks { get; set; } = [];
    public string Detail { get; set; } = "";
}

public sealed class AuditReportFindingDto
{
    public string Id { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public List<int> LineHints { get; set; } = [];
}

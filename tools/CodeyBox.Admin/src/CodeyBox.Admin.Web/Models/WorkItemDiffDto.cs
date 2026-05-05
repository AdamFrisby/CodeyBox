namespace CodeyBox.Admin.Web.Models;

/// <summary>
/// Local mirror of the GET /workitems/{id}/diff JSON response shape.
/// Intentionally separate from CodeyBox.Core — coupling is REST + JSON only.
/// </summary>
public sealed class WorkItemDiffDto
{
    public string WorkItemId { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string WorkBranch { get; set; } = "";
    public string? BaseCommitSha { get; set; }
    public string? WorkCommitSha { get; set; }
    public int FilesChanged { get; set; }
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public string? Diff { get; set; }
    public bool Truncated { get; set; }
    public string? Hint { get; set; }
    public List<string> ChangedFiles { get; set; } = [];
}

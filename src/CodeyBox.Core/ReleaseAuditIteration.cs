namespace CodeyBox.Core;

/// <summary>
/// Records one completed iteration of the deep-audit loop for a release.
/// Persisted by the orchestrator and surfaced via GET /releases/{id}/audit-iterations
/// for the admin deep-audit timeline.
/// </summary>
public sealed class ReleaseAuditIteration
{
    public required ReleaseId ReleaseId { get; init; }
    public int Iteration { get; init; }
    public int MaxIterations { get; init; }
    public int TotalFindings { get; init; }
    public int BlockingFindings { get; init; }
    public IReadOnlyList<AuditFinding> Findings { get; init; } = [];
    public WorkItemId? RemediationWorkItemId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

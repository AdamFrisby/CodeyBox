namespace CodeyBox.Core;

/// <summary>
/// An adjacent issue observed by an agent during a work or merge phase that was
/// out of scope for the current work item. Persisted for operator triage; never
/// auto-queued as new work — operators decide whether to promote or dismiss.
/// </summary>
public sealed record Suggestion
{
    public required string Id { get; init; }
    public required string SourceWorkItemId { get; init; }
    public required string ProjectId { get; init; }
    public required string Title { get; init; }
    public required string Rationale { get; init; }
    public required string Category { get; init; }
    public required string Severity { get; init; }
    public required string EstimatedEffort { get; init; }
    public IReadOnlyList<string> FilesReferenced { get; init; } = [];
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string State { get; init; } = "open";
    public string? DismissReason { get; init; }
    public string? PromotedToWorkItemId { get; init; }
}

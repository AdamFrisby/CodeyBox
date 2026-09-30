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

    /// <summary>
    /// How many times this finding has been raised across work items: 1 for a
    /// fresh suggestion, incremented each time a repeat is merged by
    /// <see cref="ISuggestionStore.CreateOrMergeAsync"/>.
    /// </summary>
    public int OccurrenceCount { get; init; } = 1;

    /// <summary>
    /// Every work item that has raised this finding, oldest first. Always
    /// contains <see cref="SourceWorkItemId"/>; bounded by
    /// <see cref="SuggestionDedupePolicy.MaxRecordedSourceIds"/> on merge.
    /// </summary>
    public IReadOnlyList<string> SourceWorkItemIds { get; init; } = [];

    /// <summary>
    /// When the suggestion entered the <c>dismissed</c> state. Null for
    /// suggestions dismissed before this field existed — those rows are still
    /// eligible for merge matching regardless of the dismissal window.
    /// </summary>
    public DateTimeOffset? DismissedAt { get; init; }

    /// <summary>
    /// Normalized dedupe key (<see cref="SuggestionDedupe.ComputeDedupeKey"/>)
    /// persisted at insert so exact repeats match without rescanning; null on
    /// rows written before dedupe existed (still matched via similarity).
    /// </summary>
    public string? DedupeKey { get; init; }
}

namespace CodeyBox.Core;

/// <summary>
/// A single timing measurement for one step within a work-item pipeline phase.
/// Rows are written at step start (ended_at/duration_ms null) and updated on
/// completion. Rows left with null ended_at indicate in-flight or crashed steps.
/// </summary>
public sealed record TimingRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public WorkItemId WorkItemId { get; init; }
    public string Phase { get; init; } = "";
    public int? Iteration { get; init; }
    public string Step { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public long? DurationMs { get; init; }
    public string MetadataJson { get; init; } = "{}";
}

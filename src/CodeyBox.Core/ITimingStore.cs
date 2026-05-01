namespace CodeyBox.Core;

/// <summary>
/// Durable store for per-step wall-clock timing records. Writes are best-effort:
/// callers wrap with try/catch and log warnings on failure rather than propagating.
/// </summary>
public interface ITimingStore
{
    /// <summary>
    /// Persists a timing record with null ended_at / duration_ms (step started).
    /// </summary>
    Task BeginAsync(TimingRecord record, CancellationToken ct = default);

    /// <summary>
    /// Marks a timing record complete by setting ended_at and duration_ms.
    /// </summary>
    Task EndAsync(string id, DateTimeOffset endedAt, long durationMs, CancellationToken ct = default);

    /// <summary>Returns all timing records for a single work item, ordered by started_at.</summary>
    Task<IReadOnlyList<TimingRecord>> GetByWorkItemAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>Deletes all timing records for a work item (called when the item is removed).</summary>
    Task DeleteByWorkItemAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>
    /// Streams completed timing records (ended_at not null) for the most recent
    /// <paramref name="workItemLimit"/> Done work items, ordered by work_item_id then started_at.
    /// Used by the aggregate endpoint without loading all rows into memory.
    /// </summary>
    IAsyncEnumerable<TimingRecord> StreamCompletedAsync(int workItemLimit, CancellationToken ct = default);
}

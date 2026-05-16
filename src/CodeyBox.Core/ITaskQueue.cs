namespace CodeyBox.Core;

/// <summary>
/// In-process dispatch notification stream. The durable work item store is the
/// source of truth for queued work; this queue wakes the dispatcher when a row
/// may need pickup or re-evaluation.
/// </summary>
public interface ITaskQueue
{
    ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>
    /// Awaits the next dispatch kick. Returns null when the stream is closed.
    /// </summary>
    ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default);

    /// <summary>
    /// Number of buffered dispatch kicks, not the durable queued work-item count.
    /// </summary>
    int Count { get; }
}

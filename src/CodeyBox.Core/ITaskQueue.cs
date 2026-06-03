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
    /// Enqueues a non-item-specific dispatcher wake. Consumers should rescan the
    /// durable store without treating this as an explicit retry for any one item.
    /// </summary>
    ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default);

    /// <summary>
    /// Awaits the next item-specific dispatch kick. Returns null when the stream
    /// is closed or when the next buffered kick is generic.
    /// </summary>
    ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default);

    /// <summary>
    /// Awaits the next dispatch kick. Returns false when the stream is closed.
    /// The durable store remains the source of truth for which item to pick.
    /// </summary>
    async ValueTask<bool> DequeueDispatchSignalAsync(CancellationToken ct = default)
        => await DequeueAsync(ct) is not null;

    /// <summary>
    /// Number of buffered dispatch kicks, not the durable queued work-item count.
    /// </summary>
    int Count { get; }
}

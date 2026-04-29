namespace CodeyBox.Core;

/// <summary>
/// In-process task queue. Implementations may be backed by an in-memory
/// channel, a database-backed durable queue, or an external broker.
/// </summary>
public interface ITaskQueue
{
    ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>
    /// Awaits the next available work item. Returns null when the queue is closed.
    /// </summary>
    ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default);

    /// <summary>Number of items currently waiting in the queue.</summary>
    int Count { get; }
}

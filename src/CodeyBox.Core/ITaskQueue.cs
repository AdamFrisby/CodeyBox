namespace CodeyBox.Core;

public enum TaskQueueDispatchKind
{
    WorkItem,
    GenericWake,
}

public readonly record struct TaskQueueDispatch(TaskQueueDispatchKind Kind, WorkItemId? WorkItemId)
{
    public static TaskQueueDispatch ForWorkItem(WorkItemId id) =>
        new(TaskQueueDispatchKind.WorkItem, id);

    public static TaskQueueDispatch GenericWake { get; } =
        new(TaskQueueDispatchKind.GenericWake, null);
}

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
    /// Awaits the next dispatch kick, preserving whether it is item-specific or
    /// a generic rescan wake. Returns null when the stream is closed.
    /// </summary>
    ValueTask<TaskQueueDispatch?> DequeueDispatchAsync(CancellationToken ct = default);

    /// <summary>
    /// Number of buffered dispatch kicks, not the durable queued work-item count.
    /// </summary>
    int Count { get; }
}

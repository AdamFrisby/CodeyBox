namespace CodeyBox.Core;

/// <summary>
/// Durable store of work items. Survives orchestrator restart so in-flight
/// items can be recovered and replayed.
/// </summary>
public interface IWorkItemStore
{
    Task CreateAsync(WorkItem item, CancellationToken ct = default);
    Task UpdateAsync(WorkItem item, CancellationToken ct = default);
    /// <summary>Updates the item only when its persisted state still matches <paramref name="onlyIfState"/>. Returns true if the row was updated.</summary>
    Task<bool> TryUpdateIfStateAsync(WorkItem item, WorkItemState onlyIfState, CancellationToken ct = default);
    Task<WorkItem?> GetAsync(WorkItemId id, CancellationToken ct = default);
    IAsyncEnumerable<WorkItem> ListAsync(CancellationToken ct = default);
    IAsyncEnumerable<WorkItem> ListByStateAsync(WorkItemState state, CancellationToken ct = default);

    /// <summary>
    /// Sets <c>queue_position</c> for the listed Queued items in the given order
    /// (index + 1 becomes the position). Only rows still in <c>Queued</c> state
    /// are touched; items that raced to a non-Queued state are silently skipped.
    /// Runs inside a single transaction for atomicity.
    /// </summary>
    Task ReorderAsync(IReadOnlyList<WorkItemId> orderedIds, CancellationToken ct = default);
}

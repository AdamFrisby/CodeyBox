namespace CodeyBox.Core;

/// <summary>
/// Durable, append-only log of delegation turns. Separate from the mutable
/// delegation fields on <see cref="WorkItem"/> (attempt count, request flag)
/// so an operator can see afterwards exactly what each delegate was told and
/// what it did, even after later retries overwrote the item row.
/// </summary>
public interface IDelegationEventStore
{
    /// <summary>Appends one delegation event. The store bounds brief/diff length before insert.</summary>
    Task RecordAsync(DelegationEvent @event, CancellationToken ct = default);

    /// <summary>
    /// Returns all delegation events for a work item ordered by
    /// <see cref="DelegationEvent.Attempt"/> ascending.
    /// </summary>
    Task<IReadOnlyList<DelegationEvent>> ListByWorkItemAsync(
        WorkItemId workItemId,
        CancellationToken ct = default);
}

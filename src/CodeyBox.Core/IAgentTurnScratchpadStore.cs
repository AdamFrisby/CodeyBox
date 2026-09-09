namespace CodeyBox.Core;

/// <summary>
/// Host-private durable storage for agent CLI session archives. Archive bytes
/// never belong in the Git checkpoint; the Git ref carries only the content
/// binding represented by <see cref="AgentTurnCheckpointRef"/>.
/// </summary>
public interface IAgentTurnScratchpadStore
{
    /// <summary>
    /// Idempotently saves an archive under its immutable work-item/ref key.
    /// Implementations must reject a ref whose embedded archive hash differs
    /// from <paramref name="archive"/>.
    /// </summary>
    Task SaveAsync(
        WorkItemId workItemId,
        AgentTurnCheckpointRef checkpointRef,
        AgentTurnScratchpadArchive archive,
        CancellationToken ct = default);

    /// <summary>Reads and verifies an archive, or returns null when the key is absent.</summary>
    Task<AgentTurnScratchpadArchive?> ReadAsync(
        WorkItemId workItemId,
        AgentTurnCheckpointRef checkpointRef,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically publishes checkpoint metadata after its immutable archive and
    /// Git ref are durable. The write applies only to the exact work-item state
    /// and update stamp inspected by the caller, verifies the archive row in the
    /// same transaction, and removes every non-current archive for the item.
    /// Returns false when a concurrent lifecycle write won the compare-and-set.
    /// </summary>
    Task<bool> TryPublishAsync(
        WorkItem checkpointedItem,
        WorkItemState onlyIfState,
        DateTimeOffset onlyIfUpdatedAt,
        AgentTurnCheckpointRef checkpointRef,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically publishes a provider-owned retained-sandbox boundary when
    /// infrastructure prevented normal checkpoint capture. The global count
    /// predicate and exact work-item state/update compare-and-set execute in
    /// the same transaction so concurrent outages cannot exceed
    /// <paramref name="maximumRetainedSandboxes"/>.
    /// </summary>
    Task<bool> TryPublishRecoveryLeaseAsync(
        WorkItem retainedItem,
        WorkItemState onlyIfState,
        DateTimeOffset onlyIfUpdatedAt,
        int maximumRetainedSandboxes,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes one exact immutable archive key. Used to roll back a capture
    /// that failed before its checkpoint metadata became durable. A row already
    /// referenced by the work item's current non-null checkpoint metadata is
    /// never deleted.
    /// </summary>
    Task<int> DeleteAsync(
        WorkItemId workItemId,
        AgentTurnCheckpointRef checkpointRef,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes versions for <paramref name="workItemId"/> saved before
    /// <paramref name="keepRef"/>. The operation is a no-op when the keep ref
    /// has not been saved, preventing a stale caller from deleting newer data.
    /// The archive referenced by current non-null checkpoint metadata is never
    /// deleted, even when it is older than <paramref name="keepRef"/>.
    /// </summary>
    Task<int> DeleteOlderAsync(
        WorkItemId workItemId,
        AgentTurnCheckpointRef keepRef,
        CancellationToken ct = default);

    /// <summary>
    /// Deletes every unreferenced private scratchpad archive for one work item.
    /// The archive referenced by current non-null checkpoint metadata is kept.
    /// </summary>
    Task<int> DeleteAllAsync(WorkItemId workItemId, CancellationToken ct = default);
}

/// <summary>
/// Raised when persisted scratchpad metadata or bytes do not match their
/// immutable content-addressed checkpoint ref.
/// </summary>
public sealed class AgentTurnScratchpadCorruptException : IOException
{
    public AgentTurnScratchpadCorruptException(
        WorkItemId workItemId,
        AgentTurnCheckpointRef checkpointRef,
        string reason,
        Exception? innerException = null)
        : base(
            $"Agent-turn scratchpad for work item {workItemId} at {checkpointRef.Value} is corrupt: {reason}",
            innerException)
    {
        WorkItemId = workItemId;
        CheckpointRef = checkpointRef;
    }

    public WorkItemId WorkItemId { get; }
    public AgentTurnCheckpointRef CheckpointRef { get; }
}

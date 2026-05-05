namespace CodeyBox.Core;

/// <summary>
/// Abstracts live agent-stdout broadcasting to connected dashboard clients.
/// Implemented in the API layer using SignalR; null-safe callers in the
/// orchestrator layer treat null as a no-op.
/// </summary>
public interface IStdoutBroadcaster
{
    /// <summary>
    /// Broadcasts a redacted stdout chunk to all clients subscribed to the
    /// given work item, and appends it to the in-memory ring buffer for
    /// late-joining clients. Implementations must be thread-safe. Fire-and-
    /// forget: callers do not await the downstream hub send.
    /// </summary>
    void BroadcastChunk(WorkItemId workItemId, string phase, string chunk);

    /// <summary>
    /// Flushes any pending batched chunks and broadcasts a streamComplete
    /// event. Called when the work item transitions to a terminal state.
    /// </summary>
    Task CompleteAsync(WorkItemId workItemId);

    /// <summary>
    /// Returns the current ring-buffer contents for a work item (for
    /// late-joining clients), or null if the work item is unknown.
    /// </summary>
    string? GetTail(WorkItemId workItemId);
}

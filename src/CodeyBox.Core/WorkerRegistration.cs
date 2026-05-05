namespace CodeyBox.Core;

/// <summary>
/// Snapshot of a live worker registered in the worker_registry table.
/// Each in-process worker slot writes its own row; the <c>DeadWorkerReaper</c>
/// queries stale rows to detect crashed workers and recover orphaned items.
/// </summary>
public sealed record WorkerRegistration
{
    /// <summary>GUID assigned at registration time; new on each orchestrator start.</summary>
    public required string WorkerId { get; init; }

    public required string HostName { get; init; }
    public required int ProcessId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset LastHeartbeatAt { get; init; }

    /// <summary>
    /// ID of the work item currently held by this worker, or null when idle.
    /// Set atomically on pickup and cleared (via row deletion) on finish.
    /// </summary>
    public string? CurrentWorkItemId { get; init; }
}

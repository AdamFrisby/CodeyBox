namespace CodeyBox.Core;

/// <summary>
/// Persistent registry of live workers. Workers write a heartbeat row at
/// startup and keep it fresh via periodic updates; the reaper deletes rows
/// whose heartbeat has gone stale and recovers the orphaned work items.
/// </summary>
public interface IWorkerRegistry
{
    Task RegisterAsync(WorkerRegistration registration, CancellationToken ct = default);

    /// <summary>
    /// Updates <c>last_heartbeat_at</c> (and optionally <c>current_work_item_id</c>)
    /// for the given worker. Fail-soft: implementations must not throw on transient
    /// storage errors — the caller logs and retries on the next interval.
    /// </summary>
    Task HeartbeatAsync(string workerId, string? currentWorkItemId, CancellationToken ct = default);

    Task DeregisterAsync(string workerId, CancellationToken ct = default);

    Task<IReadOnlyList<WorkerRegistration>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically claims (DELETEs) every worker row whose
    /// <c>last_heartbeat_at</c> predates <paramref name="cutoff"/> and returns
    /// the deleted rows. The DELETE acts as a distributed lock: only the
    /// caller that successfully removes a row performs recovery for that
    /// worker, ensuring the reaper is idempotent across concurrent runs or
    /// restart races.
    /// </summary>
    Task<IReadOnlyList<WorkerRegistration>> ClaimDeadWorkersAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

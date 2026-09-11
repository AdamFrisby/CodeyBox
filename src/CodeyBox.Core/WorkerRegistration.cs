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

    /// <summary>
    /// Stable executor host id when this row was written by a remote executor
    /// host's registration (see <see cref="ExecutorRegistration"/>); null for
    /// in-process worker-slot rows. The worker id of executor rows is
    /// <c>executor:{host id}</c> so re-registration upserts the same row.
    /// </summary>
    public string? ExecutorHostId { get; init; }

    /// <summary>
    /// Declared host-local sandbox capacity, mirroring the per-host
    /// <c>MaxConcurrentSandboxes</c> placement attribute. Null means uncapped
    /// here; null for non-executor rows.
    /// </summary>
    public int? MaxConcurrentSandboxes { get; init; }

    /// <summary>
    /// Declared network profiles the executor accepts. Empty means the
    /// executor accepts all profiles. Null for non-executor rows.
    /// </summary>
    public IReadOnlyList<string>? ExecutorNetworkProfiles { get; init; }

    /// <summary>
    /// Names of the agent credential sets the executor declares it holds.
    /// Null for non-executor rows.
    /// </summary>
    public IReadOnlyList<string>? ExecutorCredentials { get; init; }

    /// <summary>
    /// Draining flag from the executor's registration. True means the host
    /// registers and heartbeats but is never selected for new placements.
    /// </summary>
    public bool Cordoned { get; init; }

    /// <summary>
    /// Operator health gate from the executor's registration. False routes new
    /// placements away without removing the registration.
    /// </summary>
    public bool Healthy { get; init; } = true;

    /// <summary>True when this row belongs to a remote executor host registration.</summary>
    public bool IsExecutor => ExecutorHostId is not null;
}

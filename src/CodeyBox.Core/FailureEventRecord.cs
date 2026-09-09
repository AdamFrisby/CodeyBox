namespace CodeyBox.Core;

/// <summary>
/// One durable, timestamped record of a work item entering (or re-failing
/// within) a failure/park state. Unlike the single mutable failure fields on
/// <see cref="WorkItem"/> — which a retry overwrites — these rows are
/// append-only, so failure rates and modes can be analysed historically.
///
/// Value-like and immutable; the store persists it verbatim (error text is
/// bounded at the store's write boundary).
/// </summary>
public sealed record FailureEventRecord
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public WorkItemId WorkItemId { get; init; }

    /// <summary>Agent selected for the failing phase, or null if none was routed.</summary>
    public string? Agent { get; init; }

    /// <summary>
    /// Lifecycle phase the item entered when it failed/parked — the persisted
    /// <see cref="WorkItemState"/> name (e.g. "Failed", "WaitingForQuotaReset").
    /// </summary>
    public string Phase { get; init; } = "";

    /// <summary>
    /// Audit/rework iteration, when the caller carries one. Null from the
    /// state-transition hook because the persisted <see cref="WorkItem"/> row
    /// has no per-iteration counter (iteration history lives in the audit
    /// progress store, not on the item).
    /// </summary>
    public int? Iteration { get; init; }

    /// <summary>Informational failure category — see <see cref="WorkItem.FailureKind"/>.</summary>
    public string? FailureKind { get; init; }

    /// <summary>Last error text at failure time. Truncated by the store before insert.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Sandbox / VM name the item carried, or null if none.</summary>
    public string? SandboxName { get; init; }

    /// <summary>Sandbox provider identity, or null if the item carries none.</summary>
    public string? Provider { get; init; }

    /// <summary>UTC instant the failure/park transition was persisted.</summary>
    public DateTimeOffset OccurredAt { get; init; }
}

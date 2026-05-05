namespace CodeyBox.Core;

/// <summary>
/// Distinguishes why a work item entered <see cref="WorkItemState.Cancelled"/>.
/// Stored as TEXT in the database; null means legacy row written before this
/// column existed (treat as ambiguous — see the startup WRN log).
/// </summary>
public enum WorkItemCancellationReason
{
    /// <summary>Operator hit DELETE /workitems/{id} or equivalent.</summary>
    OperatorRequested,

    /// <summary>A <c>dependsOn</c> parent ended in Cancelled state.</summary>
    ParentCascaded,

    /// <summary>
    /// Orchestrator process shut down while the item was in flight.
    /// Not written by the new code — items interrupted by host shutdown are
    /// left in their mid-flight state so recovery picks them up. This value
    /// is reserved for documentation and potential future diagnostic use.
    /// </summary>
    HostShutdown,
}

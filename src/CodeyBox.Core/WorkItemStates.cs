namespace CodeyBox.Core;

/// <summary>
/// Single source of truth for work-item state groupings that are policy rather
/// than mechanism.
/// </summary>
public static class WorkItemStates
{
    /// <summary>
    /// States from which a work item cannot exit without explicit operator
    /// action (retry / uncancel / resume). Anything NOT in this set is still
    /// live: it either runs, is parked awaiting a resource (quota, operator
    /// input, agent resume), or is queued. Callers that need "is this item
    /// finished?" must consult this set rather than re-listing the states, so a
    /// new terminal state is added in exactly one place.
    /// </summary>
    public static readonly IReadOnlySet<WorkItemState> Terminal =
        new HashSet<WorkItemState>
        {
            WorkItemState.Done,
            WorkItemState.Failed,
            WorkItemState.AuditFailed,
            WorkItemState.Cancelled,
            WorkItemState.MergeConflictResolutionFailed,
            WorkItemState.AbandonedAfterRecoveryAttempts,
        };

    /// <summary>True when <paramref name="state"/> is a terminal state.</summary>
    public static bool IsTerminal(WorkItemState state) => Terminal.Contains(state);
}

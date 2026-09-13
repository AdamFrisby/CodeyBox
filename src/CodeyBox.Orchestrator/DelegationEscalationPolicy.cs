using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure eligibility gates for delegation triggers. Single source of truth so
/// the operator endpoint, the audit-max hook, and the terminal-failure sweep
/// cannot drift on who may delegate what.
/// </summary>
public static class DelegationEscalationPolicy
{
    /// <summary>
    /// States that carry nothing to delegate: succeeded, operator-stopped, or
    /// resolved as no-action-required. Every other state — any non-terminal
    /// state plus the terminal failure states — may delegate.
    /// </summary>
    public static bool IsDelegableState(WorkItemState state) =>
        state is not (WorkItemState.Done or WorkItemState.Cancelled or WorkItemState.NoActionRequired);

    /// <summary>
    /// Terminal failure states an item may delegate from: the three
    /// terminal-failure states plus abandonment after exhausted recovery.
    /// </summary>
    public static bool IsTerminalFailureState(WorkItemState state) =>
        state is WorkItemState.Failed
            or WorkItemState.AuditFailed
            or WorkItemState.MergeConflictResolutionFailed
            or WorkItemState.AbandonedAfterRecoveryAttempts;

    /// <summary>
    /// Whether the item may escalate automatically: it has not already done
    /// so (at most once per item), and no delegation turn has completed
    /// without advancing it (a proven-unhelpful delegation never re-arms the
    /// automatic path). Operator delegation is unaffected.
    /// </summary>
    public static bool CanAutoEscalate(WorkItem item) =>
        !item.AutoDelegationEscalated && !item.DelegationFailed;
}

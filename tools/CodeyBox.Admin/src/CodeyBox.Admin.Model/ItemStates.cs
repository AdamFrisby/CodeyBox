namespace CodeyBox.Admin.Model;

/// <summary>
/// Single source of truth for orchestrator lifecycle-state classification.
/// Closed mirror of the <c>WorkItemState</c> names; compared by exact ordinal
/// match, never substring. Reused by activity, vitals, and attention so the
/// three screens can never disagree about what a state means.
/// </summary>
public static class ItemStates
{
    public static readonly IReadOnlySet<string> Failed = new HashSet<string>(StringComparer.Ordinal)
    {
        "Failed", "AuditFailed", "MergeConflictResolutionFailed", "AbandonedAfterRecoveryAttempts",
    };

    public static readonly IReadOnlySet<string> Parked = new HashSet<string>(StringComparer.Ordinal)
    {
        "NeedsOperatorInput", "WaitingForQuotaReset", "WaitingForAgentResume", "WaitingForTransientRetry",
    };

    /// <summary>
    /// Only successful completion satisfies the dependsOn gate — the
    /// orchestrator's own rule (<c>WorkItemDependencies.SatisfyingStates</c>:
    /// Done only). A Failed parent blocks its dependents; the dependent would
    /// otherwise burn agent quota on work that cannot validate end-to-end.
    /// </summary>
    public static readonly IReadOnlySet<string> DependencySatisfying = new HashSet<string>(StringComparer.Ordinal)
    {
        "Done",
    };

    public static readonly IReadOnlySet<string> Succeeded = new HashSet<string>(StringComparer.Ordinal)
    {
        "Done", "NoActionRequired",
    };

    public static readonly IReadOnlySet<string> KnownInFlight = new HashSet<string>(StringComparer.Ordinal)
    {
        "Working", "WorkComplete", "Auditing", "Reworking", "AuditPassed",
        "Merging", "Merged", "UpstreamPushing", "Planning", "PlanReview",
        "PlanApproved", "Delegating", "ReworkingForConflict",
    };

    public static bool IsQueued(string? state) =>
        string.Equals(state, "Queued", StringComparison.Ordinal);

    public static bool IsTerminal(string? state) =>
        state is not null && (Failed.Contains(state) || Succeeded.Contains(state) || state == "Cancelled");

    public static bool IsFailedTerminal(string? state) =>
        state is not null && Failed.Contains(state);
}

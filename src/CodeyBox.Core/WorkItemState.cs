namespace CodeyBox.Core;

/// <summary>
/// Lifecycle states for a work item. Work + Audit (with rework iterations)
/// + Merge together form the atomic unit; UpstreamPush is a separate,
/// retryable post-success step.
/// </summary>
public enum WorkItemState
{
    Queued = 0,
    Working = 1,
    WorkComplete = 2,
    Auditing = 7,
    Reworking = 8,
    AuditPassed = 9,
    Merging = 3,
    Merged = 4,
    UpstreamPushing = 5,
    Done = 6,
    NeedsOperatorInput = 10,
    /// <summary>
    /// All eligible members of the agent class hit quota during this iteration,
    /// or proactive headroom projection found that dispatch would likely cross
    /// the configured quota floor. The work item is parked (not Failed) and
    /// re-enqueued by the quota retry scheduler when a probe shows any member is
    /// available again.
    /// Distinct from <see cref="Failed"/> + FailureKind="quota" because that
    /// path captures an item that *already failed* a single-agent attempt;
    /// this state captures items that exhausted every fallback in one pickup.
    /// </summary>
    WaitingForQuotaReset = 11,
    Failed = 100,
    Cancelled = 101,
    AuditFailed = 102,
    MergeConflictResolutionFailed = 104,
    /// <summary>
    /// The recovery loop has retried this item <c>MaxRecoveryAttempts</c> times
    /// after successive host shutdowns and it has never completed. Operator
    /// intervention required; use <c>POST /workitems/{id}/retry</c> to resume
    /// manually after investigating.
    /// </summary>
    AbandonedAfterRecoveryAttempts = 103,
}

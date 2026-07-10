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
    /// All eligible members of the agent class hit quota during this
    /// iteration. The work item is parked (not Failed) and re-enqueued by the
    /// quota retry scheduler when a probe shows any member is available again.
    /// Distinct from <see cref="Failed"/> + FailureKind="quota" because that
    /// path captures an item that *already failed* a single-agent attempt;
    /// this state captures items that exhausted every fallback in one pickup.
    /// </summary>
    WaitingForQuotaReset = 11,
    /// <summary>
    /// Third-line fallback for merge-phase conflicts: the original work agent
    /// is being re-engaged with a focused conflict-resolution prompt on the
    /// existing work branch (commits intact). Distinct from
    /// <see cref="Reworking"/>, which is part of the audit/rework loop and
    /// pre-merge; this state is post-merge-phase, after both the preventive
    /// auto-rebase and the merge-phase LLM rerun have already failed.
    /// </summary>
    ReworkingForConflict = 12,
    /// <summary>
    /// Every agent that can take this item is currently paused by an operator.
    /// The item is parked, not failed, and is re-enqueued automatically when
    /// agent pause state changes. Distinct from <see cref="WaitingForQuotaReset"/>
    /// so operator intent is visible and quota auto-retry accounting is not used.
    /// </summary>
    WaitingForAgentResume = 13,
    /// <summary>
    /// The last agent attempt failed due to a transient transport/network
    /// condition. The item is parked, not terminal, and the transient retry
    /// scheduler re-enqueues it after the configured backoff+jitter delay.
    /// </summary>
    WaitingForTransientRetry = 14,
    /// <summary>
    /// Optional planning-only agent turn is running. This phase asks the
    /// selected work agent to produce a reviewable plan artifact and does not
    /// import commits or file changes.
    /// </summary>
    Planning = 15,
    /// <summary>
    /// A planning artifact is undergoing one active auditor review pass.
    /// Rejection transitions the item back to <see cref="Planning"/> for a
    /// rework turn; approval advances it to <see cref="PlanApproved"/>.
    /// </summary>
    PlanReview = 16,
    /// <summary>
    /// Planning artifact has passed review and the item may enter the normal
    /// work/audit/merge lifecycle.
    /// </summary>
    PlanApproved = 17,
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

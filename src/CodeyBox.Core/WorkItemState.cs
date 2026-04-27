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
    Failed = 100,
    Cancelled = 101,
    AuditFailed = 102,
}

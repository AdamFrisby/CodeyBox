using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class AgentPauseResumeMapper
{
    public static string NormalizeRetryFrom(string? retryFrom) =>
        RetryFromPolicy.NormalizeOrWork(retryFrom);

    public static string RetryFromForState(WorkItemState state) => state switch
    {
        WorkItemState.Planning => RetryFromPolicy.Planning,
        WorkItemState.PlanReview => RetryFromPolicy.PlanReview,
        WorkItemState.PlanApproved => RetryFromPolicy.PlanApproved,
        WorkItemState.WorkComplete => RetryFromPolicy.Audit,
        WorkItemState.Auditing => RetryFromPolicy.Audit,
        WorkItemState.Reworking => RetryFromPolicy.Audit,
        WorkItemState.ReworkingForConflict => RetryFromPolicy.ConflictRework,
        WorkItemState.AuditFailed => RetryFromPolicy.Audit,
        WorkItemState.AuditPassed => RetryFromPolicy.Merge,
        WorkItemState.Merging => RetryFromPolicy.Merge,
        WorkItemState.Merged => RetryFromPolicy.Upstream,
        WorkItemState.UpstreamPushing => RetryFromPolicy.Upstream,
        _ => RetryFromPolicy.Work,
    };

    public static WorkItemState ResumeStateForRetryFrom(string? retryFrom) =>
        RetryFromPolicy.ResumeStateForRetryFrom(retryFrom);
}

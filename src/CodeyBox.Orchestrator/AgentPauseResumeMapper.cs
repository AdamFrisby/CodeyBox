using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class AgentPauseResumeMapper
{
    public static string NormalizeRetryFrom(string? retryFrom) =>
        retryFrom?.Trim().ToLowerInvariant() switch
        {
            "planning" => "planning",
            "plan_review" => "plan_review",
            "plan_approved" => "plan_approved",
            "audit" => "audit",
            "conflict_rework" => "conflict_rework",
            "merge" => "merge",
            "upstream" => "upstream",
            _ => "work",
        };

    public static string RetryFromForState(WorkItemState state) => state switch
    {
        WorkItemState.Planning => "planning",
        WorkItemState.PlanReview => "plan_review",
        WorkItemState.PlanApproved => "plan_approved",
        WorkItemState.WorkComplete => "audit",
        WorkItemState.Auditing => "audit",
        WorkItemState.Reworking => "audit",
        WorkItemState.ReworkingForConflict => "conflict_rework",
        WorkItemState.AuditFailed => "audit",
        WorkItemState.AuditPassed => "merge",
        WorkItemState.Merging => "merge",
        WorkItemState.Merged => "upstream",
        WorkItemState.UpstreamPushing => "upstream",
        _ => "work",
    };

    public static WorkItemState ResumeStateForRetryFrom(string? retryFrom) =>
        NormalizeRetryFrom(retryFrom) switch
        {
            "planning" => WorkItemState.Queued,
            "plan_review" => WorkItemState.PlanReview,
            "plan_approved" => WorkItemState.PlanApproved,
            "audit" => WorkItemState.WorkComplete,
            "conflict_rework" => WorkItemState.ReworkingForConflict,
            "merge" => WorkItemState.AuditPassed,
            "upstream" => WorkItemState.Merged,
            _ => WorkItemState.Queued,
        };
}

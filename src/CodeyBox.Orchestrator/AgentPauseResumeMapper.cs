using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class AgentPauseResumeMapper
{
    public static string NormalizeRetryFrom(string? retryFrom) =>
        retryFrom?.Trim().ToLowerInvariant() switch
        {
            "audit" => "audit",
            "conflict_rework" => "conflict_rework",
            "merge" => "merge",
            "upstream" => "upstream",
            _ => "work",
        };

    public static string RetryFromForState(WorkItemState state) => state switch
    {
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
            "audit" => WorkItemState.WorkComplete,
            "conflict_rework" => WorkItemState.ReworkingForConflict,
            "merge" => WorkItemState.AuditPassed,
            "upstream" => WorkItemState.Merged,
            _ => WorkItemState.Queued,
        };
}

using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class AgentPauseResumeMapper
{
    public static string RetryFromForState(WorkItemState state) => state switch
    {
        WorkItemState.WorkComplete => "audit",
        WorkItemState.Auditing => "audit",
        WorkItemState.Reworking => "audit",
        WorkItemState.ReworkingForConflict => "audit",
        WorkItemState.AuditFailed => "audit",
        WorkItemState.AuditPassed => "merge",
        WorkItemState.Merging => "merge",
        WorkItemState.Merged => "upstream",
        WorkItemState.UpstreamPushing => "upstream",
        _ => "work",
    };

    public static WorkItemState ResumeStateForRetryFrom(string? retryFrom) =>
        retryFrom?.Trim().ToLowerInvariant() switch
        {
            "audit" => WorkItemState.WorkComplete,
            "merge" => WorkItemState.AuditPassed,
            "upstream" => WorkItemState.Merged,
            _ => WorkItemState.Queued,
        };
}

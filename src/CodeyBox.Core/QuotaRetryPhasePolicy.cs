namespace CodeyBox.Core;

/// <summary>
/// Owns the quota-retry phase mapping used when a parked item is ordered for
/// dispatch, resumed, and routed through capability-gated members.
/// </summary>
public static class QuotaRetryPhasePolicy
{
    public static string NormalizePhase(string? phase) =>
        phase?.Trim().ToLowerInvariant() switch
        {
            "planning" => "planning",
            "audit" => "audit",
            "rework" => "rework",
            "merge" => "merge",
            "upstream" => "upstream",
            _ => "work",
        };

    public static string RetryFromForPhase(string? phase) => NormalizePhase(phase) switch
    {
        "planning" => "planning",
        "audit" => "audit",
        "rework" => "audit",
        "merge" => "merge",
        "upstream" => "upstream",
        _ => "work",
    };

    public static string NormalizeRetryFrom(string? retryFrom) =>
        retryFrom?.Trim().ToLowerInvariant() switch
        {
            "planning" => "planning",
            "audit" => "audit",
            "conflict_rework" => "conflict_rework",
            "merge" => "merge",
            "upstream" => "upstream",
            _ => "work",
        };

    public static WorkItemState ResumeStateForRetryFrom(string? retryFrom) =>
        NormalizeRetryFrom(retryFrom) switch
        {
            "planning" => WorkItemState.Queued,
            "audit" => WorkItemState.WorkComplete,
            "conflict_rework" => WorkItemState.ReworkingForConflict,
            "merge" => WorkItemState.AuditPassed,
            "upstream" => WorkItemState.Merged,
            _ => WorkItemState.Queued,
        };

    public static WorkItemState OrderingStateForQuotaRetryCandidate(WorkItem item)
    {
        var retryFrom = !string.IsNullOrWhiteSpace(item.QuotaRetryPhase)
            ? RetryFromForPhase(item.QuotaRetryPhase)
            : NormalizeRetryFrom(item.QuotaRetryFrom);

        return ResumeStateForRetryFrom(retryFrom);
    }

    public static int OrderingStateForQuotaRetryCandidate(string? quotaRetryPhase, string? quotaRetryFrom)
    {
        var retryFrom = !string.IsNullOrWhiteSpace(quotaRetryPhase)
            ? RetryFromForPhase(quotaRetryPhase)
            : NormalizeRetryFrom(quotaRetryFrom);

        return (int)ResumeStateForRetryFrom(retryFrom);
    }

    public static int DispatchPhaseBucket(WorkItemState state) =>
        state is WorkItemState.AuditPassed
            or WorkItemState.Merging
            or WorkItemState.Merged
            or WorkItemState.UpstreamPushing
            ? 0
            : 1;

    public static string? RequiredCapabilityForQuotaRetryCandidate(WorkItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.QuotaRetryPhase))
            return RequiredCapabilityForPhase(item.QuotaRetryPhase);

        return NormalizeRetryFrom(item.QuotaRetryFrom) == "audit"
            ? WellKnownCapabilities.Audit
            : null;
    }

    public static string? RequiredCapabilityForDispatchAdmission(WorkItem item)
    {
        if (item.State == WorkItemState.WaitingForQuotaReset)
            return RequiredCapabilityForQuotaRetryCandidate(item);

        return item.State == WorkItemState.WorkComplete
            ? WellKnownCapabilities.Audit
            : null;
    }

    public static string? RequiredCapabilityForPhase(string? phase) =>
        string.Equals(NormalizePhase(phase), "audit", StringComparison.Ordinal)
            ? WellKnownCapabilities.Audit
            : null;
}

namespace CodeyBox.Core;

/// <summary>
/// Owns the quota-retry phase mapping used when a parked item is ordered for
/// dispatch, resumed, and routed through capability-gated members.
/// </summary>
public static class QuotaRetryPhasePolicy
{
    public const string PlanningPhase = RetryFromPolicy.Planning;
    public const string AuditPhase = RetryFromPolicy.Audit;
    public const string ReworkPhase = RetryFromPolicy.Rework;
    public const string MergePhase = RetryFromPolicy.Merge;
    public const string UpstreamPhase = RetryFromPolicy.Upstream;
    public const string WorkPhase = RetryFromPolicy.Work;

    /// <summary>
    /// Normalizes quota park phase labels. Supported phase labels are
    /// <c>planning</c>, <c>audit</c>, <c>rework</c>, <c>merge</c>, and
    /// <c>upstream</c>; unknown historical values fall back to <c>work</c>.
    /// </summary>
    public static string NormalizePhase(string? phase) =>
        phase?.Trim().ToLowerInvariant() switch
        {
            PlanningPhase => PlanningPhase,
            AuditPhase => AuditPhase,
            ReworkPhase => ReworkPhase,
            MergePhase => MergePhase,
            UpstreamPhase => UpstreamPhase,
            _ => WorkPhase,
        };

    public static string RetryFromForPhase(string? phase) => NormalizePhase(phase) switch
    {
        PlanningPhase => RetryFromPolicy.Planning,
        AuditPhase => RetryFromPolicy.Audit,
        ReworkPhase => RetryFromPolicy.Audit,
        MergePhase => RetryFromPolicy.Merge,
        UpstreamPhase => RetryFromPolicy.Upstream,
        _ => RetryFromPolicy.Work,
    };

    /// <summary>
    /// Normalizes persisted retry-from values using the shared retry contract.
    /// Unknown historical values fall back to <c>work</c>.
    /// </summary>
    public static string NormalizeRetryFrom(string? retryFrom) =>
        RetryFromPolicy.NormalizeOrWork(retryFrom);

    public static WorkItemState ResumeStateForRetryFrom(string? retryFrom) =>
        RetryFromPolicy.ResumeStateForRetryFrom(retryFrom);

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

        return ResumeStateForRetryFrom(item.QuotaRetryFrom) == WorkItemState.WorkComplete
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
        string.Equals(NormalizePhase(phase), AuditPhase, StringComparison.Ordinal)
            ? WellKnownCapabilities.Audit
            : null;
}

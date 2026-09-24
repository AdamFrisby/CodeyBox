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
    public const string DelegationPhase = RetryFromPolicy.Delegation;
    public const string MergePhase = RetryFromPolicy.Merge;
    public const string UpstreamPhase = RetryFromPolicy.Upstream;
    public const string WorkPhase = RetryFromPolicy.Work;

    /// <summary>
    /// Normalizes quota park phase labels. Supported phase labels are
    /// <c>planning</c>, <c>audit</c>, <c>rework</c>, <c>delegation</c>,
    /// <c>merge</c>, and <c>upstream</c>; unknown historical values fall back
    /// to <c>work</c>.
    /// </summary>
    public static string NormalizePhase(string? phase) =>
        phase?.Trim().ToLowerInvariant() switch
        {
            PlanningPhase => PlanningPhase,
            AuditPhase => AuditPhase,
            ReworkPhase => ReworkPhase,
            DelegationPhase => DelegationPhase,
            MergePhase => MergePhase,
            UpstreamPhase => UpstreamPhase,
            _ => WorkPhase,
        };

    public static string RetryFromForPhase(string? phase) => NormalizePhase(phase) switch
    {
        PlanningPhase => RetryFromPolicy.Planning,
        AuditPhase => RetryFromPolicy.Audit,
        ReworkPhase => RetryFromPolicy.Audit,
        DelegationPhase => RetryFromPolicy.Delegation,
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

    /// <summary>
    /// Dispatch-eligible states that count as "past the work phase": the item
    /// already consumed pipeline effort and only needs a continuation (audit,
    /// rework, merge, upstream push, or an in-flight delegation turn) to
    /// drain. Fresh starts — <see cref="WorkItemState.Queued"/>, the planning
    /// lifecycle states, and <see cref="WorkItemState.Working"/> re-pickups
    /// (in the work phase, not past it) — sort behind these at equal priority
    /// under <see cref="DispatchCandidateOrdering.InFlightBeforeFresh"/>.
    /// The post-audit finishing states are deliberately also members: the
    /// <see cref="DispatchPhaseBucket"/> precedence is evaluated first, so
    /// listing them here keeps the bucket honest without double-counting.
    /// </summary>
    public static IReadOnlyList<WorkItemState> InFlightDispatchStates { get; } =
    [
        WorkItemState.WorkComplete,
        WorkItemState.Auditing,
        WorkItemState.Reworking,
        WorkItemState.ReworkingForConflict,
        WorkItemState.AuditPassed,
        WorkItemState.Merging,
        WorkItemState.Merged,
        WorkItemState.UpstreamPushing,
        WorkItemState.Delegating,
    ];

    /// <summary>
    /// Tie-break bucket used by
    /// <see cref="DispatchCandidateOrdering.InFlightBeforeFresh"/> after
    /// priority: 0 for states in <see cref="InFlightDispatchStates"/>, 1 for
    /// fresh starts.
    /// </summary>
    public static int DispatchInFlightBucket(WorkItemState state) =>
        InFlightDispatchStates.Contains(state) ? 0 : 1;

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

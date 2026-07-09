namespace CodeyBox.Core;

/// <summary>
/// Pure eligibility rules for agent-restore retry sweeps. Stores use this
/// policy to bound pre-limit candidate selection, and the scheduler uses it as
/// the final guard before claiming and requeueing a work item.
/// </summary>
public static class AgentRestoreRetryCandidatePolicy
{
    public static bool IsTerminalRetryState(WorkItemState state) =>
        state is WorkItemState.Failed or WorkItemState.MergeConflictResolutionFailed;

    public static bool IsEligibleFailure(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return IsEligibleFailure(item.FailureKind, item.AuthFailureScope);
    }

    public static bool IsEligibleFailure(
        string? failureKind,
        WorkItemAuthFailureScope? authFailureScope)
    {
        if (!WorkItemFailureKinds.IsInfraShaped(failureKind))
            return false;

        if (!string.Equals(failureKind, WorkItemFailureKinds.AuthRequired, StringComparison.OrdinalIgnoreCase))
            return true;

        return authFailureScope == WorkItemAuthFailureScope.Fleet;
    }

    public static bool IsEligible(
        WorkItem item,
        AgentKind restoredAgent,
        AgentKind? latestFailedInvolvementAgent)
    {
        ArgumentNullException.ThrowIfNull(item);
        return IsEligible(
            item.State,
            item.FailureKind,
            item.AuthFailureScope,
            item.Agent,
            restoredAgent,
            latestFailedInvolvementAgent);
    }

    public static bool IsEligible(
        WorkItemState state,
        string? failureKind,
        WorkItemAuthFailureScope? authFailureScope,
        AgentKind? itemAgent,
        AgentKind restoredAgent,
        AgentKind? latestFailedInvolvementAgent)
    {
        if (!IsTerminalRetryState(state))
            return false;

        if (!IsEligibleFailure(failureKind, authFailureScope))
            return false;

        if (latestFailedInvolvementAgent is { } failedAgent)
            return AgentMatches(failedAgent, restoredAgent);

        if (itemAgent is not { } agent || !AgentMatches(agent, restoredAgent))
            return false;

        return AllowsWorkItemAgentFallback(failureKind, authFailureScope);
    }

    public static bool AgentMatches(AgentKind candidate, AgentKind restoredAgent) =>
        string.Equals(candidate.Value, restoredAgent.Value, StringComparison.OrdinalIgnoreCase);

    public static bool AllowsWorkItemAgentFallback(WorkItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return AllowsWorkItemAgentFallback(item.FailureKind, item.AuthFailureScope);
    }

    public static bool AllowsWorkItemAgentFallback(
        string? failureKind,
        WorkItemAuthFailureScope? authFailureScope)
    {
        return string.Equals(failureKind, WorkItemFailureKinds.AgentUnavailable, StringComparison.OrdinalIgnoreCase)
            || (string.Equals(failureKind, WorkItemFailureKinds.AuthRequired, StringComparison.OrdinalIgnoreCase)
                && authFailureScope == WorkItemAuthFailureScope.Fleet);
    }

    public static AgentKind? LatestFailedInvolvementAgent(
        IEnumerable<AgentInvolvement> involvements,
        DateTimeOffset terminalUpdatedAt,
        TimeSpan terminalLookback,
        TimeSpan terminalClockSkew,
        Func<string?, bool> isFailedOutcome)
    {
        ArgumentNullException.ThrowIfNull(involvements);
        ArgumentNullException.ThrowIfNull(isFailedOutcome);

        return involvements
            .Where(row => isFailedOutcome(row.Outcome)
                && IsNearTerminalUpdate(row.EndedAt ?? row.StartedAt, terminalUpdatedAt, terminalLookback, terminalClockSkew))
            .OrderByDescending(static row => row.EndedAt ?? row.StartedAt)
            .ThenByDescending(static row => row.StartedAt)
            .ThenByDescending(static row => row.Id)
            .FirstOrDefault()
            ?.AgentKind;
    }

    public static bool IsNearTerminalUpdate(
        DateTimeOffset involvementAt,
        DateTimeOffset terminalUpdatedAt,
        TimeSpan terminalLookback,
        TimeSpan terminalClockSkew) =>
        involvementAt >= terminalUpdatedAt - terminalLookback
        && involvementAt <= terminalUpdatedAt + terminalClockSkew;
}

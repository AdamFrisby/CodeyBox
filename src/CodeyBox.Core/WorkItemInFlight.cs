using System;
using System.Collections.Generic;

namespace CodeyBox.Core;

/// <summary>
/// Shared definition of the rows that count as actively in flight for
/// per-project dispatch gates.
/// </summary>
public static class WorkItemInFlight
{
    private static readonly WorkItemState[] ExcludedStatesArray =
    [
        WorkItemState.Done,
        WorkItemState.Failed,
        WorkItemState.Cancelled,
        WorkItemState.AuditFailed,
        WorkItemState.MergeConflictResolutionFailed,
        WorkItemState.NeedsOperatorInput,
        WorkItemState.WaitingForQuotaReset,
        WorkItemState.WaitingForAgentResume,
        WorkItemState.AbandonedAfterRecoveryAttempts,
    ];

    public static IReadOnlyList<WorkItemState> ExcludedStates => ExcludedStatesArray;

    public static bool IsInFlight(WorkItem item) =>
        item.StartedAt is not null
        && item.PreemptCheckpoint is null
        && !IsExcludedState(item.State);

    public static bool IsExcludedState(WorkItemState state) =>
        Array.IndexOf(ExcludedStatesArray, state) >= 0;
}

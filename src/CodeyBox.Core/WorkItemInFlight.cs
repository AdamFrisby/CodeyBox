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
        WorkItemState.WaitingForTransientRetry,
        WorkItemState.AbandonedAfterRecoveryAttempts,
        WorkItemState.NoActionRequired,
    ];

    public static IReadOnlyList<WorkItemState> ExcludedStates => ExcludedStatesArray;

    public static bool IsInFlight(WorkItem item) =>
        item.StartedAt is not null
        && !item.HasAgentTurnRecoveryBoundary
        && !IsExcludedState(item.State);

    /// <summary>
    /// Whether a worker currently holds the item. The durable proxy for a
    /// bound worker is <see cref="WorkItem.StartedAt"/>: dispatch writes it
    /// inside the pickup lock before the pipeline runs, and retry/recovery
    /// clear it when no worker holds the row. A durable agent-turn resume
    /// that was retried into <see cref="WorkItemState.Working"/> (or
    /// <see cref="WorkItemState.Reworking"/>) therefore reports not-running
    /// until a worker picks it up — unlike <see cref="IsInFlight"/>, a
    /// running resume (started, boundary still attached until the phase
    /// completes) counts as running here because a worker does hold it.
    /// Terminal and parked states never count, even with a stale stamp.
    /// </summary>
    public static bool IsRunning(WorkItem item) =>
        item.StartedAt is not null
        && !IsExcludedState(item.State);

    /// <summary>
    /// Whether the item sits in a worker-occupiable lifecycle state with a
    /// recovery boundary but no worker holding it: e.g. a durable agent-turn
    /// checkpoint retried into <see cref="WorkItemState.Working"/> while it
    /// waits for a dispatch slot. Renders as "waiting to resume", not running.
    /// </summary>
    public static bool HasPendingResume(WorkItem item) =>
        !IsRunning(item)
        && !IsExcludedState(item.State)
        && item.State != WorkItemState.Queued
        && item.HasAgentTurnRecoveryBoundary;

    public static bool IsExcludedState(WorkItemState state) =>
        Array.IndexOf(ExcludedStatesArray, state) >= 0;
}

using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class WorkItemRecoveryPolicy
{
    public static bool RequiresPipelinePreemptCheckpointBeforeLifecycleTeardown(WorkItem item) =>
        item.JobType != JobType.CheckAndAct
        && item.State is (WorkItemState.Working or WorkItemState.Reworking)
        && string.IsNullOrWhiteSpace(item.PreemptCheckpoint);

    public static bool IsRerunnableCheckAndActWithoutPreempt(WorkItem item) =>
        item.JobType == JobType.CheckAndAct
        && item.State == WorkItemState.Working
        && string.IsNullOrWhiteSpace(item.PreemptCheckpoint);

    public static WorkItem BuildCheckAndActRerun(WorkItem item, int recoveryAttempts) => item with
    {
        State = WorkItemState.Queued,
        LastError = null,
        RecoveryAttempts = recoveryAttempts,
        StartedAt = null,
        PreemptedAt = null,
        PreemptCheckpoint = null,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    public static bool TryBuildWorkingWithoutPreemptFailure(
        WorkItem item,
        string lastError,
        out WorkItem failed)
    {
        if (IsRerunnableCheckAndActWithoutPreempt(item)
            || item.State != WorkItemState.Working
            || !string.IsNullOrWhiteSpace(item.PreemptCheckpoint))
        {
            failed = item;
            return false;
        }

        failed = item with
        {
            State = WorkItemState.Failed,
            LastError = lastError,
            RecoveryAttempts = item.RecoveryAttempts + 1,
            StartedAt = null,
            PreemptedAt = null,
            PreemptCheckpoint = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        return true;
    }

    public static WorkItem? BuildGracefulShutdownRecoveryState(
        WorkItem item,
        DateTimeOffset now,
        string recoveryReason = "graceful shutdown drain timed out")
    {
        if (!string.IsNullOrWhiteSpace(item.SuspendedVmName))
            return null;

        if (!string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && item.State is WorkItemState.Working or WorkItemState.Reworking)
        {
            return item with
            {
                StartedAt = null,
                UpdatedAt = now,
            };
        }

        var target = item.State == WorkItemState.Working
            ? WorkItemState.Queued
            : MapToRecoveryState(item.State);

        if (target is null)
            return null;

        var error = target == WorkItemState.Queued
            ? $"{recoveryReason} while item was {item.State}; re-queued for a fresh run"
            : null;

        return item.With(target.Value, error) with
        {
            StartedAt = target == WorkItemState.Queued ? null : item.StartedAt,
            UpdatedAt = now,
        };
    }

    public static bool HandlesRecoveryState(WorkItemState state)
        => state == WorkItemState.Working || MapToRecoveryState(state) is not null;

    /// <summary>
    /// Maps a state for which a stale worker row could exist to the state the
    /// recovery layer should redispatch it into. Mid-flight states map back to
    /// durable resume points; phase-boundary resting states map to themselves.
    /// Returns null for terminal, parked, or otherwise dispatcher-owned states.
    /// </summary>
    public static WorkItemState? MapToRecoveryState(WorkItemState state) => state switch
    {
        WorkItemState.Reworking => WorkItemState.Queued,
        WorkItemState.WorkComplete => WorkItemState.WorkComplete,
        WorkItemState.Auditing => WorkItemState.WorkComplete,
        WorkItemState.AuditPassed => WorkItemState.AuditPassed,
        WorkItemState.Merging => WorkItemState.AuditPassed,
        WorkItemState.Merged => WorkItemState.Merged,
        WorkItemState.ReworkingForConflict => WorkItemState.AuditPassed,
        WorkItemState.UpstreamPushing => WorkItemState.Merged,
        _ => null,
    };
}

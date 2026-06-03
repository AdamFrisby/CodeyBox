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
        DateTimeOffset now)
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
            : DeadWorkerReaper.MapToRecoveryState(item.State);

        if (target is null)
            return null;

        var error = target == WorkItemState.Queued
            ? $"graceful shutdown drain timed out while item was {item.State}; re-queued for a fresh run"
            : null;

        return item.With(target.Value, error) with
        {
            StartedAt = target == WorkItemState.Queued ? null : item.StartedAt,
            UpdatedAt = now,
        };
    }
}

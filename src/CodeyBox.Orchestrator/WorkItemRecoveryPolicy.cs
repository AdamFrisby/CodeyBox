using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class WorkItemRecoveryPolicy
{
    public static bool TryBuildWorkingWithoutPreemptFailure(
        WorkItem item,
        string lastError,
        out WorkItem failed)
    {
        if (item.State != WorkItemState.Working
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
}

using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class WorkItemRecoveryPolicy
{
    public static bool RequiresPipelinePreemptCheckpointBeforeLifecycleTeardown(WorkItem item) =>
        item.JobType is not JobType.CheckAndAct and not JobType.AgentControl
        && item.State is (WorkItemState.Working or WorkItemState.Reworking)
        && string.IsNullOrWhiteSpace(item.PreemptCheckpoint);

    public static bool IsRerunnableCheckAndActWithoutPreempt(WorkItem item) =>
        item.JobType == JobType.CheckAndAct
        && item.State == WorkItemState.Working
        && string.IsNullOrWhiteSpace(item.PreemptCheckpoint);

    public static bool IsRerunnableAgentControlWithoutPreempt(WorkItem item) =>
        item.JobType == JobType.AgentControl
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

    public static WorkItem BuildAgentControlRerun(WorkItem item, int recoveryAttempts) => item with
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
            || IsRerunnableAgentControlWithoutPreempt(item)
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

    public static WorkItem? BuildInfrastructureDeferredResumeState(WorkItem item, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && item.State is WorkItemState.Working or WorkItemState.Reworking)
        {
            return ClearInfrastructureDeferralFields(item, now) with
            {
                StartedAt = null,
            };
        }

        var target = item.State switch
        {
            WorkItemState.Queued => WorkItemState.Queued,
            WorkItemState.Working => WorkItemState.Queued,
            _ => MapToRecoveryState(item.State),
        };

        if (target is null)
            return null;

        return ClearInfrastructureDeferralFields(item.With(target.Value), now) with
        {
            StartedAt = null,
        };
    }

    public static bool HandlesRecoveryState(WorkItemState state)
        => state == WorkItemState.Working || MapToRecoveryState(state) is not null;

    /// <summary>
    /// Active in-flight states a per-item stale-updatedAt detector watches.
    /// An item parked in one of these whose <c>UpdatedAt</c> has not advanced
    /// for the configured window is considered wedged independent of the
    /// worker registry — the worker may be dead, may be heartbeating but
    /// stuck in a transport reconnect loop, or may have been orphaned by an
    /// orchestrator restart.
    /// </summary>
    public static bool IsItemStaleWatchedState(WorkItemState state) => state switch
    {
        WorkItemState.Working => true,
        WorkItemState.Reworking => true,
        WorkItemState.Auditing => true,
        WorkItemState.Merging => true,
        WorkItemState.ReworkingForConflict => true,
        WorkItemState.UpstreamPushing => true,
        _ => false,
    };

    /// <summary>
    /// Builds the next state for an item whose UpdatedAt has been frozen past
    /// the per-item stale threshold. Unlike <see cref="MapToRecoveryState"/>
    /// (which clears <c>WorkBranch</c> on Working → Queued so the next pickup
    /// regenerates a fresh branch), this path PRESERVES the work branch and
    /// flags <see cref="WorkItem.PreserveWorkBranchOnQueuedPickup"/> so the
    /// committed progress survives recovery and the next pickup re-rebases the
    /// branch onto current upstream main rather than discarding it.
    ///
    /// <para>
    /// When <paramref name="attempts"/> exceeds <paramref name="maxAttempts"/>
    /// (with <c>maxAttempts &gt; 0</c>; <c>0</c> means unlimited), the item is
    /// parked at <see cref="WorkItemState.NeedsOperatorInput"/> instead of
    /// being requeued — matches the spec's "bounded recovery attempts, then
    /// escalate" requirement and avoids burning a worker slot in a
    /// re-pickup → re-wedge loop.
    /// </para>
    ///
    /// <para>
    /// CheckAndAct and AgentControl items in <see cref="WorkItemState.Working"/>
    /// without a preempt checkpoint delegate to their existing rerun builders
    /// so the rerunnable-control-loop semantics stay consistent across detection
    /// paths. Items still in <see cref="WorkItemState.Working"/> /
    /// <see cref="WorkItemState.Reworking"/> with a preempt checkpoint keep
    /// the checkpoint and refresh <c>StartedAt</c> — the next pickup resumes
    /// from the checkpoint ref like any other preempted resume.
    /// </para>
    ///
    /// Returns null for non-watched states (caller should not invoke this on
    /// states outside <see cref="IsItemStaleWatchedState"/>).
    /// </summary>
    public static WorkItem? BuildStaleItemRecovery(
        WorkItem item,
        int attempts,
        int maxAttempts,
        string reason,
        DateTimeOffset now)
    {
        if (!IsItemStaleWatchedState(item.State))
            return null;

        if (IsRerunnableCheckAndActWithoutPreempt(item))
        {
            var rerun = BuildCheckAndActRerun(item, attempts);
            return rerun with { LastError = reason, UpdatedAt = now };
        }

        if (IsRerunnableAgentControlWithoutPreempt(item))
        {
            var rerun = BuildAgentControlRerun(item, attempts);
            return rerun with { LastError = reason, UpdatedAt = now };
        }

        if (maxAttempts > 0 && attempts > maxAttempts)
        {
            return item with
            {
                State = WorkItemState.NeedsOperatorInput,
                LastError =
                    $"{reason}; exceeded MaxRecoveryAttempts ({maxAttempts}) — operator triage required",
                RecoveryAttempts = attempts,
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = now,
            };
        }

        if (item.State is WorkItemState.Working or WorkItemState.Reworking
            && !string.IsNullOrWhiteSpace(item.PreemptCheckpoint))
        {
            return item with
            {
                LastError = reason,
                StartedAt = null,
                RecoveryAttempts = attempts,
                UpdatedAt = now,
            };
        }

        if (item.State == WorkItemState.Working)
        {
            // Working without a preempt checkpoint: requeue PRESERVING the
            // work branch so the bare repo's existing commits ride into the
            // next pickup and re-rebase onto current upstream main. Distinct
            // from Reworking, which has WorkComplete as a durable resume
            // point and is handled by MapToRecoveryState below.
            var preserve = !string.IsNullOrWhiteSpace(item.WorkBranch);
            return item with
            {
                State = WorkItemState.Queued,
                LastError = reason,
                StartedAt = null,
                WorkBranch = item.WorkBranch,
                PreserveWorkBranchOnQueuedPickup = preserve,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                RecoveryAttempts = attempts,
                UpdatedAt = now,
            };
        }

        var target = MapToRecoveryState(item.State) ?? item.State;
        return item with
        {
            State = target,
            LastError = reason,
            StartedAt = target == WorkItemState.Queued ? null : item.StartedAt,
            PreemptedAt = null,
            PreemptCheckpoint = null,
            RecoveryAttempts = attempts,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Maps a state for which a stale worker row could exist to the state the
    /// recovery layer should redispatch it into. Mid-flight states map back to
    /// durable resume points; phase-boundary resting states map to themselves.
    /// Returns null for terminal, parked, or otherwise dispatcher-owned states.
    /// </summary>
    public static WorkItemState? MapToRecoveryState(WorkItemState state) => state switch
    {
        WorkItemState.Reworking => WorkItemState.WorkComplete,
        WorkItemState.WorkComplete => WorkItemState.WorkComplete,
        WorkItemState.Auditing => WorkItemState.WorkComplete,
        WorkItemState.AuditPassed => WorkItemState.AuditPassed,
        WorkItemState.Merging => WorkItemState.AuditPassed,
        WorkItemState.Merged => WorkItemState.Merged,
        WorkItemState.ReworkingForConflict => WorkItemState.AuditPassed,
        WorkItemState.UpstreamPushing => WorkItemState.Merged,
        _ => null,
    };

    private static WorkItem ClearInfrastructureDeferralFields(WorkItem item, DateTimeOffset now) => item with
    {
        LastError = null,
        FailureKind = null,
        QuotaResetAt = null,
        NextQuotaRetryAt = null,
        QuotaRetryFrom = null,
        CancellationReason = null,
        CancellationSource = null,
        UpdatedAt = now,
    };
}

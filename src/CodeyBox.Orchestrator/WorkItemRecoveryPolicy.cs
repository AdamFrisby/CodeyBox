using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class WorkItemRecoveryPolicy
{
    public static int NextRecoveryAttempt(WorkItem item) => item.RecoveryAttempts + 1;

    public static bool ExceedsRecoveryAttempts(int attempts, int maxAttempts)
        => maxAttempts > 0 && attempts > maxAttempts;

    public static WorkItem WithRecoveryAttempt(
        WorkItem item,
        int recoveryAttempts,
        WorkItemState sourceState)
        => item with
        {
            RecoveryAttempts = recoveryAttempts,
            RecoveryAttemptSourceState = recoveryAttempts > 0 ? sourceState : null,
        };

    public static WorkItem ResetRecoveryAttemptsAfterRealProgress(WorkItem item, WorkItemState completedState)
        => ResetRecoveryAttemptsAfterRealProgress(item, item.State, completedState);

    public static WorkItem ResetRecoveryAttemptsAfterRealProgress(
        WorkItem item,
        WorkItemState fromState,
        WorkItemState toState)
    {
        if (item.RecoveryAttempts == 0 || !IsRealProgressTransition(fromState, toState, item.RecoveryAttemptSourceState))
            return item;

        return ClearRecoveryAttempts(item);
    }

    public static WorkItem ResetRecoveryAttemptsAfterRealProgressEvent(
        WorkItem item,
        RecoveryProgressEvent progressEvent)
    {
        if (item.RecoveryAttempts == 0
            || !IsRealProgressEventForRecoverySource(item.RecoveryAttemptSourceState, progressEvent))
        {
            return item;
        }

        return ClearRecoveryAttempts(item);
    }

    public static WorkItem ClearPlanFieldsIfQueued(WorkItem item)
    {
        if (item.State != WorkItemState.Queued)
            return item;

        return item with
        {
            PlanArtifact = null,
            PlanGeneratedAt = null,
            PlanReviewedAt = null,
            PlanReviewSummary = null,
            PlanReviewAttempts = 0,
        };
    }

    public static bool ShouldClearStartedAtForRecoveryTarget(WorkItemState target)
        => target is WorkItemState.Queued
            or WorkItemState.PlanReview
            or WorkItemState.PlanApproved;

    private static WorkItem ClearRecoveryAttempts(WorkItem item) => item with
    {
        RecoveryAttempts = 0,
        RecoveryAttemptSourceState = null,
    };

    private static bool IsRealProgressTransition(
        WorkItemState fromState,
        WorkItemState toState,
        WorkItemState? recoverySourceState)
    {
        _ = fromState;

        return recoverySourceState is null
            ? IsRealProgressCompletionState(toState)
            : toState switch
            {
                WorkItemState.PlanApproved => recoverySourceState is WorkItemState.Planning or WorkItemState.PlanReview,
                WorkItemState.WorkComplete => recoverySourceState is
                    WorkItemState.PlanApproved
                    or WorkItemState.Working
                    or WorkItemState.Reworking,
                WorkItemState.AuditPassed => recoverySourceState is
                    WorkItemState.WorkComplete
                    or WorkItemState.Auditing
                    or WorkItemState.Reworking
                    or WorkItemState.ReworkingForConflict,
                WorkItemState.Merged => recoverySourceState is WorkItemState.AuditPassed or WorkItemState.Merging,
                WorkItemState.Done => true,
                _ => false,
            };
    }

    private static bool IsRealProgressEventForRecoverySource(
        WorkItemState? recoverySourceState,
        RecoveryProgressEvent progressEvent)
    {
        if (recoverySourceState is null)
            return true;

        return progressEvent switch
        {
            RecoveryProgressEvent.AuditVerdictProduced => recoverySourceState is
                WorkItemState.WorkComplete
                or WorkItemState.Auditing,
            RecoveryProgressEvent.AuditReworkCompleted => recoverySourceState == WorkItemState.Reworking,
            RecoveryProgressEvent.PostActReworkCompleted => recoverySourceState == WorkItemState.Reworking,
            RecoveryProgressEvent.ConflictReworkBranchAdvanced => recoverySourceState is
                WorkItemState.AuditPassed
                or WorkItemState.Merging
                or WorkItemState.ReworkingForConflict,
            _ => false,
        };
    }

    private static bool IsRealProgressCompletionState(WorkItemState state) => state switch
    {
        WorkItemState.WorkComplete => true,
        WorkItemState.PlanApproved => true,
        WorkItemState.AuditPassed => true,
        WorkItemState.Merged => true,
        WorkItemState.Done => true,
        _ => false,
    };

    public static WorkItem BuildPreemptCheckpointRecovery(
        WorkItem item,
        int recoveryAttempts,
        int maxRecoveryAttempts,
        DateTimeOffset now,
        string exceededMessage)
    {
        if (ExceedsRecoveryAttempts(recoveryAttempts, maxRecoveryAttempts))
        {
            return WithRecoveryAttempt(item with
            {
                State = WorkItemState.AbandonedAfterRecoveryAttempts,
                LastError = exceededMessage,
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = now,
            }, recoveryAttempts, item.State);
        }

        return WithRecoveryAttempt(item with
        {
            StartedAt = null,
            UpdatedAt = now,
        }, recoveryAttempts, item.State);
    }

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

    public static WorkItem BuildCheckAndActRerun(WorkItem item, int recoveryAttempts) =>
        WithRecoveryAttempt(item with
        {
            State = WorkItemState.Queued,
            LastError = null,
            StartedAt = null,
            PreemptedAt = null,
            PreemptCheckpoint = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, recoveryAttempts, item.State);

    public static WorkItem BuildAgentControlRerun(WorkItem item, int recoveryAttempts) =>
        WithRecoveryAttempt(item with
        {
            State = WorkItemState.Queued,
            LastError = null,
            StartedAt = null,
            PreemptedAt = null,
            PreemptCheckpoint = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, recoveryAttempts, item.State);

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

        failed = WithRecoveryAttempt(item with
        {
            State = WorkItemState.Failed,
            LastError = lastError,
            StartedAt = null,
            PreemptedAt = null,
            PreemptCheckpoint = null,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, item.RecoveryAttempts + 1, item.State);
        return true;
    }

    public static WorkItem? BuildGracefulShutdownRecoveryState(
        WorkItem item,
        DateTimeOffset now,
        int maxRecoveryAttempts,
        string recoveryReason = "graceful shutdown drain timed out")
    {
        if (!string.IsNullOrWhiteSpace(item.SuspendedVmName))
            return null;

        if (!string.IsNullOrWhiteSpace(item.PreemptCheckpoint)
            && item.State is WorkItemState.Working or WorkItemState.Reworking)
        {
            return BuildPreemptCheckpointRecovery(
                item,
                NextRecoveryAttempt(item),
                maxRecoveryAttempts,
                now,
                "exceeded MaxRecoveryAttempts");
        }

        var target = item.State == WorkItemState.Working
            ? WorkItemState.Queued
            : MapToRecoveryState(item.State);

        if (target is null)
            return null;

        var attempts = NextRecoveryAttempt(item);
        if (ExceedsRecoveryAttempts(attempts, maxRecoveryAttempts))
        {
            return WithRecoveryAttempt(item with
            {
                State = WorkItemState.AbandonedAfterRecoveryAttempts,
                LastError = $"exceeded MaxRecoveryAttempts ({maxRecoveryAttempts}) during {recoveryReason}",
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = now,
            }, attempts, item.State);
        }

        var error = target == WorkItemState.Queued
            ? $"{recoveryReason} while item was {item.State}; re-queued for a fresh run"
            : null;

        var recovered = ClearPlanFieldsIfQueued(item.With(target.Value, error) with
        {
            StartedAt = ShouldClearStartedAtForRecoveryTarget(target.Value) ? null : item.StartedAt,
            UpdatedAt = now,
        });
        return WithRecoveryAttempt(recovered, attempts, item.State);
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

        if (item.State == WorkItemState.Working
            && target == WorkItemState.Queued
            && !string.IsNullOrWhiteSpace(item.WorkBranch))
        {
            return ClearPlanFieldsIfQueued(ClearInfrastructureDeferralFields(item with
            {
                State = WorkItemState.Queued,
                StartedAt = null,
                PreserveWorkBranchOnQueuedPickup = true,
                PreemptedAt = null,
                PreemptCheckpoint = null,
            }, now));
        }

        return ClearPlanFieldsIfQueued(ClearInfrastructureDeferralFields(item.With(target.Value), now) with
        {
            StartedAt = null,
        });
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
        WorkItemState.Planning => true,
        WorkItemState.PlanReview => true,
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

        // Max-attempts cap is checked BEFORE the rerunnable CheckAndAct /
        // AgentControl branches: an item that hits the cap on either of those
        // job types must escalate to NeedsOperatorInput too, otherwise a
        // chronically-wedging CheckAndAct/AgentControl item would be requeued
        // forever by Build{CheckAndAct,AgentControl}Rerun and never hit the
        // bounded-then-escalate contract the rest of the recovery surface
        // honours.
        if (ExceedsRecoveryAttempts(attempts, maxAttempts))
        {
            return WithRecoveryAttempt(item with
            {
                State = WorkItemState.NeedsOperatorInput,
                LastError =
                    $"{reason}; exceeded MaxRecoveryAttempts ({maxAttempts}) — operator triage required",
                StartedAt = null,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = now,
            }, attempts, item.State);
        }

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

        if (item.State is WorkItemState.Working or WorkItemState.Reworking
            && !string.IsNullOrWhiteSpace(item.PreemptCheckpoint))
        {
            return WithRecoveryAttempt(item with
            {
                LastError = reason,
                StartedAt = null,
                UpdatedAt = now,
            }, attempts, item.State);
        }

        if (item.State == WorkItemState.Working)
        {
            // Working without a preempt checkpoint: requeue PRESERVING the
            // work branch so the bare repo's existing commits ride into the
            // next pickup and re-rebase onto current upstream main. Distinct
            // from Reworking, which has WorkComplete as a durable resume
            // point and is handled by MapToRecoveryState below.
            var preserve = !string.IsNullOrWhiteSpace(item.WorkBranch);
            var recoveredWorking = ClearPlanFieldsIfQueued(item with
            {
                State = WorkItemState.Queued,
                LastError = reason,
                StartedAt = null,
                WorkBranch = item.WorkBranch,
                PreserveWorkBranchOnQueuedPickup = preserve,
                PreemptedAt = null,
                PreemptCheckpoint = null,
                UpdatedAt = now,
            });
            return WithRecoveryAttempt(recoveredWorking, attempts, item.State);
        }

        var target = MapToRecoveryState(item.State) ?? item.State;
        var recovered = ClearPlanFieldsIfQueued(item with
        {
            State = target,
            LastError = reason,
            StartedAt = ShouldClearStartedAtForRecoveryTarget(target) ? null : item.StartedAt,
            PreemptedAt = null,
            PreemptCheckpoint = null,
            UpdatedAt = now,
        });
        return WithRecoveryAttempt(recovered, attempts, item.State);
    }

    /// <summary>
    /// Maps a state for which a stale worker row could exist to the state the
    /// recovery layer should redispatch it into. Mid-flight states map back to
    /// durable resume points; phase-boundary resting states map to themselves.
    /// Returns null for terminal, parked, or otherwise dispatcher-owned states.
    /// </summary>
    public static WorkItemState? MapToRecoveryState(WorkItemState state) => state switch
    {
        WorkItemState.Planning => WorkItemState.Queued,
        WorkItemState.PlanReview => WorkItemState.PlanReview,
        WorkItemState.PlanApproved => WorkItemState.PlanApproved,
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
        QuotaRetryPhase = null,
        NextTransientRetryAt = null,
        TransientRetryAttempts = 0,
        TransientRetryFirstFailedAt = null,
        TransientRetryFrom = null,
        CancellationReason = null,
        CancellationSource = null,
        UpdatedAt = now,
    };
}

internal enum RecoveryProgressEvent
{
    AuditVerdictProduced,
    AuditReworkCompleted,
    PostActReworkCompleted,
    ConflictReworkBranchAdvanced,
}

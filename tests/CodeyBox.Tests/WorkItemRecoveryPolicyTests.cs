using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkItemRecoveryPolicyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkingItem_WithEmptyOrWhitespaceCheckpoint_StillRequiresPipelinePreemptBeforeLifecycleTeardown(
        string preemptCheckpoint)
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            PreemptCheckpoint = preemptCheckpoint,
        };

        Assert.True(WorkItemRecoveryPolicy.RequiresPipelinePreemptCheckpointBeforeLifecycleTeardown(item));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CheckAndActWorkingItem_WithEmptyOrWhitespaceCheckpoint_IsRerunnableWithoutPreempt(
        string preemptCheckpoint)
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            JobType = JobType.CheckAndAct,
            PreemptCheckpoint = preemptCheckpoint,
        };

        Assert.True(WorkItemRecoveryPolicy.IsRerunnableCheckAndActWithoutPreempt(item));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AgentControlWorkingItem_WithEmptyOrWhitespaceCheckpoint_IsRerunnableWithoutPreempt(
        string preemptCheckpoint)
    {
        var item = MakeAgentControlItem(WorkItemState.Working) with
        {
            PreemptCheckpoint = preemptCheckpoint,
        };

        Assert.True(WorkItemRecoveryPolicy.IsRerunnableAgentControlWithoutPreempt(item));
    }

    [Fact]
    public void BuildAgentControlRerun_RequeuesAndPreservesControlSpec()
    {
        var item = MakeAgentControlItem(WorkItemState.Working) with
        {
            LastError = "worker disappeared",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            RecoveryAttempts = 2,
        };

        var recovered = WorkItemRecoveryPolicy.BuildAgentControlRerun(item, recoveryAttempts: 3);

        Assert.Equal(WorkItemState.Queued, recovered.State);
        Assert.Equal(3, recovered.RecoveryAttempts);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.StartedAt);
        Assert.Same(item.AgentControl, recovered.AgentControl);
    }

    [Theory]
    [InlineData(WorkItemState.PlanApproved)]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merged)]
    [InlineData(WorkItemState.Done)]
    public void ResetRecoveryAttemptsAfterRealProgress_ClearsCompletionStates(WorkItemState state)
    {
        var item = MakeItem(state) with { RecoveryAttempts = 2 };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(item, state);

        Assert.Equal(0, reset.RecoveryAttempts);
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Planning)]
    [InlineData(WorkItemState.PlanReview)]
    [InlineData(WorkItemState.Auditing)]
    [InlineData(WorkItemState.Reworking)]
    [InlineData(WorkItemState.Merging)]
    [InlineData(WorkItemState.UpstreamPushing)]
    public void ResetRecoveryAttemptsAfterRealProgress_PreservesInFlightStates(WorkItemState state)
    {
        var item = MakeItem(state) with { RecoveryAttempts = 2 };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(item, state);

        Assert.Equal(2, reset.RecoveryAttempts);
    }

    [Fact]
    public void ResetRecoveryAttemptsAfterRealProgress_PreservesDirectAuditingToReworkingTransition()
    {
        var item = MakeItem(WorkItemState.Auditing) with { RecoveryAttempts = 2 };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(
            item.With(WorkItemState.Reworking),
            fromState: WorkItemState.Auditing,
            toState: WorkItemState.Reworking);

        Assert.Equal(2, reset.RecoveryAttempts);
    }

    [Fact]
    public void ResetRecoveryAttemptsAfterRealProgressEvent_ClearsAfterCompletedRework()
    {
        var item = MakeItem(WorkItemState.Reworking) with
        {
            RecoveryAttempts = 2,
            RecoveryAttemptSourceState = WorkItemState.Reworking,
        };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgressEvent(
            item,
            RecoveryProgressEvent.AuditReworkCompleted);

        Assert.Equal(0, reset.RecoveryAttempts);
        Assert.Null(reset.RecoveryAttemptSourceState);
    }

    [Fact]
    public void ResetRecoveryAttemptsAfterRealProgressEvent_PreservesReworkRecoveryOnAuditVerdict()
    {
        var item = MakeItem(WorkItemState.Auditing) with
        {
            RecoveryAttempts = 2,
            RecoveryAttemptSourceState = WorkItemState.Reworking,
        };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgressEvent(
            item,
            RecoveryProgressEvent.AuditVerdictProduced);

        Assert.Equal(2, reset.RecoveryAttempts);
        Assert.Equal(WorkItemState.Reworking, reset.RecoveryAttemptSourceState);
    }

    [Theory]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Auditing)]
    public void ResetRecoveryAttemptsAfterRealProgressEvent_ClearsAuditRecoveryOnAuditVerdict(
        WorkItemState sourceState)
    {
        var item = MakeItem(WorkItemState.Auditing) with
        {
            RecoveryAttempts = 2,
            RecoveryAttemptSourceState = sourceState,
        };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgressEvent(
            item,
            RecoveryProgressEvent.AuditVerdictProduced);

        Assert.Equal(0, reset.RecoveryAttempts);
        Assert.Null(reset.RecoveryAttemptSourceState);
    }

    [Fact]
    public void ResetRecoveryAttemptsAfterRealProgress_PreservesAuditStartTransition()
    {
        var item = MakeItem(WorkItemState.WorkComplete) with { RecoveryAttempts = 2 };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(
            item.With(WorkItemState.Auditing),
            fromState: WorkItemState.WorkComplete,
            toState: WorkItemState.Auditing);

        Assert.Equal(2, reset.RecoveryAttempts);
    }

    [Theory]
    [InlineData(WorkItemState.Planning)]
    [InlineData(WorkItemState.PlanReview)]
    public void ResetRecoveryAttemptsAfterRealProgress_ClearsPlanningRecoveryOnPlanApproval(
        WorkItemState sourceState)
    {
        var item = MakeItem(WorkItemState.PlanReview) with
        {
            RecoveryAttempts = 2,
            RecoveryAttemptSourceState = sourceState,
        };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(
            item.With(WorkItemState.PlanApproved),
            fromState: sourceState,
            toState: WorkItemState.PlanApproved);

        Assert.Equal(0, reset.RecoveryAttempts);
        Assert.Null(reset.RecoveryAttemptSourceState);
    }

    [Fact]
    public void ResetRecoveryAttemptsAfterRealProgress_ClearsPlanApprovedRecoveryOnWorkCompletion()
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            RecoveryAttempts = 2,
            RecoveryAttemptSourceState = WorkItemState.PlanApproved,
        };

        var reset = WorkItemRecoveryPolicy.ResetRecoveryAttemptsAfterRealProgress(
            item.With(WorkItemState.WorkComplete),
            fromState: WorkItemState.PlanApproved,
            toState: WorkItemState.WorkComplete);

        Assert.Equal(0, reset.RecoveryAttempts);
        Assert.Null(reset.RecoveryAttemptSourceState);
    }

    [Fact]
    public void OrchestratorRecovery_AgentControlWorkingWithoutCheckpoint_Requeues()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-agent-control-recovery-{Guid.NewGuid():N}.db");
        using var store = new SqliteWorkItemStore(dbPath);
        try
        {
            var svc = new OrchestratorService(
                new InMemoryTaskQueue(),
                store,
                new FakePipelineRunner(store),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions { MaxRecoveryAttempts = 3 },
                NullLogger<OrchestratorService>.Instance);
            var item = MakeAgentControlItem(WorkItemState.Working) with
            {
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                RecoveryAttempts = 1,
            };

            var recovered = svc.TryBuildRecoveredStateForTest(item);

            Assert.NotNull(recovered);
            Assert.Equal(WorkItemState.Queued, recovered!.State);
            Assert.Equal(2, recovered.RecoveryAttempts);
            Assert.Null(recovered.StartedAt);
            Assert.Same(item.AgentControl, recovered.AgentControl);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void OrchestratorRecovery_AgentControlWorkingWithoutCheckpoint_AtCapAbandons()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-agent-control-recovery-cap-{Guid.NewGuid():N}.db");
        using var store = new SqliteWorkItemStore(dbPath);
        try
        {
            var svc = new OrchestratorService(
                new InMemoryTaskQueue(),
                store,
                new FakePipelineRunner(store),
                new CancellationRegistry(CancellationToken.None),
                new OrchestratorOptions { MaxRecoveryAttempts = 3 },
                NullLogger<OrchestratorService>.Instance);
            var item = MakeAgentControlItem(WorkItemState.Working) with
            {
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                PreemptedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
                PreemptCheckpoint = null,
                RecoveryAttempts = 3,
            };

            var recovered = svc.TryBuildRecoveredStateForTest(item);

            Assert.NotNull(recovered);
            Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, recovered!.State);
            Assert.Equal(4, recovered.RecoveryAttempts);
            Assert.Null(recovered.StartedAt);
            Assert.Null(recovered.PreemptedAt);
            Assert.Null(recovered.PreemptCheckpoint);
            Assert.Same(item.AgentControl, recovered.AgentControl);
            Assert.Contains("3 recovery attempts", recovered.LastError);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Theory]
    [InlineData(WorkItemState.Working, WorkItemState.Queued, true)]
    [InlineData(WorkItemState.Planning, WorkItemState.Queued, true)]
    [InlineData(WorkItemState.PlanReview, WorkItemState.PlanReview, false)]
    [InlineData(WorkItemState.PlanApproved, WorkItemState.PlanApproved, false)]
    [InlineData(WorkItemState.Reworking, WorkItemState.WorkComplete, false)]
    [InlineData(WorkItemState.Auditing, WorkItemState.WorkComplete, false)]
    [InlineData(WorkItemState.ReworkingForConflict, WorkItemState.AuditPassed, false)]
    [InlineData(WorkItemState.Merging, WorkItemState.AuditPassed, false)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Merged, false)]
    [InlineData(WorkItemState.WorkComplete, WorkItemState.WorkComplete, false)]
    [InlineData(WorkItemState.AuditPassed, WorkItemState.AuditPassed, false)]
    [InlineData(WorkItemState.Merged, WorkItemState.Merged, false)]
    public void GracefulShutdownRecovery_MapsRecoverableStates(
        WorkItemState from,
        WorkItemState to,
        bool clearsStartedAt)
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            MakeItem(from) with { StartedAt = startedAt },
            DateTimeOffset.UtcNow,
            maxRecoveryAttempts: 3);

        Assert.NotNull(recovered);
        Assert.Equal(to, recovered!.State);
        Assert.Equal(clearsStartedAt ? null : startedAt, recovered.StartedAt);
        Assert.Equal(1, recovered.RecoveryAttempts);
    }

    [Fact]
    public void GracefulShutdownRecovery_PlanningToQueuedClearsPlanFields()
    {
        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            MakeItem(WorkItemState.Planning) with
            {
                PlanArtifact = ValidPlan,
                PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                PlanReviewSummary = "approved",
            },
            DateTimeOffset.UtcNow,
            maxRecoveryAttempts: 3);

        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.Queued, recovered!.State);
        Assert.Null(recovered.PlanArtifact);
        Assert.Null(recovered.PlanGeneratedAt);
        Assert.Null(recovered.PlanReviewedAt);
        Assert.Null(recovered.PlanReviewSummary);
    }

    [Fact]
    public void GracefulShutdownRecovery_WorkingWithPreemptCheckpoint_PreservesResumeState()
    {
        var item = MakeItem(WorkItemState.Working) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PreemptCheckpoint = "refs/heads/codeybox/preempt/test",
        };

        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            item,
            DateTimeOffset.UtcNow,
            maxRecoveryAttempts: 3);

        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.Working, recovered!.State);
        Assert.Null(recovered.StartedAt);
        Assert.Equal(1, recovered.RecoveryAttempts);
        Assert.Equal(item.PreemptCheckpoint, recovered.PreemptCheckpoint);
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Reworking)]
    public void GracefulShutdownRecovery_WithPreemptCheckpoint_AtCapAbandons(WorkItemState state)
    {
        var item = MakeItem(state) with
        {
            PreemptCheckpoint = "refs/heads/codeybox/preempt/test",
            RecoveryAttempts = 3,
        };

        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            item,
            DateTimeOffset.UtcNow,
            maxRecoveryAttempts: 3);

        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, recovered!.State);
        Assert.Equal(4, recovered.RecoveryAttempts);
        Assert.Null(recovered.PreemptCheckpoint);
    }

    [Fact]
    public void GracefulShutdownRecovery_NormalRecoverableState_AtCapAbandons()
    {
        var item = MakeItem(WorkItemState.Auditing) with { RecoveryAttempts = 3 };

        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            item,
            DateTimeOffset.UtcNow,
            maxRecoveryAttempts: 3);

        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.AbandonedAfterRecoveryAttempts, recovered!.State);
        Assert.Equal(4, recovered.RecoveryAttempts);
        Assert.Contains("MaxRecoveryAttempts", recovered.LastError);
    }

    [Theory]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.Reworking)]
    public void InfrastructureDeferral_WithPreemptCheckpoint_PreservesCheckpointResumeState(
        WorkItemState state)
    {
        var item = MakeItem(state) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            PreemptedAt = DateTimeOffset.UtcNow.AddMinutes(-4),
            PreemptCheckpoint = "refs/heads/codeybox/preempt/test",
            LastError = "prior error",
            FailureKind = "other",
        };

        var recovered = WorkItemRecoveryPolicy.BuildInfrastructureDeferredResumeState(
            item,
            DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(state, recovered!.State);
        Assert.Null(recovered.StartedAt);
        Assert.Equal(item.PreemptedAt, recovered.PreemptedAt);
        Assert.Equal(item.PreemptCheckpoint, recovered.PreemptCheckpoint);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.FailureKind);
    }

    [Fact]
    public void InfrastructureDeferral_NormalReworking_ResumesFromWorkComplete()
    {
        var item = MakeItem(WorkItemState.Reworking) with
        {
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            WorkBranch = "codeybox/work",
            LastError = "prior error",
            FailureKind = "other",
            NextTransientRetryAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TransientRetryAttempts = 2,
            TransientRetryFirstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            TransientRetryFrom = "merge",
        };

        var recovered = WorkItemRecoveryPolicy.BuildInfrastructureDeferredResumeState(
            item,
            DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(WorkItemState.WorkComplete, recovered!.State);
        Assert.Equal(item.WorkBranch, recovered.WorkBranch);
        Assert.Null(recovered.StartedAt);
        Assert.Null(recovered.PreemptCheckpoint);
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.FailureKind);
        Assert.Null(recovered.NextTransientRetryAt);
        Assert.Equal(0, recovered.TransientRetryAttempts);
        Assert.Null(recovered.TransientRetryFirstFailedAt);
        Assert.Null(recovered.TransientRetryFrom);
    }

    [Theory]
    [InlineData(WorkItemState.Planning, WorkItemState.Queued, true)]
    [InlineData(WorkItemState.PlanReview, WorkItemState.PlanReview, false)]
    [InlineData(WorkItemState.PlanApproved, WorkItemState.PlanApproved, false)]
    public void InfrastructureDeferral_PlanningStatesResumeWithValidPlanShape(
        WorkItemState from,
        WorkItemState to,
        bool clearsPlan)
    {
        var item = MakeItem(from) with
        {
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = from == WorkItemState.PlanApproved ? DateTimeOffset.UtcNow.AddMinutes(-1) : null,
            PlanReviewSummary = from == WorkItemState.PlanApproved ? "approved" : null,
            LastError = "deferred",
            FailureKind = "infrastructure",
        };

        var recovered = WorkItemRecoveryPolicy.BuildInfrastructureDeferredResumeState(
            item,
            DateTimeOffset.UtcNow);

        Assert.NotNull(recovered);
        Assert.Equal(to, recovered!.State);
        if (clearsPlan)
        {
            Assert.Null(recovered.PlanArtifact);
            Assert.Null(recovered.PlanGeneratedAt);
            Assert.Null(recovered.PlanReviewedAt);
            Assert.Null(recovered.PlanReviewSummary);
        }
        else
        {
            Assert.Equal(item.PlanArtifact, recovered.PlanArtifact);
            Assert.Equal(item.PlanGeneratedAt, recovered.PlanGeneratedAt);
            Assert.Equal(item.PlanReviewedAt, recovered.PlanReviewedAt);
            Assert.Equal(item.PlanReviewSummary, recovered.PlanReviewSummary);
        }
        Assert.Null(recovered.LastError);
        Assert.Null(recovered.FailureKind);
    }

    [Fact]
    public void GracefulShutdownRecovery_SuspendedItem_IsLeftAlone()
    {
        var item = MakeItem(WorkItemState.Working) with { SuspendedVmName = "vm-1" };

        var recovered = WorkItemRecoveryPolicy.BuildGracefulShutdownRecoveryState(
            item,
            DateTimeOffset.UtcNow,
            maxRecoveryAttempts: 3);

        Assert.Null(recovered);
    }

    private static WorkItem MakeItem(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
    };

    private static WorkItem MakeAgentControlItem(WorkItemState state) => MakeItem(state) with
    {
        JobType = JobType.AgentControl,
        AgentControl = new AgentControlSpec
        {
            Action = AgentControlAction.Pause,
            Agent = AgentKind.Claude.Value,
            Reason = "reserve quota",
        },
    };

    private const string ValidPlan = """
        {
          "approach": "recover planning",
          "files": ["output.txt"],
          "testStrategy": ["run tests"],
          "risks": ["none"],
          "satisfiesTask": "continues safely"
        }
        """;
}

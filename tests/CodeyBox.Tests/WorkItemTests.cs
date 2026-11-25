using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class WorkItemTests
{
    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        Agent = AgentKind.Claude,
    };

    [Fact]
    public void With_UpdatesStateAndTimestamp()
    {
        var item = Sample();
        var before = item.UpdatedAt;
        Thread.Sleep(2);
        var next = item.With(WorkItemState.Working);
        Assert.Equal(WorkItemState.Working, next.State);
        Assert.True(next.UpdatedAt >= before);
        Assert.Equal(item.Id, next.Id);
    }

    [Fact]
    public void With_RecordsLastError()
    {
        var item = Sample();
        var failed = item.With(WorkItemState.Failed, "boom");
        Assert.Equal("boom", failed.LastError);
    }

    [Fact]
    public void With_QueuedToQueuedPreservesResumeBranchAndFlag()
    {
        var item = Sample() with
        {
            State = WorkItemState.Queued,
            WorkBranch = "feature/operator-resume",
            PreserveWorkBranchOnQueuedPickup = true,
            StartedAt = DateTimeOffset.UtcNow,
        };

        var requeued = item.With(WorkItemState.Queued);

        Assert.Equal(WorkItemState.Queued, requeued.State);
        Assert.Equal("feature/operator-resume", requeued.WorkBranch);
        Assert.True(requeued.PreserveWorkBranchOnQueuedPickup);
        Assert.Null(requeued.StartedAt);
    }

    [Fact]
    public void With_RequeueClearsPreserveFlagWhenWorkBranchIsCleared()
    {
        var item = Sample() with
        {
            State = WorkItemState.Working,
            WorkBranch = "feature/operator-resume",
            PreserveWorkBranchOnQueuedPickup = true,
            StartedAt = DateTimeOffset.UtcNow,
        };

        var requeued = item.With(WorkItemState.Queued);

        Assert.Null(requeued.WorkBranch);
        Assert.False(requeued.PreserveWorkBranchOnQueuedPickup);
    }

    [Fact]
    public void With_WaitingForQuotaResetPreservesRetryPhase()
    {
        var resetAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var item = Sample() with
        {
            State = WorkItemState.Auditing,
            FailureKind = "quota",
            QuotaResetAt = resetAt,
            NextQuotaRetryAt = resetAt,
            QuotaRetryFrom = "audit",
            QuotaRetryPhase = "rework",
        };

        var waiting = item.With(
            WorkItemState.WaitingForQuotaReset,
            "quota",
            failureKind: "quota",
            quotaResetAt: resetAt);

        Assert.Equal("quota", waiting.FailureKind);
        Assert.Equal(resetAt, waiting.QuotaResetAt);
        Assert.Equal(resetAt, waiting.NextQuotaRetryAt);
        Assert.Equal("audit", waiting.QuotaRetryFrom);
        Assert.Equal("rework", waiting.QuotaRetryPhase);
    }

    [Fact]
    public void With_NonQuotaTransitionClearsRetryPhase()
    {
        var item = Sample() with
        {
            State = WorkItemState.WaitingForQuotaReset,
            FailureKind = "quota",
            QuotaResetAt = DateTimeOffset.UtcNow.AddMinutes(10),
            NextQuotaRetryAt = DateTimeOffset.UtcNow.AddMinutes(10),
            QuotaRetryFrom = "audit",
            QuotaRetryPhase = "rework",
        };

        var queued = item.With(WorkItemState.Queued);

        Assert.Null(queued.FailureKind);
        Assert.Null(queued.QuotaResetAt);
        Assert.Null(queued.NextQuotaRetryAt);
        Assert.Null(queued.QuotaRetryFrom);
        Assert.Null(queued.QuotaRetryPhase);
    }

    [Fact]
    public void With_MergeConflictResolutionFailedCarriesFailureKind()
    {
        var item = Sample();

        var failed = item.With(
            WorkItemState.MergeConflictResolutionFailed,
            "merge resolver failed",
            failureKind: WorkItemFailureKinds.Infrastructure);

        Assert.Equal(WorkItemFailureKinds.Infrastructure, failed.FailureKind);
    }

    [Fact]
    public void With_AuthRequiredFailedCarriesAuthFailureScope()
    {
        var item = Sample();

        var failed = item.With(
            WorkItemState.Failed,
            "auth required",
            failureKind: WorkItemFailureKinds.AuthRequired,
            authFailureScope: WorkItemAuthFailureScope.Item);

        Assert.Equal(WorkItemAuthFailureScope.Item, failed.AuthFailureScope);
    }

    [Theory]
    [InlineData(WorkItemState.Working, WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Auditing, WorkItemState.AuditPassed)]
    [InlineData(WorkItemState.Merging, WorkItemState.Merged)]
    [InlineData(WorkItemState.UpstreamPushing, WorkItemState.Done)]
    public void With_SuccessfulPhaseBoundaryClearsTransientRetrySeries(
        WorkItemState from,
        WorkItemState to)
    {
        var firstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var item = Sample() with
        {
            State = from,
            TransientRetryAttempts = 3,
            TransientRetryFirstFailedAt = firstFailedAt,
            NextTransientRetryAt = DateTimeOffset.UtcNow.AddMinutes(5),
            TransientRetryFrom = "audit",
            TransientRetryParkedAt = DateTimeOffset.UtcNow,
            TransientRetryParkedDuration = TimeSpan.FromMinutes(2),
        };

        var transitioned = item.With(to);

        Assert.Equal(0, transitioned.TransientRetryAttempts);
        Assert.Null(transitioned.TransientRetryFirstFailedAt);
        Assert.Null(transitioned.NextTransientRetryAt);
        Assert.Null(transitioned.TransientRetryFrom);
        Assert.Null(transitioned.TransientRetryParkedAt);
        Assert.Equal(TimeSpan.Zero, transitioned.TransientRetryParkedDuration);
    }

    [Theory]
    [InlineData(WorkItemState.Queued, WorkItemState.Working)]
    [InlineData(WorkItemState.Queued, WorkItemState.Planning)]
    [InlineData(WorkItemState.Planning, WorkItemState.PlanReview)]
    [InlineData(WorkItemState.PlanReview, WorkItemState.PlanApproved)]
    [InlineData(WorkItemState.PlanApproved, WorkItemState.Working)]
    [InlineData(WorkItemState.WorkComplete, WorkItemState.Auditing)]
    [InlineData(WorkItemState.AuditPassed, WorkItemState.Merging)]
    [InlineData(WorkItemState.Merged, WorkItemState.UpstreamPushing)]
    public void With_RetryPhaseStartPreservesTransientRetrySeries(
        WorkItemState from,
        WorkItemState to)
    {
        var firstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var item = Sample() with
        {
            State = from,
            TransientRetryAttempts = 2,
            TransientRetryFirstFailedAt = firstFailedAt,
        };

        var transitioned = item.With(to);

        Assert.Equal(2, transitioned.TransientRetryAttempts);
        Assert.Equal(firstFailedAt, transitioned.TransientRetryFirstFailedAt);
    }

    [Fact]
    public void With_TransientExhaustedFailedPreservesHistoryButClosesOpenPark()
    {
        var firstFailedAt = DateTimeOffset.UtcNow.AddMinutes(-30);
        var parkedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var item = Sample() with
        {
            State = WorkItemState.WaitingForTransientRetry,
            FailureKind = "transient",
            NextTransientRetryAt = DateTimeOffset.UtcNow.AddMinutes(3),
            TransientRetryFrom = "audit",
            TransientRetryAttempts = 2,
            TransientRetryFirstFailedAt = firstFailedAt,
            TransientRetryParkedAt = parkedAt,
            TransientRetryParkedDuration = TimeSpan.FromMinutes(4),
        };

        var exhausted = item.With(
            WorkItemState.Failed,
            "transient network auto-retry exhausted",
            failureKind: "transient-exhausted");

        Assert.Equal(2, exhausted.TransientRetryAttempts);
        Assert.Equal(firstFailedAt, exhausted.TransientRetryFirstFailedAt);
        Assert.Equal(TimeSpan.FromMinutes(4), exhausted.TransientRetryParkedDuration);
        Assert.Null(exhausted.NextTransientRetryAt);
        Assert.Null(exhausted.TransientRetryFrom);
        Assert.Null(exhausted.TransientRetryParkedAt);
    }

    [Fact]
    public void TransientRetryParkedDuration_RejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = Sample() with
        {
            TransientRetryParkedDuration = TimeSpan.FromSeconds(-1),
        });
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class WorkItemRetrierPlanningTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("codeybox-planning-retrier-").FullName;

    public void Dispose()
    {
        CodeyBox.Tests.TestTempArtifacts.DeleteDirectory(_workspace);
    }

    [Fact]
    public async Task RetryQuotaAuto_FromPlanning_RequeuesAndClearsPlan()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.WaitingForQuotaReset) with
        {
            FailureKind = "quota",
            QuotaRetryFrom = "planning",
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            PlanReviewSummary = "approved",
        };
        await store.CreateAsync(item);

        var result = await retrier.RetryQuotaAutoAsync(item, "planning", "quota-reset");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("planning", result.ActualFrom);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Queued, read!.State);
        Assert.Null(read.PlanArtifact);
        Assert.Null(read.PlanReviewedAt);
        Assert.Equal(1, read.QuotaRetryAttempts);
    }

    [Fact]
    public async Task ResumeCancelled_FromPlanning_DoesNotRequireWorkBranchAndClearsPlan()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.Cancelled) with
        {
            WorkBranch = null,
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            PlanReviewSummary = "approved",
        };
        await store.CreateAsync(item);

        var result = await retrier.ResumeAsync(item, "planning", "operator resume");

        Assert.Equal(WorkItemRetrier.ResumeStatus.Ok, result.Status);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Queued, read!.State);
        Assert.Null(read.PlanArtifact);
        Assert.Null(read.PlanReviewedAt);
        Assert.False(read.PreserveWorkBranchOnQueuedPickup);
    }

    [Fact]
    public async Task ResumeCancelled_FromPlanReview_DoesNotRequireWorkBranchAndPreservesPlan()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.Cancelled) with
        {
            WorkBranch = null,
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        };
        await store.CreateAsync(item);

        var result = await retrier.ResumeAsync(item, "plan_review", "operator resume");

        Assert.Equal(WorkItemRetrier.ResumeStatus.Ok, result.Status);
        Assert.Equal(WorkItemState.PlanReview, result.ResumeState);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.PlanReview, read!.State);
        Assert.Equal(ValidPlan, read.PlanArtifact);
        Assert.Equal(item.PlanGeneratedAt, read.PlanGeneratedAt);
        Assert.False(read.PreserveWorkBranchOnQueuedPickup);
    }

    [Fact]
    public async Task ResumeCancelled_FromPlanApproved_DoesNotRequireWorkBranchAndPreservesApproval()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var reviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = Sample(WorkItemState.Cancelled) with
        {
            WorkBranch = null,
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = reviewedAt,
            PlanReviewSummary = "approved",
        };
        await store.CreateAsync(item);

        var result = await retrier.ResumeAsync(item, "plan_approved", "operator resume");

        Assert.Equal(WorkItemRetrier.ResumeStatus.Ok, result.Status);
        Assert.Equal(WorkItemState.PlanApproved, result.ResumeState);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.PlanApproved, read!.State);
        Assert.Equal(ValidPlan, read.PlanArtifact);
        Assert.Equal(reviewedAt, read.PlanReviewedAt);
        Assert.Equal("approved", read.PlanReviewSummary);
    }

    [Fact]
    public async Task RetryManual_FromPlanReview_DoesNotRequireWorkBranchAndPreservesPlan()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.Failed) with
        {
            WorkBranch = null,
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            LastError = "review failed",
        };
        await store.CreateAsync(item);

        var result = await retrier.RetryAsync(item, "plan_review");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.PlanReview, result.ResumeState);
        Assert.Equal("plan_review", result.ActualFrom);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.PlanReview, read!.State);
        Assert.Equal(ValidPlan, read.PlanArtifact);
        Assert.Equal(item.PlanGeneratedAt, read.PlanGeneratedAt);
        Assert.Null(read.LastError);
    }

    [Fact]
    public async Task RetryManual_FromPlanApproved_PreservesApprovedPlanAndResumesAtPlanApproved()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var reviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var item = Sample(WorkItemState.Failed) with
        {
            WorkBranch = null,
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = reviewedAt,
            PlanReviewSummary = "approved",
            LastError = "implementation failed",
        };
        await store.CreateAsync(item);

        var result = await retrier.RetryAsync(item, "plan_approved");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.PlanApproved, result.ResumeState);
        Assert.Equal("plan_approved", result.ActualFrom);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.PlanApproved, read!.State);
        Assert.Equal(ValidPlan, read.PlanArtifact);
        Assert.Equal(item.PlanGeneratedAt, read.PlanGeneratedAt);
        Assert.Equal(reviewedAt, read.PlanReviewedAt);
        Assert.Equal("approved", read.PlanReviewSummary);
        Assert.Null(read.LastError);
    }

    [Fact]
    public async Task RetryManual_FromPlanReviewWithoutPlan_IsRejected()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.Failed) with
        {
            WorkBranch = null,
            LastError = "planning never completed",
        };
        await store.CreateAsync(item);

        var result = await retrier.RetryAsync(item, "plan_review");

        Assert.False(result.Success);
        Assert.Contains("planning artifact is missing", result.Error, StringComparison.Ordinal);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Failed, read!.State);
        Assert.Equal("planning never completed", read.LastError);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RetryManual_FromPlanApprovedWithoutReview_IsRejected()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.Failed) with
        {
            WorkBranch = null,
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            LastError = "review never approved",
        };
        await store.CreateAsync(item);

        var result = await retrier.RetryAsync(item, "plan_approved");

        Assert.False(result.Success);
        Assert.Contains("has not been reviewed", result.Error, StringComparison.Ordinal);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Failed, read!.State);
        Assert.Equal("review never approved", read.LastError);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task RetryManual_FromWork_RequeuesAndClearsApprovedPlan()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.Failed) with
        {
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            PlanReviewSummary = "approved",
        };
        await store.CreateAsync(item);

        var result = await retrier.RetryAsync(item, "work");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal("work", result.ActualFrom);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Queued, read!.State);
        Assert.Null(read.PlanArtifact);
        Assert.Null(read.PlanGeneratedAt);
        Assert.Null(read.PlanReviewedAt);
        Assert.Null(read.PlanReviewSummary);
    }

    [Fact]
    public async Task AgentPauseResume_FromPlanningRequeuesAndClearsPlan()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.WaitingForAgentResume) with
        {
            AgentPauseRetryFrom = "planning",
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            PlanReviewSummary = "approved",
        };
        await store.CreateAsync(item);

        var result = await retrier.ResumeAfterAgentPauseAsync(item, "test");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.Resumed!.State);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Queued, read!.State);
        Assert.Null(read.PlanArtifact);
        Assert.Null(read.PlanGeneratedAt);
        Assert.Null(read.PlanReviewedAt);
        Assert.Null(read.PlanReviewSummary);
        Assert.Null(read.AgentPauseRetryFrom);
    }

    [Theory]
    [InlineData("plan_review")]
    [InlineData("plan_approved")]
    public async Task ResumeCancelled_ToPlanningBoundaryWithoutRequiredPlan_IsRejected(string from)
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.Cancelled) with
        {
            WorkBranch = null,
            LastError = "cancelled before planning completed",
        };
        await store.CreateAsync(item);

        var result = await retrier.ResumeAsync(item, from, "operator resume");

        Assert.Equal(WorkItemRetrier.ResumeStatus.Conflict, result.Status);
        Assert.Contains("planning artifact", result.Error, StringComparison.Ordinal);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Cancelled, read!.State);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ResumeCancelled_FromPlanApprovedWithoutReview_IsRejected()
    {
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var retrier = NewRetrier(store, queue);
        var item = Sample(WorkItemState.Cancelled) with
        {
            WorkBranch = null,
            PlanArtifact = ValidPlan,
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            LastError = "cancelled before plan approval",
        };
        await store.CreateAsync(item);

        var result = await retrier.ResumeAsync(item, "plan_approved", "operator resume");

        Assert.Equal(WorkItemRetrier.ResumeStatus.Conflict, result.Status);
        Assert.Contains("has not been reviewed", result.Error, StringComparison.Ordinal);
        var read = await store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Cancelled, read!.State);
        Assert.Equal(0, queue.Count);
    }

    private SqliteWorkItemStore NewStore()
        => new(Path.Combine(_workspace, Guid.NewGuid().ToString("N") + ".db"));

    private WorkItemRetrier NewRetrier(SqliteWorkItemStore store, InMemoryTaskQueue queue)
    {
        var gitHost = new LocalGitHost(
            new LocalGitHostOptions { RootDirectory = Path.Combine(_workspace, "repos-" + Guid.NewGuid().ToString("N")) },
            NullLogger<LocalGitHost>.Instance);
        return new WorkItemRetrier(store, queue, gitHost, NullLogger<WorkItemRetrier>.Instance);
    }

    private static WorkItem Sample(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "planning retrier",
        Prompt = "do work",
        State = state,
    };

    private const string ValidPlan = """
        {
          "approach": "retry planning",
          "files": ["output.txt"],
          "testStrategy": ["run tests"],
          "risks": ["none"],
          "satisfiesTask": "reruns planning"
        }
        """;
}

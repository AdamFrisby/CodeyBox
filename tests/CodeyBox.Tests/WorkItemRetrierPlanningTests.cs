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
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
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

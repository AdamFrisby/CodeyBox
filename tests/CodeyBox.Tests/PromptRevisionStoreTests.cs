using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for the prompt-revision Layer-1 surface on <see cref="SqliteWorkItemStore"/>:
/// the counter starts at 1, increments via <see cref="IWorkItemStore.TryReplacePromptAsync"/>,
/// and iteration-dispatch rows preserve the revision that was active at dispatch
/// time even after the prompt is bumped later.
/// </summary>
public sealed class PromptRevisionStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-rev-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public PromptRevisionStoreTests() => _store = new SqliteWorkItemStore(_dbPath);

    public void Dispose()
    {
        _store.Dispose();
        TestTempArtifacts.DeleteSqliteDatabase(_dbPath);
    }

    private static WorkItem Sample(WorkItemState state = WorkItemState.Queued) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "original prompt",
        State = state,
    };

    [Fact]
    public async Task NewWorkItem_HasPromptRevisionOne()
    {
        var item = Sample();
        await _store.CreateAsync(item);
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(1, read!.PromptRevision);
    }

    [Fact]
    public async Task TryReplacePromptAsync_IncrementsRevisionAndPersistsPrompt()
    {
        var item = Sample(WorkItemState.Working);
        await _store.CreateAsync(item);

        var first = await _store.TryReplacePromptAsync(item.Id, "v2", DateTimeOffset.UtcNow);
        Assert.Equal(PromptReplaceOutcome.Updated, first.Outcome);
        Assert.Equal(2, first.NewRevision);

        var second = await _store.TryReplacePromptAsync(item.Id, "v3", DateTimeOffset.UtcNow);
        Assert.Equal(3, second.NewRevision);

        var read = await _store.GetAsync(item.Id);
        Assert.Equal("v3", read!.Prompt);
        Assert.Equal(3, read.PromptRevision);
    }

    [Theory]
    [InlineData(WorkItemState.Planning)]
    [InlineData(WorkItemState.PlanReview)]
    [InlineData(WorkItemState.PlanApproved)]
    public async Task TryReplacePromptAsync_ClearsPlanAndRequeuesPlanningState(WorkItemState state)
    {
        var item = Sample(state) with
        {
            PlanArtifact = "PLAN:\nApproach: old prompt",
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewSummary = "old review",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };
        await _store.CreateAsync(item);

        var result = await _store.TryReplacePromptAsync(item.Id, "new prompt", DateTimeOffset.UtcNow);

        Assert.Equal(PromptReplaceOutcome.Updated, result.Outcome);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Queued, read!.State);
        Assert.Null(read.StartedAt);
        Assert.Null(read.PlanArtifact);
        Assert.Null(read.PlanGeneratedAt);
        Assert.Null(read.PlanReviewedAt);
        Assert.Null(read.PlanReviewSummary);
        Assert.Equal("new prompt", read.Prompt);
        Assert.Equal(2, read.PromptRevision);
    }

    [Fact]
    public async Task TryReplacePromptAsync_ClearsPlanWithoutChangingNonPlanningState()
    {
        var item = Sample(WorkItemState.Working) with
        {
            PlanArtifact = "PLAN:\nApproach: old prompt",
            PlanGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            PlanReviewedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            PlanReviewSummary = "old review",
        };
        await _store.CreateAsync(item);

        var result = await _store.TryReplacePromptAsync(item.Id, "new prompt", DateTimeOffset.UtcNow);

        Assert.Equal(PromptReplaceOutcome.Updated, result.Outcome);
        var read = await _store.GetAsync(item.Id);
        Assert.NotNull(read);
        Assert.Equal(WorkItemState.Working, read!.State);
        Assert.Null(read.PlanArtifact);
        Assert.Null(read.PlanGeneratedAt);
        Assert.Null(read.PlanReviewedAt);
        Assert.Null(read.PlanReviewSummary);
        Assert.Equal(2, read.PromptRevision);
    }

    [Fact]
    public async Task TryReplacePromptAsync_RejectsTerminalState()
    {
        var item = Sample(WorkItemState.Done);
        await _store.CreateAsync(item);

        var result = await _store.TryReplacePromptAsync(item.Id, "new", DateTimeOffset.UtcNow);
        Assert.Equal(PromptReplaceOutcome.TerminalState, result.Outcome);
        Assert.Null(result.NewRevision);

        var read = await _store.GetAsync(item.Id);
        Assert.Equal(1, read!.PromptRevision);
        Assert.Equal("original prompt", read.Prompt);
    }

    [Fact]
    public async Task TryReplacePromptAsync_NotFound_WhenIdMissing()
    {
        var result = await _store.TryReplacePromptAsync(WorkItemId.New(), "x", DateTimeOffset.UtcNow);
        Assert.Equal(PromptReplaceOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task IterationDispatch_SnapshottedRevisionIsImmutableAcrossPromptEdits()
    {
        // This is the core race the system protects against: an iteration was
        // dispatched with prompt_revision=1; the operator edits the prompt mid-run
        // (revision → 2). The recorded iteration row must still read back as 1.
        var item = Sample(WorkItemState.Working);
        await _store.CreateAsync(item);

        await _store.RecordIterationDispatchAsync(item.Id, iteration: 1,
            promptRevisionAtDispatch: 1, dispatchedAt: DateTimeOffset.UtcNow);

        await _store.TryReplacePromptAsync(item.Id, "edited mid-flight", DateTimeOffset.UtcNow);

        var rows = await _store.GetIterationsAsync(item.Id);
        Assert.Single(rows);
        Assert.Equal(1, rows[0].Iteration);
        Assert.Equal(1, rows[0].PromptRevisionAtDispatch);

        // The work item itself reflects the new revision; only the dispatch row
        // is frozen.
        var read = await _store.GetAsync(item.Id);
        Assert.Equal(2, read!.PromptRevision);
    }

    [Fact]
    public async Task GetIterationsAsync_ReturnsRowsInIterationOrder()
    {
        var item = Sample(WorkItemState.Reworking);
        await _store.CreateAsync(item);

        // Insert out of order to verify the ORDER BY iteration.
        await _store.RecordIterationDispatchAsync(item.Id, 3, 3, DateTimeOffset.UtcNow);
        await _store.RecordIterationDispatchAsync(item.Id, 1, 1, DateTimeOffset.UtcNow);
        await _store.RecordIterationDispatchAsync(item.Id, 2, 2, DateTimeOffset.UtcNow);

        var rows = await _store.GetIterationsAsync(item.Id);
        Assert.Equal([1, 2, 3], rows.Select(r => r.Iteration));
        Assert.Equal([1, 2, 3], rows.Select(r => r.PromptRevisionAtDispatch));
    }

    [Fact]
    public async Task PrimaryAcceptanceScenario_MidIterationPromptEdit_NextIterationCapturesNewRevision()
    {
        // Acceptance scenario from the task spec, exercised end-to-end against
        // the real store (the orchestrator's pickup + dispatch logic depends
        // entirely on this primitive): update a prompt while an iteration is
        // already in flight, then observe that
        //   (a) the in-flight iteration's recorded revision stays at 1
        //   (b) the next iteration is dispatched at revision 2
        //   (c) the next iteration is fed a snapshot reflecting the new revision
        // The PipelineRunner's rework-dispatch code re-reads the work item from
        // the store just before recording the dispatch row precisely so that
        // (c) holds — this test pins that behaviour at the store level.
        var item = Sample(WorkItemState.Working);
        await _store.CreateAsync(item);

        // Iteration 1 dispatches at the original revision.
        await _store.RecordIterationDispatchAsync(item.Id, 1, item.PromptRevision, DateTimeOffset.UtcNow);

        // Operator edits the prompt mid-iteration.
        var put = await _store.TryReplacePromptAsync(item.Id, "edited", DateTimeOffset.UtcNow);
        Assert.Equal(2, put.NewRevision);

        // Orchestrator re-reads before scheduling the NEXT iteration.
        var fresh = await _store.GetAsync(item.Id);
        Assert.NotNull(fresh);
        Assert.Equal(2, fresh!.PromptRevision);
        Assert.Equal("edited", fresh.Prompt);
        await _store.RecordIterationDispatchAsync(item.Id, 2, fresh.PromptRevision, DateTimeOffset.UtcNow);

        // Assertion (a) + (b): both rows landed with the expected dispatch revs.
        var rows = await _store.GetIterationsAsync(item.Id);
        Assert.Equal(2, rows.Count);
        var iter1 = rows.Single(r => r.Iteration == 1);
        var iter2 = rows.Single(r => r.Iteration == 2);
        Assert.Equal(1, iter1.PromptRevisionAtDispatch);
        Assert.Equal(2, iter2.PromptRevisionAtDispatch);
    }

    [Fact]
    public async Task RecordIterationDispatchAsync_IsIdempotent_OverwritesOnReDispatch()
    {
        // Restart-recovery re-dispatches the same iteration; the row must update
        // rather than create a duplicate (composite PK enforces this).
        var item = Sample(WorkItemState.Working);
        await _store.CreateAsync(item);

        var firstDispatch = DateTimeOffset.UtcNow;
        await _store.RecordIterationDispatchAsync(item.Id, 1, 1, firstDispatch);

        var secondDispatch = firstDispatch.AddMinutes(5);
        await _store.RecordIterationDispatchAsync(item.Id, 1, 2, secondDispatch);

        var rows = await _store.GetIterationsAsync(item.Id);
        Assert.Single(rows);
        Assert.Equal(2, rows[0].PromptRevisionAtDispatch);
    }
}

using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

public sealed class CheckAndActFollowupRecoveryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"codeybox-checkact-recovery-{Guid.NewGuid():N}.db");
    private readonly SqliteWorkItemStore _store;

    public CheckAndActFollowupRecoveryTests()
    {
        _store = new SqliteWorkItemStore(_dbPath);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { File.Delete(_dbPath); } catch { }
        try { File.Delete(_dbPath + "-wal"); } catch { }
        try { File.Delete(_dbPath + "-shm"); } catch { }
    }

    [Fact]
    public async Task PersistedNonActionableVerdict_CompletesWithoutFollowup()
    {
        var item = MakeCheckItem(answer: false, actionableAnswer: true);
        item = item with
        {
            PreemptCheckpoint = null,
            AgentTurnResumeCheckpoint = new AgentTurnResumeCheckpoint(
                AgentKind.Claude,
                "claude/default",
                modelId: null,
                reasoningMode: null,
                nativeSessionId: null,
                WorkItemState.Working,
                AgentTurnResumePhase.Work,
                iteration: null,
                item.PromptRevision,
                DateTimeOffset.UtcNow.AddMinutes(-2)),
            AgentTurnRecoveryLease = new SandboxRecoveryLease(
                "incus",
                "retained-check-sandbox",
                "retained-check-token"),
        };

        var completed = await CheckAndActFollowupRecovery.TryBuildCompletedFromPersistedVerdictAsync(
            _store, item, CancellationToken.None);

        Assert.NotNull(completed);
        Assert.Equal(WorkItemState.Done, completed!.State);
        Assert.Null(completed.LastError);
        Assert.Null(completed.StartedAt);
        Assert.Null(completed.PreemptedAt);
        Assert.Null(completed.PreemptCheckpoint);
        Assert.Null(completed.AgentTurnResumeCheckpoint);
        Assert.Null(completed.AgentTurnRecoveryLease);
    }

    [Fact]
    public async Task PersistedActionableVerdictWithoutFollowup_ReturnsNullSoCheckReruns()
    {
        var item = MakeCheckItem(answer: true, actionableAnswer: true);

        var completed = await CheckAndActFollowupRecovery.TryBuildCompletedFromPersistedVerdictAsync(
            _store, item, CancellationToken.None);

        Assert.Null(completed);
    }

    [Fact]
    public async Task EnqueueIfReady_NullQueue_ReturnsFalse()
    {
        var followup = MakeFollowup(WorkItemState.Queued);

        var enqueued = await CheckAndActFollowupRecovery.EnqueueIfReadyAsync(
            _store, queue: null, followup, CancellationToken.None);

        Assert.False(enqueued);
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Working)]
    public async Task EnqueueIfReady_NonQueuedFollowup_DoesNotKickQueue(WorkItemState state)
    {
        var queue = new InMemoryTaskQueue();
        var followup = MakeFollowup(state);

        var enqueued = await CheckAndActFollowupRecovery.EnqueueIfReadyAsync(
            _store, queue, followup, CancellationToken.None);

        Assert.False(enqueued);
        Assert.Equal(0, queue.Count);
    }

    private static WorkItem MakeCheckItem(bool answer, bool actionableAnswer) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Check",
        Prompt = "p",
        State = WorkItemState.Working,
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        PreemptedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        PreemptCheckpoint = "checkpoint",
        LastError = "stale",
        JobType = JobType.CheckAndAct,
        Check = new CheckAndActSpec
        {
            Question = "Is action needed?",
            ActionableAnswer = actionableAnswer,
            OnYes = new OnYesActionSpec
            {
                Title = "Act",
                Prompt = "Act on the check.",
            },
        },
        Verdict = new CheckVerdict
        {
            Answer = answer,
            Evidence = answer ? "actionable" : "not actionable",
        },
    };

    private static WorkItem MakeFollowup(WorkItemState state) => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("test-project"),
        Title = "Follow up",
        Prompt = "p",
        State = state,
        JobType = JobType.Normal,
        OriginCheckWorkItemId = WorkItemId.New(),
    };
}

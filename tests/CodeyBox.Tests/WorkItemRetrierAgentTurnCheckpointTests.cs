using CodeyBox.Agents;
using CodeyBox.Core;
using CodeyBox.Git;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeyBox.Tests;

[Collection("GlobalSerilog")]
public sealed class WorkItemRetrierAgentTurnCheckpointTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("codeybox-retrier-checkpoint-").FullName;
    private readonly int _originalResumeAttempts = SessionResumeOptions.MaxResumeAttempts;

    public void Dispose()
    {
        SessionResumeOptions.SetMaxResumeAttempts(_originalResumeAttempts);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task AutoPick_ValidWorkCheckpointResumesExactTurnWithoutClaimingDispatchEarly()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure);
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item, createWorkBranch: false);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Working, result.ResumeState);
        Assert.Equal(RetryFromPolicy.Work, result.ActualFrom);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.Working, persisted!.State);
        Assert.Equal(item.WorkBranch, persisted.WorkBranch);
        Assert.Equal(item.PreemptCheckpoint, persisted.PreemptCheckpoint);
        Assert.Equal(AgentKind.Claude, persisted.Agent);
        Assert.Equal("claude/acct-a", persisted.AgentInstanceId);
        Assert.Equal(0, persisted.AgentTurnResumeCheckpoint!.AttemptCount);
        Assert.Equal("claude-opus-4-7", persisted.AgentTurnResumeCheckpoint.ModelId);
        Assert.Equal("high", persisted.AgentTurnResumeCheckpoint.ReasoningMode);
        Assert.Equal("native-session-retrier", persisted.AgentTurnResumeCheckpoint.NativeSessionId?.Value);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AutoPick_ValidRetainedSandboxResumesExactTurnAndPreservesLeaseForAdoption()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var lease = new SandboxRecoveryLease(
            "incus",
            "codeybox-retained-retrier",
            "retained-retrier-token");
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure) with
        {
            PreemptCheckpoint = null,
            AgentTurnRecoveryLease = lease,
        };
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item, createWorkBranch: false);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Working, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.PreemptCheckpoint);
        Assert.Equal(lease, persisted.AgentTurnRecoveryLease);
        Assert.Equal(item.AgentTurnResumeCheckpoint, persisted.AgentTurnResumeCheckpoint);
        Assert.True(persisted.HasAgentTurnRecoveryBoundary);
        Assert.Equal(item.Id, await queue.DequeueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ExplicitMismatchedBoundary_RefusesToDiscardRetainedSandboxLease()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var lease = new SandboxRecoveryLease(
            "incus",
            "codeybox-retained-mismatch",
            "retained-mismatch-token");
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Rework,
            failureKind: WorkItemFailureKinds.Infrastructure) with
        {
            PreemptCheckpoint = null,
            AgentTurnRecoveryLease = lease,
        };
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Work);

        Assert.False(result.Success);
        Assert.Contains("cannot discard", result.Error, StringComparison.Ordinal);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(item.State, persisted!.State);
        Assert.Equal(lease, persisted.AgentTurnRecoveryLease);
        Assert.Equal(item.AgentTurnResumeCheckpoint, persisted.AgentTurnResumeCheckpoint);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task QueueFailureAfterDanglingLeaseDiscard_DoesNotResurrectMalformedRecoveryMetadata()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure) with
        {
            PreemptCheckpoint = null,
            AgentTurnResumeCheckpoint = null,
            AgentTurnRecoveryLease = new SandboxRecoveryLease(
                "incus",
                "codeybox-dangling-retrier",
                "dangling-retrier-token"),
        };
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, new ThrowingTaskQueue(), gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Work);

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Error, StringComparison.Ordinal);
        var rolledBack = await store.GetAsync(item.Id);
        Assert.NotNull(rolledBack);
        Assert.Equal(WorkItemState.Failed, rolledBack!.State);
        Assert.Null(rolledBack.PreemptCheckpoint);
        Assert.Null(rolledBack.AgentTurnResumeCheckpoint);
        Assert.Null(rolledBack.AgentTurnRecoveryLease);
    }

    [Fact]
    public async Task Retry_ClaimedCheckpointFailsClosedWithoutDuplicateQueueDispatch()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure);
        item = item with
        {
            AgentTurnResumeCheckpoint = item.AgentTurnResumeCheckpoint!
                .ClaimDispatch(Guid.Parse("4ff43403-01a7-45e7-9fb1-d3ac58b961ba")),
        };
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item, createWorkBranch: false);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: null);

        Assert.False(result.Success);
        Assert.Contains("active dispatch", result.Error, StringComparison.Ordinal);
        var persisted = await store.GetAsync(item.Id);
        Assert.Equal(item.State, persisted?.State);
        Assert.Equal(
            item.AgentTurnResumeCheckpoint.DispatchClaimId,
            persisted?.AgentTurnResumeCheckpoint?.DispatchClaimId);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task ManualReworkRetry_ResumesOriginalReworkStateAndIteration()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.WaitingForTransientRetry,
            AgentTurnResumePhase.Rework,
            failureKind: "transient");
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Rework);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Reworking, result.ResumeState);
        Assert.Equal(RetryFromPolicy.Rework, result.ActualFrom);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.Reworking, persisted!.State);
        Assert.Equal(3, persisted.AgentTurnResumeCheckpoint!.Iteration);
        Assert.Equal(0, persisted.AgentTurnResumeCheckpoint.AttemptCount);
        Assert.Equal(item.WorkBranch, persisted.WorkBranch);
    }

    [Fact]
    public async Task ExplicitMismatchedWorkBoundary_DiscardsReworkCheckpoint()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Rework,
            failureKind: WorkItemFailureKinds.Infrastructure);
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Work);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        Assert.Equal(RetryFromPolicy.Work, result.ActualFrom);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.WorkBranch);
        Assert.Null(persisted.PreemptCheckpoint);
        Assert.Null(persisted.AgentTurnResumeCheckpoint);
    }

    [Fact]
    public async Task ExplicitMismatchedReworkBoundary_DiscardsWorkCheckpoint()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure);
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Rework);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.WorkComplete, result.ResumeState);
        Assert.Equal(RetryFromPolicy.Rework, result.ActualFrom);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.WorkComplete, persisted!.State);
        Assert.Null(persisted.PreemptedAt);
        Assert.Null(persisted.PreemptCheckpoint);
        Assert.Null(persisted.AgentTurnResumeCheckpoint);
    }

    [Fact]
    public async Task QuotaAutoRetry_FromWorkUsesCheckpointWithoutClaimingDispatchEarly()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.WaitingForQuotaReset,
            AgentTurnResumePhase.Work,
            failureKind: "quota");
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryQuotaAutoAsync(
            item,
            from: RetryFromPolicy.Work,
            trigger: "quota-reset");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Working, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted!.QuotaRetryAttempts);
        Assert.Equal(0, persisted.AgentTurnResumeCheckpoint!.AttemptCount);
        Assert.Equal(item.PreemptCheckpoint, persisted.PreemptCheckpoint);
    }

    [Fact]
    public async Task TransientAutoRetry_FromReworkUsesSnapshotGuardWithoutClaimingDispatchEarly()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.WaitingForTransientRetry,
            AgentTurnResumePhase.Rework,
            failureKind: "transient");
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item, createWorkBranch: false);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryTransientAutoAsync(
            item,
            from: RetryFromPolicy.Rework,
            trigger: "transient-backoff");

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Reworking, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(1, persisted!.TransientRetryAttempts);
        Assert.Equal(0, persisted.AgentTurnResumeCheckpoint!.AttemptCount);
        Assert.Equal(item.PreemptCheckpoint, persisted.PreemptCheckpoint);
    }

    [Fact]
    public async Task AgentRestoreAutoRetry_AtomicallyClaimsRestoreWithoutClaimingDispatchEarly()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure);
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item, createWorkBranch: false);
        var retrier = NewRetrier(store, queue, gitHost);
        var outageStartedAt = CheckpointCreatedAt.AddMinutes(-5);
        var restoredAt = CheckpointCreatedAt.AddMinutes(5);

        var result = await retrier.RetryAgentRestoreAsync(
            item,
            from: null,
            trigger: "agent-restored",
            restoredAgent: AgentKind.Claude,
            outageStartedAt: outageStartedAt,
            restoredAt: restoredAt);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Working, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(0, persisted!.AgentTurnResumeCheckpoint!.AttemptCount);
        Assert.True(await store.HasAgentRestoreRetryClaimAsync(
            item.Id,
            AgentKind.Claude,
            outageStartedAt));
    }

    [Fact]
    public async Task ExplicitLaterPhaseRetry_DiscardsBothCheckpointsAndPreservesBranch()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Rework,
            failureKind: WorkItemFailureKinds.Infrastructure);
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Audit);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.WorkComplete, result.ResumeState);
        Assert.Equal(RetryFromPolicy.Audit, result.ActualFrom);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.WorkComplete, persisted!.State);
        Assert.Equal(item.WorkBranch, persisted.WorkBranch);
        Assert.Null(persisted.PreemptCheckpoint);
        Assert.Null(persisted.AgentTurnResumeCheckpoint);
    }

    [Fact]
    public async Task ChangedPromptRevision_DiscardsStaleCheckpointAndUsesFreshWorkBoundary()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var checkpointed = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure);
        var item = checkpointed with { PromptRevision = checkpointed.PromptRevision + 1 };
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Work);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkItemState.Queued, persisted!.State);
        Assert.Null(persisted.WorkBranch);
        Assert.Null(persisted.PreemptCheckpoint);
        Assert.Null(persisted.AgentTurnResumeCheckpoint);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task DisabledOrExhaustedHotReloadedAttemptCap_DiscardsCheckpointWithoutThrowing(
        int configuredLimit,
        int attemptCount)
    {
        SessionResumeOptions.SetMaxResumeAttempts(configuredLimit);
        using var store = NewStore();
        var queue = new InMemoryTaskQueue();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure,
            attemptCount: attemptCount);
        await store.CreateAsync(item);
        var retrier = NewRetrier(store, queue, gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Work);

        Assert.True(result.Success, result.Error);
        Assert.Equal(WorkItemState.Queued, result.ResumeState);
        var persisted = await store.GetAsync(item.Id);
        Assert.NotNull(persisted);
        Assert.Null(persisted!.PreemptCheckpoint);
        Assert.Null(persisted.AgentTurnResumeCheckpoint);
    }

    [Fact]
    public async Task QueueFailureRollback_RestoresOriginalCheckpointBranchStateAndAttempt()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure);
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item);
        var retrier = NewRetrier(store, new ThrowingTaskQueue(), gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Work);

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Error, StringComparison.Ordinal);
        var rolledBack = await store.GetAsync(item.Id);
        Assert.NotNull(rolledBack);
        Assert.Equal(WorkItemState.Failed, rolledBack!.State);
        Assert.Equal(item.FailureKind, rolledBack.FailureKind);
        Assert.Equal(item.LastError, rolledBack.LastError);
        Assert.Equal(item.WorkBranch, rolledBack.WorkBranch);
        Assert.Equal(item.PreemptCheckpoint, rolledBack.PreemptCheckpoint);
        Assert.Equal(0, rolledBack.AgentTurnResumeCheckpoint!.AttemptCount);
        Assert.Equal(item.AgentTurnResumeCheckpoint, rolledBack.AgentTurnResumeCheckpoint);
    }

    [Fact]
    public async Task QueueFailureAfterCheckpointDiscard_DoesNotResurrectMetadataWithoutDeletedBlob()
    {
        SessionResumeOptions.SetMaxResumeAttempts(3);
        using var store = NewStore();
        var gitHost = NewGitHost();
        var item = NewRecoverableItem(
            WorkItemState.Failed,
            AgentTurnResumePhase.Work,
            failureKind: WorkItemFailureKinds.Infrastructure);
        await store.CreateAsync(item);
        await CreateRepositoryAsync(gitHost, item);
        var checkpointRef = AgentTurnCheckpointRef.Parse(item.PreemptCheckpoint!);
        var scratchpad = new AgentTurnScratchpadArchive(new byte[] { 1 });
        await store.SaveAsync(item.Id, checkpointRef, scratchpad);
        Assert.NotNull(await store.ReadAsync(item.Id, checkpointRef));
        var retrier = NewRetrier(store, new ThrowingTaskQueue(), gitHost);

        var result = await retrier.RetryAsync(item, from: RetryFromPolicy.Audit);

        Assert.False(result.Success);
        Assert.Contains("rolled back", result.Error, StringComparison.Ordinal);
        var rolledBack = await store.GetAsync(item.Id);
        Assert.NotNull(rolledBack);
        Assert.Equal(WorkItemState.Failed, rolledBack!.State);
        Assert.Null(rolledBack.PreemptedAt);
        Assert.Null(rolledBack.PreemptCheckpoint);
        Assert.Null(rolledBack.AgentTurnResumeCheckpoint);
        Assert.Null(await store.ReadAsync(item.Id, checkpointRef));
    }

    private SqliteWorkItemStore NewStore() =>
        new(Path.Combine(_directory, $"state-{Guid.NewGuid():N}.db"));

    private LocalGitHost NewGitHost() => new(
        new LocalGitHostOptions
        {
            RootDirectory = Path.Combine(_directory, $"repos-{Guid.NewGuid():N}"),
        },
        NullLogger<LocalGitHost>.Instance);

    private async Task CreateRepositoryAsync(
        LocalGitHost gitHost,
        WorkItem item,
        bool createWorkBranch = true)
    {
        var seed = await TestSupport.CreateSeedRepoAsync(_directory);
        var repoId = await gitHost.EnsureRepositoryAsync(item.Id, seed, "main");
        var barePath = gitHost.GetRepoPath(repoId);
        if (createWorkBranch)
        {
            await TestSupport.RunGit(
                barePath,
                "update-ref",
                $"refs/heads/{item.WorkBranch}",
                "refs/heads/main");
        }
        if (item.PreemptCheckpoint is not null)
        {
            await TestSupport.RunGit(
                barePath,
                "update-ref",
                item.PreemptCheckpoint,
                "refs/heads/main");
        }
    }

    private static WorkItemRetrier NewRetrier(
        IWorkItemStore store,
        ITaskQueue queue,
        IGitHost gitHost) => new(
        store,
        queue,
        gitHost,
        NullLogger<WorkItemRetrier>.Instance);

    private static WorkItem NewRecoverableItem(
        WorkItemState state,
        AgentTurnResumePhase phase,
        string failureKind,
        int attemptCount = 0)
    {
        var id = WorkItemId.New();
        var promptRevision = 5;
        var resumeState = phase == AgentTurnResumePhase.Work
            ? WorkItemState.Working
            : WorkItemState.Reworking;
        var checkpointRef = AgentTurnCheckpointRef.Create(
            id,
            new string('1', 40),
            new AgentTurnScratchpadArchive(new byte[] { 1 }));
        return new WorkItem
        {
            Id = id,
            ProjectId = new ProjectId("retrier-checkpoint"),
            Title = "resume native turn",
            Prompt = "continue the interrupted task",
            PromptRevision = promptRevision,
            BaseBranch = "main",
            WorkBranch = $"codeybox/resume-{Guid.NewGuid():N}",
            State = state,
            LastError = "agent infrastructure failed",
            FailureKind = failureKind,
            Agent = AgentKind.Codex,
            AgentInstanceId = "codex/original",
            PreemptedAt = CheckpointCreatedAt,
            PreemptCheckpoint = checkpointRef.Value,
            AgentTurnResumeCheckpoint = new AgentTurnResumeCheckpoint(
                AgentKind.Claude,
                "claude/acct-a",
                "claude-opus-4-7",
                "high",
                new AgentNativeSessionId("native-session-retrier"),
                resumeState,
                phase,
                phase == AgentTurnResumePhase.Rework ? 3 : null,
                promptRevision,
                CheckpointCreatedAt,
                attemptCount),
        };
    }

    private sealed class ThrowingTaskQueue : ITaskQueue
    {
        public int Count => 0;

        public ValueTask EnqueueAsync(WorkItemId id, CancellationToken ct = default) =>
            throw new InvalidOperationException("queue enqueue failed");

        public ValueTask EnqueueDispatchWakeAsync(CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        public ValueTask<WorkItemId?> DequeueAsync(CancellationToken ct = default) =>
            ValueTask.FromResult<WorkItemId?>(null);
    }

    private static readonly DateTimeOffset CheckpointCreatedAt =
        new(2026, 7, 12, 2, 3, 4, TimeSpan.Zero);
}

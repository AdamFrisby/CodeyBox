using System.Text.Json;
using CodeyBox.Core;

namespace CodeyBox.Tests;

public sealed class AgentTurnResumeCheckpointTests
{
    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 12, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void ConstructorAndIncrement_PreserveExactResumeIdentity()
    {
        var checkpoint = CreateCheckpoint(attemptCount: 1);

        var incremented = checkpoint.IncrementAttemptCount();

        Assert.Equal(AgentKind.Claude, incremented.Agent);
        Assert.Equal("claude/acct-a", incremented.AgentInstanceRoute);
        Assert.Equal("claude-opus-4-7", incremented.ModelId);
        Assert.Equal("high", incremented.ReasoningMode);
        Assert.Equal("native-session-123", incremented.NativeSessionId?.Value);
        Assert.Equal(WorkItemState.Reworking, incremented.ResumeState);
        Assert.Equal(AgentTurnResumePhase.Rework, incremented.Phase);
        Assert.Equal(3, incremented.Iteration);
        Assert.Equal(7, incremented.PromptRevision);
        Assert.Equal(CreatedAt, incremented.CreatedAt);
        Assert.Equal(2, incremented.AttemptCount);
        Assert.Equal(1, checkpoint.AttemptCount);
    }

    [Fact]
    public void ConstructorAndIncrement_AllowScratchpadRecoveryWithoutNativeSessionId()
    {
        var checkpoint = new AgentTurnResumeCheckpoint(
            AgentKind.Claude,
            "claude/acct-a",
            modelId: null,
            reasoningMode: null,
            nativeSessionId: null,
            WorkItemState.Working,
            AgentTurnResumePhase.Work,
            iteration: null,
            promptRevision: 7,
            CreatedAt);

        var incremented = checkpoint.IncrementAttemptCount();

        Assert.Null(checkpoint.NativeSessionId);
        Assert.Null(incremented.NativeSessionId);
        Assert.Equal(1, incremented.AttemptCount);
    }

    [Fact]
    public void Constructor_RejectsStatePhaseMismatch()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentTurnResumeCheckpoint(
            AgentKind.Claude,
            "claude/acct-a",
            modelId: null,
            reasoningMode: null,
            new AgentNativeSessionId("native-session-123"),
            WorkItemState.Working,
            AgentTurnResumePhase.Rework,
            iteration: null,
            promptRevision: 1,
            CreatedAt));

        Assert.Equal("phase", exception.ParamName);
    }

    [Fact]
    public void Constructor_RejectsInstanceRouteOwnedByDifferentAgent()
    {
        var exception = Assert.Throws<ArgumentException>(() => new AgentTurnResumeCheckpoint(
            AgentKind.Claude,
            "codex/acct-a",
            modelId: null,
            reasoningMode: null,
            new AgentNativeSessionId("native-session-123"),
            WorkItemState.Working,
            AgentTurnResumePhase.Work,
            iteration: null,
            promptRevision: 1,
            CreatedAt));

        Assert.Equal("agentInstanceRoute", exception.ParamName);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(AgentTurnResumeCheckpoint.MaximumAttemptCount + 1)]
    public void Constructor_RejectsAttemptCountOutsideStorageBound(int attemptCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateCheckpoint(attemptCount));
    }

    [Fact]
    public void Increment_RejectsStorageBoundWithoutOverflowing()
    {
        var checkpoint = CreateCheckpoint(AgentTurnResumeCheckpoint.MaximumAttemptCount);

        Assert.Throws<InvalidOperationException>(checkpoint.IncrementAttemptCount);
        Assert.Equal(AgentTurnResumeCheckpoint.MaximumAttemptCount, checkpoint.AttemptCount);
    }

    [Fact]
    public void ClaimDispatch_IsExclusive_AndRecoveryReleaseDoesNotRefundAttempt()
    {
        var checkpoint = CreateCheckpoint(attemptCount: 1);
        var claimId = Guid.Parse("8b8b0f9e-9965-4f04-9013-a2e37c747f7d");

        var claimed = checkpoint.ClaimDispatch(claimId);

        Assert.Equal(2, claimed.AttemptCount);
        Assert.Equal(claimId, claimed.DispatchClaimId);
        Assert.Equal(AgentTurnDispatchClaimStage.Dispatched, claimed.DispatchClaimStage);
        Assert.Throws<InvalidOperationException>(() => claimed.ClaimDispatch(Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(claimed.IncrementAttemptCount);

        var released = claimed.ReleaseDispatchClaim();
        Assert.Equal(2, released.AttemptCount);
        Assert.Null(released.DispatchClaimId);
        Assert.Null(released.DispatchClaimStage);

        Assert.Equal(3, released.ClaimDispatch(Guid.NewGuid()).AttemptCount);
    }

    [Fact]
    public void PreparationClaim_FencesAdoptionAndRecoveryReleaseDoesNotConsumeAttempt()
    {
        var checkpoint = CreateCheckpoint(attemptCount: 1);
        var claimId = Guid.Parse("a63e6f9d-5b61-4718-af8c-d56aed5ad8e3");

        var preparing = checkpoint.ClaimPreparation(claimId);

        Assert.Equal(1, preparing.AttemptCount);
        Assert.Equal(claimId, preparing.DispatchClaimId);
        Assert.Equal(AgentTurnDispatchClaimStage.Preparation, preparing.DispatchClaimStage);
        Assert.Throws<InvalidOperationException>(preparing.ReleaseUndispatchedClaim);

        var released = preparing.ReleaseDispatchClaim();
        Assert.Equal(1, released.AttemptCount);
        Assert.Null(released.DispatchClaimId);
        Assert.Null(released.DispatchClaimStage);

        var exhaustedDispatchBudget = CreateCheckpoint(
            attemptCount: AgentTurnResumeCheckpoint.MaximumAttemptCount);
        Assert.Equal(
            AgentTurnResumeCheckpoint.MaximumAttemptCount,
            exhaustedDispatchBudget.ClaimPreparation(Guid.NewGuid()).AttemptCount);
    }

    [Fact]
    public void ReleaseUndispatchedClaim_RefundsOnlyClaimedPreDispatchAttempt()
    {
        var checkpoint = CreateCheckpoint(attemptCount: 1);
        var claimed = checkpoint.ClaimDispatch(Guid.Parse("62327a71-df5d-4249-a557-8bddcf6daab2"));

        var refunded = claimed.ReleaseUndispatchedClaim();

        Assert.Equal(checkpoint.AttemptCount, refunded.AttemptCount);
        Assert.Null(refunded.DispatchClaimId);
        Assert.Throws<InvalidOperationException>(checkpoint.ReleaseUndispatchedClaim);
    }

    [Fact]
    public void Constructor_RejectsEmptyDispatchClaimId()
    {
        Assert.Throws<ArgumentException>(() => new AgentTurnResumeCheckpoint(
            AgentKind.Claude,
            "claude/acct-a",
            modelId: null,
            reasoningMode: null,
            nativeSessionId: null,
            WorkItemState.Working,
            AgentTurnResumePhase.Work,
            iteration: null,
            promptRevision: 7,
            CreatedAt,
            attemptCount: 1,
            dispatchClaimId: Guid.Empty));
    }

    [Fact]
    public void WorkItemJson_DoesNotExposeNativeSessionCheckpoint()
    {
        var item = Sample() with
        {
            AgentTurnResumeCheckpoint = CreateCheckpoint(),
        };

        var json = JsonSerializer.Serialize(item);

        Assert.DoesNotContain(nameof(WorkItem.AgentTurnResumeCheckpoint), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("native-session-123", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WorkItemState.Working, null, true)]
    [InlineData(WorkItemState.Reworking, null, true)]
    [InlineData(WorkItemState.WaitingForQuotaReset, "quota", true)]
    [InlineData(WorkItemState.WaitingForTransientRetry, "transient", true)]
    [InlineData(WorkItemState.WaitingForAgentResume, null, true)]
    [InlineData(WorkItemState.Failed, WorkItemFailureKinds.Infrastructure, true)]
    [InlineData(WorkItemState.Failed, WorkItemFailureKinds.AgentUnavailable, true)]
    [InlineData(WorkItemState.Failed, WorkItemFailureKinds.AuthRequired, true)]
    [InlineData(WorkItemState.Failed, "agent", false)]
    [InlineData(WorkItemState.WorkComplete, null, false)]
    [InlineData(WorkItemState.Auditing, null, false)]
    [InlineData(WorkItemState.Queued, null, false)]
    public void With_RetainsOrClearsGitAndNativeCheckpointsTogether(
        WorkItemState target,
        string? failureKind,
        bool expectedToRetain)
    {
        var checkpoint = target == WorkItemState.Working
            ? CreateWorkCheckpoint()
            : CreateCheckpoint();
        var preemptedAt = CreatedAt.AddMinutes(1);
        var original = Sample();
        var item = original with
        {
            State = checkpoint.ResumeState,
            WorkBranch = "codeybox/work",
            PreemptedAt = preemptedAt,
            PreemptCheckpoint = $"refs/heads/codeybox/preempt/{original.Id}",
            AgentTurnResumeCheckpoint = checkpoint,
        };

        var transitioned = item.With(target, failureKind: failureKind);

        if (expectedToRetain)
        {
            Assert.NotNull(transitioned.PreemptCheckpoint);
            Assert.Same(checkpoint, transitioned.AgentTurnResumeCheckpoint);
            Assert.Equal(preemptedAt, transitioned.PreemptedAt);
        }
        else
        {
            Assert.Null(transitioned.PreemptCheckpoint);
            Assert.Null(transitioned.AgentTurnResumeCheckpoint);
            Assert.Null(transitioned.PreemptedAt);
        }
    }

    [Fact]
    public void RecoveryBoundary_AcceptsLegacyGitAndExactlyOneTypedBacking()
    {
        var original = Sample();
        var checkpoint = CreateWorkCheckpoint();
        var checkpointRef = $"refs/heads/codeybox/preempt/{original.Id}";
        var lease = new SandboxRecoveryLease("incus", "sandbox-1", "recovery-token");

        Assert.True((original with { PreemptCheckpoint = checkpointRef }).HasAgentTurnRecoveryBoundary);
        Assert.True((original with
        {
            PreemptCheckpoint = checkpointRef,
            AgentTurnResumeCheckpoint = checkpoint,
        }).HasAgentTurnRecoveryBoundary);
        Assert.True((original with
        {
            AgentTurnResumeCheckpoint = checkpoint,
            AgentTurnRecoveryLease = lease,
        }).HasAgentTurnRecoveryBoundary);

        Assert.False((original with { AgentTurnRecoveryLease = lease }).HasAgentTurnRecoveryBoundary);
        Assert.False((original with { AgentTurnResumeCheckpoint = checkpoint }).HasAgentTurnRecoveryBoundary);
        Assert.False((original with
        {
            PreemptCheckpoint = checkpointRef,
            AgentTurnResumeCheckpoint = checkpoint,
            AgentTurnRecoveryLease = lease,
        }).HasAgentTurnRecoveryBoundary);
    }

    [Fact]
    public void With_RetainsValidLeaseOnlyInRecoveryStatesAndClearsMalformedLease()
    {
        var checkpoint = CreateWorkCheckpoint();
        var lease = new SandboxRecoveryLease("incus", "sandbox-1", "recovery-token");
        var valid = Sample() with
        {
            State = WorkItemState.Working,
            StartedAt = CreatedAt,
            PreemptedAt = CreatedAt,
            AgentTurnResumeCheckpoint = checkpoint,
            AgentTurnRecoveryLease = lease,
        };

        var parked = valid.With(WorkItemState.NeedsOperatorInput, "operator recovery required");
        var completed = valid.With(WorkItemState.WorkComplete);
        var malformed = (Sample() with
        {
            State = WorkItemState.Working,
            AgentTurnRecoveryLease = lease,
        }).With(WorkItemState.NeedsOperatorInput);

        Assert.Same(checkpoint, parked.AgentTurnResumeCheckpoint);
        Assert.Same(lease, parked.AgentTurnRecoveryLease);
        Assert.True(parked.HasAgentTurnRecoveryBoundary);
        Assert.False(WorkItemInFlight.IsInFlight(valid));
        Assert.Null(completed.AgentTurnResumeCheckpoint);
        Assert.Null(completed.AgentTurnRecoveryLease);
        Assert.Null(malformed.AgentTurnRecoveryLease);
    }

    private static AgentTurnResumeCheckpoint CreateCheckpoint(int attemptCount = 0) => new(
        AgentKind.Claude,
        "claude/acct-a",
        "claude-opus-4-7",
        "high",
        new AgentNativeSessionId("native-session-123"),
        WorkItemState.Reworking,
        AgentTurnResumePhase.Rework,
        iteration: 3,
        promptRevision: 7,
        CreatedAt,
        attemptCount);

    private static AgentTurnResumeCheckpoint CreateWorkCheckpoint() => new(
        AgentKind.Claude,
        "claude/acct-a",
        "claude-opus-4-7",
        "high",
        new AgentNativeSessionId("native-session-123"),
        WorkItemState.Working,
        AgentTurnResumePhase.Work,
        iteration: null,
        promptRevision: 7,
        CreatedAt);

    private static WorkItem Sample() => new()
    {
        Id = WorkItemId.New(),
        ProjectId = new ProjectId("resume-checkpoint"),
        Title = "resume checkpoint",
        Prompt = "continue",
        PromptRevision = 7,
    };
}

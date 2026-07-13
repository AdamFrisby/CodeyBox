using System.Text.Json.Serialization;

namespace CodeyBox.Core;

/// <summary>Agent phase represented by a durable turn-resume checkpoint.</summary>
public enum AgentTurnResumePhase
{
    Work,
    Rework,
}

/// <summary>Durable ownership stage for an exclusive resumed-turn claim.</summary>
public enum AgentTurnDispatchClaimStage
{
    Preparation,
    Dispatched,
}

/// <summary>
/// Durable, provider-neutral metadata required to resume an interrupted work or
/// rework agent turn. A provider-native session identifier is carried when one
/// was captured; dirty-tree and scratchpad recovery remains valid without it.
/// The metadata is intentionally kept off public work-item JSON;
/// <see cref="WorkItem.AgentTurnResumeCheckpoint"/> is an internal orchestration
/// detail persisted by the work-item store.
/// </summary>
public sealed record AgentTurnResumeCheckpoint
{
    /// <summary>Maximum characters in the persisted agent-kind token.</summary>
    public const int MaximumAgentKindLength = 64;
    /// <summary>Maximum characters in the persisted canonical agent route.</summary>
    public const int MaximumAgentInstanceRouteLength = 256;
    /// <summary>Maximum characters in a persisted model identifier.</summary>
    public const int MaximumModelIdLength = 256;
    /// <summary>Maximum characters in a persisted reasoning-mode token.</summary>
    public const int MaximumReasoningModeLength = 64;
    /// <summary>Hard storage bound; the hot-reloadable retry policy may be lower.</summary>
    public const int MaximumAttemptCount = 10;

    [JsonConstructor]
    public AgentTurnResumeCheckpoint(
        AgentKind agent,
        string agentInstanceRoute,
        string? modelId,
        string? reasoningMode,
        AgentNativeSessionId? nativeSessionId,
        WorkItemState resumeState,
        AgentTurnResumePhase phase,
        int? iteration,
        int promptRevision,
        DateTimeOffset createdAt,
        int attemptCount = 0,
        Guid? dispatchClaimId = null,
        AgentTurnDispatchClaimStage? dispatchClaimStage = null)
    {
        ValidateToken(agent.Value, MaximumAgentKindLength, nameof(agent));
        ValidateToken(agentInstanceRoute, MaximumAgentInstanceRouteLength, nameof(agentInstanceRoute));
        if (!string.Equals(
                AgentInstanceIds.KindFromRouteKey(agentInstanceRoute),
                agent.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Agent instance route must belong to the checkpoint agent.",
                nameof(agentInstanceRoute));
        }

        ValidateOptionalToken(modelId, MaximumModelIdLength, nameof(modelId));
        ValidateOptionalToken(reasoningMode, MaximumReasoningModeLength, nameof(reasoningMode));

        var expectedPhase = resumeState switch
        {
            WorkItemState.Working => AgentTurnResumePhase.Work,
            WorkItemState.Reworking => AgentTurnResumePhase.Rework,
            _ => throw new ArgumentOutOfRangeException(
                nameof(resumeState),
                resumeState,
                "Agent turns can resume only in Working or Reworking state."),
        };
        if (phase != expectedPhase)
        {
            throw new ArgumentException(
                $"Resume phase {phase} is inconsistent with state {resumeState}.",
                nameof(phase));
        }

        if (iteration is <= 0)
            throw new ArgumentOutOfRangeException(nameof(iteration), iteration, "Iteration must be positive when supplied.");
        if (promptRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(promptRevision), promptRevision, "Prompt revision must be positive.");
        if (createdAt == default)
            throw new ArgumentOutOfRangeException(nameof(createdAt), createdAt, "Created-at must be populated.");
        if (attemptCount is < 0 or > MaximumAttemptCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attemptCount),
                attemptCount,
                $"Attempt count must be between 0 and {MaximumAttemptCount}.");
        }
        if (dispatchClaimId == Guid.Empty)
            throw new ArgumentException("Dispatch claim id must not be empty when supplied.", nameof(dispatchClaimId));
        if (dispatchClaimId is null && dispatchClaimStage is not null)
            throw new ArgumentException("A dispatch claim stage requires a dispatch claim id.", nameof(dispatchClaimStage));

        // Backward-read path: rows written before the claim-stage field existed
        // had already consumed their attempt when DispatchClaimId was set.
        AgentTurnDispatchClaimStage? effectiveClaimStage = dispatchClaimId is null
            ? null
            : dispatchClaimStage ?? AgentTurnDispatchClaimStage.Dispatched;
        if (effectiveClaimStage is { } claimStage && !Enum.IsDefined(claimStage))
            throw new ArgumentOutOfRangeException(nameof(dispatchClaimStage), claimStage, "Dispatch claim stage is invalid.");

        Agent = agent;
        AgentInstanceRoute = agentInstanceRoute;
        ModelId = modelId;
        ReasoningMode = reasoningMode;
        NativeSessionId = nativeSessionId;
        ResumeState = resumeState;
        Phase = phase;
        Iteration = iteration;
        PromptRevision = promptRevision;
        CreatedAt = createdAt;
        AttemptCount = attemptCount;
        DispatchClaimId = dispatchClaimId;
        DispatchClaimStage = effectiveClaimStage;
    }

    public AgentKind Agent { get; }
    public string AgentInstanceRoute { get; }
    public string? ModelId { get; }
    public string? ReasoningMode { get; }
    /// <summary>
    /// Exact provider-native session id when the runner emitted one. Null still
    /// represents a valid dirty-tree/scratchpad checkpoint for runners whose
    /// continuation mechanism discovers restored state itself.
    /// </summary>
    public AgentNativeSessionId? NativeSessionId { get; }
    public WorkItemState ResumeState { get; }
    public AgentTurnResumePhase Phase { get; }

    /// <summary>One-based rework iteration when known; null for work or unknown iterations.</summary>
    public int? Iteration { get; }

    /// <summary>Prompt generation used by the interrupted turn.</summary>
    public int PromptRevision { get; }

    /// <summary>Timestamp at which this durable agent-turn lineage was first captured.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Number of durable re-dispatches already made from this checkpoint lineage.</summary>
    public int AttemptCount { get; }

    /// <summary>
    /// Exclusive durable-dispatch claim. A non-null value means one worker has
    /// already consumed this checkpoint generation and may invoke the resumed
    /// CLI. Confirmed dead-worker recovery releases the claim before requeueing.
    /// </summary>
    public Guid? DispatchClaimId { get; }

    /// <summary>
    /// Preparation fences adoption of a mutable retained sandbox without
    /// consuming an attempt. Dispatched is the durable reservation made next
    /// to the resumed CLI invocation and counts the attempt optimistically. A
    /// typed pre-invocation preparation failure may refund it; a worker crash
    /// after reservation remains counted because the sink outcome is unknown.
    /// </summary>
    public AgentTurnDispatchClaimStage? DispatchClaimStage { get; }

    /// <summary>
    /// Returns whether a work-item lifecycle state may retain an interrupted
    /// work/rework turn. Terminal failures keep it only when they are
    /// infrastructure-shaped; successful and explicitly later phases discard it.
    /// </summary>
    public static bool CanPersistThrough(WorkItemState state, string? failureKind) =>
        state is WorkItemState.Working
            or WorkItemState.Reworking
            or WorkItemState.WaitingForQuotaReset
            or WorkItemState.WaitingForTransientRetry
            or WorkItemState.WaitingForAgentResume
            or WorkItemState.NeedsOperatorInput
            or WorkItemState.AbandonedAfterRecoveryAttempts
        || (state == WorkItemState.Failed && WorkItemFailureKinds.IsInfraShaped(failureKind));

    /// <summary>Returns the next durable retry generation without mutating this checkpoint.</summary>
    public AgentTurnResumeCheckpoint IncrementAttemptCount()
    {
        if (DispatchClaimId is not null)
        {
            throw new InvalidOperationException(
                "Release the active dispatch claim before advancing the resume attempt generation.");
        }
        if (AttemptCount >= MaximumAttemptCount)
        {
            throw new InvalidOperationException(
                $"Agent turn resume checkpoint reached its {MaximumAttemptCount}-attempt storage limit.");
        }

        return new AgentTurnResumeCheckpoint(
            Agent,
            AgentInstanceRoute,
            ModelId,
            ReasoningMode,
            NativeSessionId,
            ResumeState,
            Phase,
            Iteration,
            PromptRevision,
            CreatedAt,
            AttemptCount + 1);
    }

    /// <summary>Reserves one resumed CLI dispatch and consumes one attempt optimistically.</summary>
    public AgentTurnResumeCheckpoint ClaimDispatch(Guid claimId)
    {
        if (claimId == Guid.Empty)
            throw new ArgumentException("Dispatch claim id must not be empty.", nameof(claimId));
        if (DispatchClaimId is not null)
            throw new InvalidOperationException("Agent turn resume checkpoint is already claimed for dispatch.");
        if (AttemptCount >= MaximumAttemptCount)
        {
            throw new InvalidOperationException(
                $"Agent turn resume checkpoint reached its {MaximumAttemptCount}-attempt storage limit.");
        }

        return new AgentTurnResumeCheckpoint(
            Agent,
            AgentInstanceRoute,
            ModelId,
            ReasoningMode,
            NativeSessionId,
            ResumeState,
            Phase,
            Iteration,
            PromptRevision,
            CreatedAt,
            AttemptCount + 1,
            claimId,
            AgentTurnDispatchClaimStage.Dispatched);
    }

    /// <summary>Fences retained-sandbox adoption without consuming a CLI attempt.</summary>
    public AgentTurnResumeCheckpoint ClaimPreparation(Guid claimId)
    {
        if (claimId == Guid.Empty)
            throw new ArgumentException("Dispatch claim id must not be empty.", nameof(claimId));
        if (DispatchClaimId is not null)
            throw new InvalidOperationException("Agent turn resume checkpoint is already claimed.");

        return new AgentTurnResumeCheckpoint(
            Agent,
            AgentInstanceRoute,
            ModelId,
            ReasoningMode,
            NativeSessionId,
            ResumeState,
            Phase,
            Iteration,
            PromptRevision,
            CreatedAt,
            AttemptCount,
            claimId,
            AgentTurnDispatchClaimStage.Preparation);
    }

    /// <summary>Releases a claim after confirmed worker death without refunding its attempt.</summary>
    public AgentTurnResumeCheckpoint ReleaseDispatchClaim() => DispatchClaimId is null
        ? this
        : new AgentTurnResumeCheckpoint(
            Agent,
            AgentInstanceRoute,
            ModelId,
            ReasoningMode,
            NativeSessionId,
            ResumeState,
            Phase,
            Iteration,
            PromptRevision,
            CreatedAt,
            AttemptCount);

    /// <summary>
    /// Releases a claim and refunds its attempt only when orchestration has
    /// typed proof that resume preparation failed before the agent CLI sink.
    /// </summary>
    public AgentTurnResumeCheckpoint ReleaseUndispatchedClaim()
    {
        if (DispatchClaimId is null)
            throw new InvalidOperationException("An undispatched refund requires an active dispatch claim.");
        if (DispatchClaimStage != AgentTurnDispatchClaimStage.Dispatched)
            throw new InvalidOperationException("Only a dispatch-reserved attempt can be refunded.");
        if (AttemptCount <= 0)
            throw new InvalidOperationException("A claimed dispatch must have consumed one attempt.");

        return new AgentTurnResumeCheckpoint(
            Agent,
            AgentInstanceRoute,
            ModelId,
            ReasoningMode,
            NativeSessionId,
            ResumeState,
            Phase,
            Iteration,
            PromptRevision,
            CreatedAt,
            AttemptCount - 1);
    }

    private static void ValidateOptionalToken(string? value, int maximumLength, string parameterName)
    {
        if (value is null)
            return;
        ValidateToken(value, maximumLength, parameterName);
    }

    private static void ValidateToken(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException("Value must be non-empty.", parameterName);
        if (value.Length > maximumLength)
            throw new ArgumentException($"Value must be at most {maximumLength} characters.", parameterName);
        if (value.StartsWith('-'))
            throw new ArgumentException("Value must not start with '-'.", parameterName);
        if (value.Any(static character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new ArgumentException("Value must not contain whitespace or control characters.", parameterName);
    }
}

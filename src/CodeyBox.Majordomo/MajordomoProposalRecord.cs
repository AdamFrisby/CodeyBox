using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// One persisted majordomo proposal: the exact tool call the assistant made
/// (tool plus typed arguments), the change set the operator reviewed, the
/// reasoning the majordomo gave, who proposed it and when, and the current
/// lifecycle state. The record is immutable — state changes produce a new
/// record via <c>with</c> and persist through
/// <see cref="IMajordomoProposalStore.TryTransitionAsync"/>, which applies
/// the update only while the stored row is still in the expected state, so a
/// claim, revert, or settle can never overwrite a decision that raced it.
/// </summary>
public sealed record MajordomoProposalRecord
{
    /// <summary>Maximum stored reasoning length; mirrors the question-text cap.</summary>
    public const int MaxReasoningLength = 4000;

    /// <summary>Unique proposal id (GUID, N format).</summary>
    public required string Id { get; init; }

    /// <summary>Canonical MUTATE tool name as resolved by <see cref="MajordomoTools.TryGet"/>.</summary>
    public required string ToolName { get; init; }

    /// <summary>The exact typed arguments approval replays.</summary>
    public required MajordomoMutateArgs Arguments { get; init; }

    /// <summary>The reasoning the majordomo gave for the call; null when it gave none.</summary>
    public string? Reasoning { get; init; }

    /// <summary>
    /// The dry-run change set shown to the operator at proposal time.
    /// Approval re-plans against live queue state and refuses when the live
    /// plan no longer matches this one — the committed mutation can never
    /// exceed what was reviewed. Null only on rows persisted before the plan
    /// was recorded; those approve without the drift check.
    /// </summary>
    public MajordomoChangeSet? ReviewedChangeSet { get; init; }

    /// <summary>Identity that proposed (the majordomo API-client name).</summary>
    public required string ProposedBy { get; init; }

    /// <summary>When the proposal was persisted.</summary>
    public required DateTimeOffset ProposedAt { get; init; }

    /// <summary>Current lifecycle state.</summary>
    public MajordomoProposalState State { get; init; } = MajordomoProposalState.Pending;

    /// <summary>When the terminal state was recorded; null while pending.</summary>
    public DateTimeOffset? DecidedAt { get; init; }

    /// <summary>Who recorded the terminal state; null while pending.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>Why the terminal state was recorded; null while pending.</summary>
    public string? DecisionReason { get; init; }

    /// <summary>
    /// Work items the approval committed; set only on <see cref="MajordomoProposalState.Approved"/>.
    /// A repeated approval replays these ids without touching the queue again,
    /// which is what makes approval idempotent.
    /// </summary>
    public IReadOnlyList<WorkItemId>? ResultAffectedItems { get; init; }

    /// <summary>Mints a proposal id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Creates a pending proposal, validating the tool/argument pairing the
    /// same way <see cref="MajordomoAuthorization"/> does: the tool must be a
    /// known MUTATE tool and the arguments must be exactly its contract type.
    /// </summary>
    public static MajordomoProposalRecord Create(
        string toolName,
        MajordomoMutateArgs arguments,
        string proposedBy,
        DateTimeOffset proposedAt,
        string? reasoning = null,
        MajordomoChangeSet? reviewedChangeSet = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!MajordomoTools.TryGet(toolName, out var tool) || tool.Class != MajordomoToolClass.Mutate)
            throw new ArgumentException($"'{toolName}' is not a known MUTATE tool", nameof(toolName));
        if (arguments.GetType() != tool.ArgumentsType)
            throw new ArgumentException(
                $"tool '{tool.Name}' requires arguments of type {tool.ArgumentsType.Name}", nameof(arguments));
        if (string.IsNullOrWhiteSpace(proposedBy))
            throw new ArgumentException("proposedBy is required", nameof(proposedBy));

        return new MajordomoProposalRecord
        {
            Id = NewId(),
            ToolName = tool.Name,
            Arguments = arguments,
            Reasoning = NormalizeReasoning(reasoning ?? arguments.Reasoning, nameof(reasoning)),
            ReviewedChangeSet = reviewedChangeSet,
            ProposedBy = proposedBy,
            ProposedAt = proposedAt,
            State = MajordomoProposalState.Pending,
        };
    }

    /// <summary>
    /// True once the proposal is in a terminal state (approved, rejected,
    /// expired, superseded). <see cref="MajordomoProposalState.Applying"/> is
    /// not decided: the commit was claimed but no outcome is recorded yet.
    /// </summary>
    public bool IsDecided => State is not MajordomoProposalState.Pending
        and not MajordomoProposalState.Applying;

    /// <summary>True when <paramref name="now"/> is past the expiry deadline for this proposal.</summary>
    public bool IsExpiredAt(DateTimeOffset now, TimeSpan timeToLive) => ProposedAt + timeToLive <= now;

    internal static string? NormalizeReasoning(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (trimmed.Length > MaxReasoningLength)
            throw new ArgumentException(
                $"reasoning must be <= {MaxReasoningLength} chars", paramName);
        // Reasoning is rendered to operators, so terminal escapes must not
        // ride it into a log or dashboard. Newlines and tabs are legitimate
        // prose; every other control character is refused.
        if (trimmed.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new ArgumentException("reasoning must not contain control characters", paramName);
        return trimmed;
    }
}

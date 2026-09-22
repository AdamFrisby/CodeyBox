namespace CodeyBox.Majordomo;

/// <summary>Why <see cref="MajordomoAuthorization"/> refused a call.</summary>
public enum MajordomoRefusalReason
{
    /// <summary>The name is not in the vocabulary — default-deny.</summary>
    UnknownTool,

    /// <summary>The argument payload's type is not the named tool's contract.</summary>
    ArgumentContractMismatch,

    /// <summary>The call alone would mutate more items than the per-turn cap allows.</summary>
    TooManyItemsInOneCall,

    /// <summary>The call would push the turn's cumulative mutations past the cap.</summary>
    TurnMutationBudgetExhausted,
}

/// <summary>
/// The mutation a MUTATE call would perform, packaged for operator review in
/// <see cref="MajordomoAutonomyMode.Proposed"/> mode.
/// </summary>
/// <param name="Tool">The resolved tool.</param>
/// <param name="Arguments">The typed arguments the call was made with.</param>
public sealed record MajordomoProposal(MajordomoTool Tool, MajordomoMutateArgs Arguments)
{
    /// <summary>How many work items the proposed call would mutate.</summary>
    public int AffectedItemCount => Arguments.AffectedItemCount;
}

/// <summary>
/// Outcome of the majordomo authorization decision — the only way a tool call
/// is gated. Closed union: <see cref="Execute"/>, <see cref="Propose"/>, or
/// <see cref="Refuse"/>.
/// </summary>
public abstract record MajordomoDecision
{
    private MajordomoDecision() { }

    /// <summary>
    /// The call may run now: every READ, every dry-run, and mutations under
    /// <see cref="MajordomoAutonomyMode.Autonomous"/> within budget.
    /// </summary>
    public sealed record Execute(MajordomoTool Tool) : MajordomoDecision;

    /// <summary>
    /// The call must not run; it is packaged as a proposal for the operator
    /// to approve or reject. Produced only for non-dry-run mutations under
    /// <see cref="MajordomoAutonomyMode.Proposed"/> within budget.
    /// </summary>
    public sealed record Propose(MajordomoProposal Proposal) : MajordomoDecision;

    /// <summary>The call is denied; <paramref name="Reason"/> is machine-readable.</summary>
    public sealed record Refuse(
        MajordomoRefusalReason Reason,
        string Detail) : MajordomoDecision;
}

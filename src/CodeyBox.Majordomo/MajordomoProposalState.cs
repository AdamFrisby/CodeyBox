namespace CodeyBox.Majordomo;

/// <summary>
/// Lifecycle state of a persisted majordomo proposal. A proposal is born
/// <see cref="Pending"/> and leaves it exactly once — every terminal state
/// is recorded with when, by whom, and why.
/// </summary>
public enum MajordomoProposalState
{
    /// <summary>Awaiting an operator decision; the only state approval can run from.</summary>
    Pending,

    /// <summary>Approved and committed through the mutate backend.</summary>
    Approved,

    /// <summary>Rejected by the operator; nothing was mutated.</summary>
    Rejected,

    /// <summary>Passed its time-to-live before approval; approval is refused.</summary>
    Expired,

    /// <summary>Withdrawn because a newer proposal supersedes it; nothing was mutated.</summary>
    Superseded,
}

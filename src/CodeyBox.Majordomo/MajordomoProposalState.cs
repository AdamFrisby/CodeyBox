namespace CodeyBox.Majordomo;

/// <summary>
/// Lifecycle state of a persisted majordomo proposal. A proposal is born
/// <see cref="Pending"/>; an approval claims it into <see cref="Applying"/>
/// and then settles it into a terminal state — every terminal state is
/// recorded with when, by whom, and why. A claim whose commit is refused
/// before any write reverts the row to Pending, so Pending→Applying can
/// repeat until a decision sticks or the row expires.
/// </summary>
public enum MajordomoProposalState
{
    /// <summary>Awaiting an operator decision; the only state a fresh approval can claim.</summary>
    Pending,

    /// <summary>
    /// An approval claimed the proposal and its commit is in flight — or a
    /// crash interrupted the commit before the outcome was recorded.
    /// Re-approval is refused so an interrupted commit can never be applied
    /// twice; the operator inspects the queue and rejects or supersedes the
    /// proposal to close it out.
    /// </summary>
    Applying,

    /// <summary>Approved and committed through the mutate backend.</summary>
    Approved,

    /// <summary>Rejected by the operator. See the record's decision reason — a proposal closed from <see cref="Applying"/> may already have applied its mutation.</summary>
    Rejected,

    /// <summary>Passed its time-to-live before approval; approval is refused.</summary>
    Expired,

    /// <summary>Withdrawn because a newer proposal supersedes it. See <see cref="Rejected"/> for the interrupted-commit caveat.</summary>
    Superseded,
}

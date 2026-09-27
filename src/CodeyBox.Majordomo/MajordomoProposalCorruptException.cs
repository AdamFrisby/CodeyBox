using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Thrown when a persisted proposal row cannot be rebound to its typed
/// argument contract — the vocabulary changed under a queued proposal, or
/// the row was tampered with. Approval of such a proposal is refused with a
/// reason rather than applied blindly; the row itself is left for inspection.
/// </summary>
public sealed class MajordomoProposalCorruptException(string proposalId, string detail)
    : InvalidOperationException(
        $"proposal '{Validation.DescribeUntrustedValue(proposalId)}' cannot be read back: {detail}")
{
    public string ProposalId { get; } = proposalId;
}

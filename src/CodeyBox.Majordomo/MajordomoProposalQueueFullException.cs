namespace CodeyBox.Majordomo;

/// <summary>
/// Thrown by <see cref="IMajordomoProposalStore.EnqueueAsync"/> when the
/// queue already holds the configured maximum of undecided proposals — the
/// insert is refused atomically rather than letting a proposer grow the
/// shared state database without bound.
/// </summary>
public sealed class MajordomoProposalQueueFullException(int maxPending)
    : InvalidOperationException(
        $"the majordomo proposal queue is full — {maxPending} proposals are awaiting a decision")
{
    /// <summary>The configured cap that was reached.</summary>
    public int MaxPending { get; } = maxPending;
}

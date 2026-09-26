namespace CodeyBox.Majordomo;

/// <summary>
/// Durable store for majordomo proposals. Implementations must survive
/// orchestrator restarts (the SQLite implementation shares the state
/// database file); the queue of operator suggestions must not evaporate
/// with the process.
/// </summary>
public interface IMajordomoProposalStore
{
    /// <summary>Largest accepted page size for <see cref="ListAsync"/>.</summary>
    public const int MaxListLimit = 500;

    /// <summary>Persists a new pending proposal. Ids are unique; a duplicate id throws.</summary>
    Task EnqueueAsync(MajordomoProposalRecord proposal, CancellationToken ct = default);

    /// <summary>Loads one proposal by id; null when unknown.</summary>
    Task<MajordomoProposalRecord?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Lists proposals newest-first, optionally filtered to one state,
    /// capped at <paramref name="limit"/> rows (1..<see cref="MaxListLimit"/>).
    /// </summary>
    Task<IReadOnlyList<MajordomoProposalRecord>> ListAsync(
        MajordomoProposalState? state = null,
        int limit = MaxListLimit,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically moves a proposal out of <paramref name="expectedCurrent"/>
    /// to the state carried by <paramref name="decided"/> (which must share
    /// the same id). Returns false when the id is unknown or the stored
    /// state is no longer <paramref name="expectedCurrent"/> — the caller
    /// must then re-read and report the actual state rather than mutate.
    /// </summary>
    Task<bool> TryTransitionAsync(
        string id,
        MajordomoProposalState expectedCurrent,
        MajordomoProposalRecord decided,
        CancellationToken ct = default);
}

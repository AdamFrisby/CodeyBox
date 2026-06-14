namespace CodeyBox.Orchestrator;

/// <summary>
/// Reads the persisted signals the <see cref="TransitionHealthClassifier"/>
/// scores: finalized agent involvement rows, audit reports, and terminal
/// failed work items. The source is read-only and consults the same SQLite
/// database as the orchestrator's other stores; no new tables are introduced.
/// </summary>
public interface ITransitionHealthDataSource
{
    /// <summary>
    /// Snapshot all classifier inputs ended within
    /// <paramref name="windowStart"/>..<paramref name="windowEnd"/>. The
    /// classifier filters again on the returned window (a small over-fetch is
    /// fine), so implementations may use cheap range predicates that match the
    /// existing indexes.
    /// </summary>
    Task<TransitionDataSnapshot> LoadAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int maxRowsPerSource,
        CancellationToken ct = default);
}

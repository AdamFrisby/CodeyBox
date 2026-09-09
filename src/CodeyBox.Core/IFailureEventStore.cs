namespace CodeyBox.Core;

/// <summary>
/// Durable, append-only log of work-item failure/park transitions. Separate
/// from the mutable failure fields on <see cref="WorkItem"/> (which a retry
/// overwrites), so failure-rate and failure-mode questions can be answered
/// after the fact. Writes are best-effort at the call site: the caller wraps
/// with try/catch and logs on failure rather than breaking the state
/// transition being recorded.
/// </summary>
public interface IFailureEventStore
{
    /// <summary>Appends one failure event. The store bounds error length before insert.</summary>
    Task AppendAsync(FailureEventRecord record, CancellationToken ct = default);

    /// <summary>
    /// Returns failure events ordered by <see cref="FailureEventRecord.OccurredAt"/>
    /// descending, optionally filtered to events at or after <paramref name="since"/>
    /// (UTC) and/or matching <paramref name="kind"/> exactly.
    /// <paramref name="limit"/> bounds the row count.
    /// </summary>
    Task<IReadOnlyList<FailureEventRecord>> QueryAsync(
        DateTimeOffset? since,
        string? kind,
        int limit,
        CancellationToken ct = default);
}

using System.Collections.Concurrent;

namespace CodeyBox.Majordomo;

/// <summary>
/// In-process <see cref="IMajordomoProposalStore"/> for tests and for hosts
/// that opt out of SQLite persistence. Thread-safe; transitions are atomic
/// compare-and-set operations so concurrent approvers cannot double-apply.
/// </summary>
public sealed class InMemoryMajordomoProposalStore : IMajordomoProposalStore
{
    private readonly ConcurrentDictionary<string, MajordomoProposalRecord> _records =
        new(StringComparer.Ordinal);

    public Task EnqueueAsync(MajordomoProposalRecord proposal, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (!_records.TryAdd(proposal.Id, proposal))
            throw new InvalidOperationException($"proposal '{proposal.Id}' is already queued");
        return Task.CompletedTask;
    }

    public Task<MajordomoProposalRecord?> GetAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_records.TryGetValue(id, out var record) ? record : null);

    public Task<IReadOnlyList<MajordomoProposalRecord>> ListAsync(
        MajordomoProposalState? state = null,
        int limit = IMajordomoProposalStore.MaxListLimit,
        CancellationToken ct = default)
    {
        if (limit is < 1 or > IMajordomoProposalStore.MaxListLimit)
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit,
                $"limit must be within [1, {IMajordomoProposalStore.MaxListLimit}]");
        IReadOnlyList<MajordomoProposalRecord> rows = _records.Values
            .Where(r => state is null || r.State == state)
            .OrderByDescending(r => r.ProposedAt)
            .ThenBy(r => r.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<bool> TryTransitionAsync(
        string id,
        MajordomoProposalState expectedCurrent,
        MajordomoProposalRecord decided,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decided);
        if (!string.Equals(id, decided.Id, StringComparison.Ordinal))
            throw new ArgumentException("decided record id must match the transition id", nameof(decided));

        while (_records.TryGetValue(id, out var current))
        {
            if (current.State != expectedCurrent)
                return Task.FromResult(false);
            if (_records.TryUpdate(id, decided, current))
                return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}

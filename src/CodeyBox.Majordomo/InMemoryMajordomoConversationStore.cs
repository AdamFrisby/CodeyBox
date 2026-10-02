namespace CodeyBox.Majordomo;

/// <summary>
/// In-process <see cref="IMajordomoConversationStore"/> for tests and for
/// hosts that opt out of SQLite persistence. Thread-safe; each append and
/// its conditional compaction hold one lock, so concurrent writers cannot
/// interleave sequences or lose the summary update. Does not survive a
/// restart — production hosts use the SQLite implementation.
/// </summary>
public sealed class InMemoryMajordomoConversationStore : IMajordomoConversationStore
{
    private readonly List<MajordomoConversationEntry> _entries = [];
    private MajordomoConversationSummary _summary = MajordomoConversationSummary.None;
    private long _nextSequence;
    private readonly object _lock = new();

    public Task<MajordomoConversationEntry> AppendAsync(
        MajordomoConversationRole role,
        string text,
        string? toolName,
        DateTimeOffset recordedAt,
        MajordomoHistoryOptions policy,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(policy);
        var invalid = MajordomoHistoryOptions.Validate(policy);
        if (invalid is not null)
            throw new ArgumentException($"Invalid conversation policy: {invalid}", nameof(policy));

        lock (_lock)
        {
            var stored = MajordomoConversationLimits.TruncateForStorage(text, policy.MaxEntryChars);
            var entry = new MajordomoConversationEntry(role, stored, recordedAt, ++_nextSequence, toolName);
            _entries.Add(entry);

            if (_entries.Count > policy.MaxEntries)
            {
                var excess = _entries.Count - policy.MaxEntries;
                var covered = _entries.Take(excess).ToList();
                _summary = MajordomoHistorySummarizer.Combine(_summary, covered, recordedAt);
                _entries.RemoveRange(0, excess);
            }

            return Task.FromResult(entry);
        }
    }

    public Task<IReadOnlyList<MajordomoConversationEntry>> ListAsync(
        long afterSequence = 0,
        int limit = IMajordomoConversationStore.MaxListLimit,
        CancellationToken ct = default)
    {
        if (afterSequence < 0)
            throw new ArgumentOutOfRangeException(nameof(afterSequence), afterSequence, "afterSequence must be >= 0");
        if (limit is < 1 or > IMajordomoConversationStore.MaxListLimit)
            throw new ArgumentOutOfRangeException(
                nameof(limit), limit,
                $"limit must be within [1, {IMajordomoConversationStore.MaxListLimit}]");

        lock (_lock)
        {
            IReadOnlyList<MajordomoConversationEntry> rows = _entries
                .Where(e => e.Sequence > afterSequence)
                .Take(limit)
                .ToList();
            return Task.FromResult(rows);
        }
    }

    public Task<long> CountAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult((long)_entries.Count);
    }

    public Task<MajordomoConversationSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(_summary);
    }
}

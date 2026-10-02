namespace CodeyBox.Majordomo;

/// <summary>
/// Durable storage for the majordomo conversation. Production
/// implementations survive orchestrator restarts and sandbox recreations
/// (the SQLite implementation shares the state database file); volatile
/// implementations such as <see cref="InMemoryMajordomoConversationStore"/>
/// serve tests and hosts that explicitly opt out of persistence.
/// </summary>
public interface IMajordomoConversationStore
{
    /// <summary>Largest accepted page size for <see cref="ListAsync"/>.</summary>
    public const int MaxListLimit = 500;

    /// <summary>
    /// Appends one row and returns it with its assigned sequence. Text
    /// longer than <paramref name="policy"/>.<see cref="MajordomoHistoryOptions.MaxEntryChars"/>
    /// is truncated with a marker before it is persisted, so one huge tool
    /// result cannot grow the state database at request rate. When the append
    /// pushes the row count past <paramref name="policy"/>.<see cref="MajordomoHistoryOptions.MaxEntries"/>,
    /// the oldest excess is compacted into the stored summary inside the same
    /// write — summarised through <see cref="MajordomoHistorySummarizer"/>,
    /// never dropped.
    /// </summary>
    Task<MajordomoConversationEntry> AppendAsync(
        MajordomoConversationRole role,
        string text,
        string? toolName,
        DateTimeOffset recordedAt,
        MajordomoHistoryOptions policy,
        CancellationToken ct = default);

    /// <summary>
    /// Reads rows with a sequence above <paramref name="afterSequence"/>,
    /// oldest-first, capped at <paramref name="limit"/> rows
    /// (1..<see cref="MaxListLimit"/>).
    /// </summary>
    Task<IReadOnlyList<MajordomoConversationEntry>> ListAsync(
        long afterSequence = 0,
        int limit = MaxListLimit,
        CancellationToken ct = default);

    /// <summary>The number of stored rows.</summary>
    Task<long> CountAsync(CancellationToken ct = default);

    /// <summary>The stored rollup, or <see cref="MajordomoConversationSummary.None"/> when absent.</summary>
    Task<MajordomoConversationSummary> GetSummaryAsync(CancellationToken ct = default);
}

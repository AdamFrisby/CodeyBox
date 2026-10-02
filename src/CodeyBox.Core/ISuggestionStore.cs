namespace CodeyBox.Core;

public interface ISuggestionStore
{
    /// <summary>
    /// Unconditional insert. Used by seeding and tests; production suggestion
    /// pickup calls <see cref="CreateOrMergeAsync"/> so agent-filed repeats
    /// converge on one canonical row.
    /// </summary>
    Task CreateAsync(Suggestion suggestion, CancellationToken ct = default);

    /// <summary>
    /// Atomically inserts <paramref name="suggestion"/>, or — when its
    /// normalized dedupe key or fuzzy signature matches an open suggestion or
    /// one dismissed within <paramref name="policy"/>'s window — merges into
    /// the existing row: <see cref="Suggestion.OccurrenceCount"/> is
    /// incremented and the new source work item id appended (bounded by
    /// <see cref="SuggestionDedupePolicy.MaxRecordedSourceIds"/>). A matched
    /// row keeps its state; merging a repeat of a dismissed suggestion leaves
    /// it dismissed.
    /// </summary>
    Task<SuggestionCreateOutcome> CreateOrMergeAsync(
        Suggestion suggestion,
        SuggestionDedupePolicy policy,
        CancellationToken ct = default);
    Task<Suggestion?> GetAsync(string id, CancellationToken ct = default);
    Task UpdateAsync(Suggestion suggestion, CancellationToken ct = default);

    /// <summary>
    /// Atomically transitions a suggestion from 'open' to 'accepted', recording the linked work item.
    /// Returns true if the update succeeded (suggestion was open), false if already accepted/dismissed.
    /// </summary>
    Task<bool> TryAcceptAsync(string id, string promotedToWorkItemId, CancellationToken ct = default);

    /// <summary>
    /// Atomically transitions a suggestion from 'open' to 'dismissed'.
    /// Returns true if the update succeeded (suggestion was open), false if already dismissed/accepted.
    /// </summary>
    Task<bool> TryDismissAsync(string id, string? dismissReason, CancellationToken ct = default);

    IAsyncEnumerable<Suggestion> ListAsync(
        string? projectId = null,
        string? category = null,
        string? severity = null,
        string? state = "open",
        int limit = 200,
        int offset = 0,
        CancellationToken ct = default);

    Task<int> CountAsync(
        string? projectId = null,
        string? category = null,
        string? severity = null,
        string? state = "open",
        CancellationToken ct = default);

    Task<int> CountOpenAsync(CancellationToken ct = default);
}

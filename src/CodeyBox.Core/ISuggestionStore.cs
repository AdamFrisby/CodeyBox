namespace CodeyBox.Core;

public interface ISuggestionStore
{
    Task CreateAsync(Suggestion suggestion, CancellationToken ct = default);
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

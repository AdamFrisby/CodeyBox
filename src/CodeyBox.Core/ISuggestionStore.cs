namespace CodeyBox.Core;

public interface ISuggestionStore
{
    Task CreateAsync(Suggestion suggestion, CancellationToken ct = default);
    Task<Suggestion?> GetAsync(string id, CancellationToken ct = default);
    Task UpdateAsync(Suggestion suggestion, CancellationToken ct = default);
    IAsyncEnumerable<Suggestion> ListAsync(
        string? projectId = null,
        string? category = null,
        string? severity = null,
        string? state = "open",
        CancellationToken ct = default);
    Task<int> CountOpenAsync(CancellationToken ct = default);
}

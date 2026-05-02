namespace CodeyBox.Core;

public interface IChangelogGenerator
{
    Task<ChangelogEntry> GenerateAsync(ChangelogRequest request, CancellationToken ct);
}

public sealed record ChangelogRequest
{
    public required ProjectId ProjectId { get; init; }
    public required string FromTag { get; init; }
    public required string ToTag { get; init; }
    public required IReadOnlyList<MergedPullRequest> PullRequests { get; init; }
}

public sealed record MergedPullRequest(
    int Number,
    string Title,
    string Body,
    string MergedAt,
    IReadOnlyList<string> AuthorTrailers,
    IReadOnlyList<string> ChangedFiles);

public sealed record ChangelogEntry
{
    public required string ToTag { get; init; }
    public required string Markdown { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<int>> CategoryToPrNumbers { get; init; }
}

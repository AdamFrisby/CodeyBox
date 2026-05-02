namespace CodeyBox.Core;

public interface IPullRequestEnumerator
{
    Task<PullRequestEnumeratorResult> ListMergedBetweenAsync(
        string owner,
        string repo,
        string token,
        string fromTag,
        string toTag,
        CancellationToken ct);

    Task<string?> ResolvePreviousTagAsync(
        string owner,
        string repo,
        string token,
        string currentTag,
        CancellationToken ct);
}

public sealed record PullRequestEnumeratorResult(
    IReadOnlyList<MergedPullRequest> PullRequests,
    bool WasCapped);

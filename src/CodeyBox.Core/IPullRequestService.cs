namespace CodeyBox.Core;

/// <summary>
/// Tracks "pull request" metadata. Default implementation is local: PRs are
/// records associated with branches in a host bare repo. Other implementations
/// could front Gitea, Forgejo, or GitHub. The actual merge of branches happens
/// in the merge-phase sandbox (orchestrated by the orchestrator); this service
/// tracks status only — never holds upstream credentials and never talks to a
/// remote forge from the data plane.
/// </summary>
public interface IPullRequestService
{
    Task<PullRequest> OpenAsync(OpenPullRequest request, CancellationToken ct = default);
    Task MarkMergedAsync(PullRequestId id, string mergeCommitSha, CancellationToken ct = default);
    Task MarkClosedAsync(PullRequestId id, string? reason, CancellationToken ct = default);
    Task<PullRequest?> GetAsync(PullRequestId id, CancellationToken ct = default);
}

public readonly record struct PullRequestId(string Value)
{
    public override string ToString() => Value;
}

public sealed record OpenPullRequest(
    string RepositoryId,
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Description);

public sealed record PullRequest(
    PullRequestId Id,
    string RepositoryId,
    string SourceBranch,
    string TargetBranch,
    string Title,
    string Description,
    PullRequestStatus Status,
    string? MergeCommitSha);

public enum PullRequestStatus { Open, Merged, Closed }

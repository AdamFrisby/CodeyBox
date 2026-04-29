namespace CodeyBox.Core;

/// <summary>
/// Replication target for the host bare repo, run AFTER a successful local
/// merge. This is the only component that holds upstream credentials (e.g.
/// a GitHub PAT). Sandboxes never see it. If push fails, the local repo
/// remains the source of truth and the orchestrator retries.
/// </summary>
public interface IUpstreamRemote
{
    /// <summary>Stable identifier for diagnostics ("noop", "github", "git-generic").</summary>
    string Name { get; }

    /// <summary>
    /// Pushes the named ref from the host bare repo to the upstream. The
    /// repository identifier is opaque and must be understood by the host
    /// git module that materialises it.
    /// </summary>
    Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default);

    /// <summary>
    /// Completes a work item's upstream lifecycle after a successful local merge.
    /// Implementations decide what "complete" means for their forge type: a bare
    /// push, opening a pull request, or opening and auto-merging one. Throws on
    /// transient failures so the orchestrator can retry; returns a partial outcome
    /// for graceful soft-failures (e.g. PR already exists).
    /// </summary>
    Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default);
}

public sealed record UpstreamPushResult(bool Success, string? Error);

/// <summary>Input to <see cref="IUpstreamRemote.CompleteAsync"/>.</summary>
public sealed record UpstreamCompletionRequest
{
    public required string RepositoryId { get; init; }
    public required WorkItemId WorkItemId { get; init; }
    public required ProjectId ProjectId { get; init; }
    public required string WorkBranch { get; init; }
    public required string BaseBranch { get; init; }
    /// <summary>SHA produced by the local merge. Null when resuming past the merge phase.</summary>
    public string? MergeSha { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
}

/// <summary>Result of <see cref="IUpstreamRemote.CompleteAsync"/>.</summary>
public sealed record UpstreamCompletionOutcome
{
    /// <summary>True when the upstream kind has no push concept (noop).</summary>
    public bool Skipped { get; init; }
    /// <summary>True when a branch was successfully pushed to the remote.</summary>
    public bool BranchPushed { get; init; }
    /// <summary>URL of the pull request opened on the remote forge, if any.</summary>
    public string? PullRequestUrl { get; init; }
    /// <summary>Forge-assigned PR number, if a PR was opened.</summary>
    public int? PullRequestNumber { get; init; }
    /// <summary>SHA of the merge commit on the remote, if the PR was auto-merged.</summary>
    public string? MergedSha { get; init; }
    /// <summary>Diagnostic notes, populated on partial or graceful-degraded outcomes.</summary>
    public string? Notes { get; init; }
}

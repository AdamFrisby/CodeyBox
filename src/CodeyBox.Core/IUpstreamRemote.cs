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

    /// <summary>
    /// Attempts to merge <paramref name="sourceBranch"/> into <paramref name="targetBranch"/>
    /// on the upstream (e.g. GitHub Merges API, or host-side git merge+push for generic git).
    /// Returns <c>true</c> when the merge succeeded or the target was already up-to-date.
    /// Returns <c>false</c> when a merge conflict is detected; the caller should emit a
    /// <c>release.sync_conflict</c> event and leave the conflict for a human to resolve.
    /// Throws on unexpected infrastructure failures (network error, auth failure, etc.).
    /// </summary>
    Task<bool> TryMergeUpstreamBranchAsync(string targetBranch, string sourceBranch, CancellationToken ct = default);

    /// <summary>
    /// Creates a tag at <paramref name="sha"/> and publishes a release named
    /// <paramref name="tagName"/> on the upstream forge. Returns the URL of the
    /// created release, or <c>null</c> when the upstream kind does not support
    /// forge releases (e.g. noop, git-generic). Never throws on unsupported — the
    /// caller logs and continues; a missing GitHub release is not a hard failure.
    /// </summary>
    Task<string?> CreateTagAndReleaseAsync(string tagName, string sha, string? releaseNotes, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Fetches the current head of <paramref name="baseBranch"/> from the
    /// upstream forge into the host bare repo, overwriting the local ref, and
    /// returns the new commit sha. Used by the auto-merge race recovery flow
    /// in the orchestrator when GitHub returns 405 on the merge call: we
    /// refetch the upstream main to detect whether the race is real (base
    /// moved → re-run merge phase) or a different kind of unmergeability
    /// (base unchanged → branch protection or some other issue we can't fix
    /// by retrying).
    ///
    /// Default returns <c>null</c> for upstream kinds that don't model a
    /// remote base branch (noop, git-generic without a configured base).
    /// </summary>
    Task<string?> FetchBaseBranchAsync(string repositoryId, string baseBranch, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    /// <summary>
    /// Lists open pull requests whose head branch starts with <paramref name="branchPrefix"/>
    /// and whose mergeability is known. Used by the stale-base PR sweeper to
    /// detect CodeyBox-authored PRs whose base branch has moved and produced a
    /// conflict the auto-merger can no longer resolve.
    ///
    /// <para>Implementations only need to return PRs whose mergeability has
    /// been computed by the forge (i.e. <c>mergeable</c> is not null on
    /// GitHub). PRs whose state is still being calculated are skipped so the
    /// sweeper reconsiders them on the next tick.</para>
    ///
    /// <para>Default returns an empty list for upstream kinds that don't
    /// model PRs (noop, git-generic).</para>
    /// </summary>
    Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(
        string branchPrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UpstreamPullRequest>>([]);
}

/// <summary>
/// Snapshot of an open pull request as seen by an <see cref="IUpstreamRemote"/>
/// at a point in time. Fields mirror the subset of the forge PR object the
/// stale-base sweeper needs: identity (number + URL), branch endpoints, head
/// sha, and a textual mergeability classification.
/// </summary>
public sealed record UpstreamPullRequest
{
    public required int Number { get; init; }
    public required string Url { get; init; }
    public required string HeadBranch { get; init; }
    public required string HeadSha { get; init; }
    public required string BaseBranch { get; init; }
    /// <summary>
    /// True when the forge reports the PR has conflicts that need a
    /// manual rebase (GitHub: <c>mergeable=false</c> or
    /// <c>mergeable_state=dirty</c>). False when the PR is mergeable or
    /// blocked for an unrelated reason (branch protection, awaiting review).
    /// </summary>
    public required bool HasMergeConflict { get; init; }
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
    /// <summary>Static fallback PR description. Used when LLM generation is disabled or fails.</summary>
    public string? Description { get; init; }
    /// <summary>git diff --stat output between base and work branches. Empty when unavailable.</summary>
    public string DiffStat { get; init; } = string.Empty;
    /// <summary>Full git diff between base and work branches. Empty when unavailable.</summary>
    public string FullDiff { get; init; } = string.Empty;
    /// <summary>Titles of audit findings the agent addressed across rework iterations.</summary>
    public IReadOnlyList<string> AddressedFindings { get; init; } = [];
    /// <summary>Original work item prompt, truncated to 2 KB. Null for legacy callers.</summary>
    public string? WorkItemPrompt { get; init; }
    /// <summary>Raw agent stdout. Used for AgentReasoningTail in LLM-generated descriptions. Null for legacy callers.</summary>
    public string? AgentStdout { get; init; }
    /// <summary>
    /// Name of the environment variable holding the upstream credential (from
    /// <c>Upstream.TokenEnvVar</c> in the project config). Null when not configured.
    /// Plugin implementations read: <c>Environment.GetEnvironmentVariable(TokenEnvVar)</c>.
    /// </summary>
    public string? TokenEnvVar { get; init; }
    /// <summary>When true, merge the PR immediately after opening it.</summary>
    public bool AutoMerge { get; init; }
    /// <summary>Merge strategy: "merge", "squash", or "rebase". Matches <c>Upstream.MergeMethod</c>.</summary>
    public string MergeMethod { get; init; } = "merge";

    /// <summary>
    /// PR number already opened on the forge from a prior CompleteAsync attempt.
    /// When set, the implementation skips PR creation and proceeds directly to
    /// the merge step using this PR number. Used by the orchestrator's
    /// auto-merge race recovery (re-fetch base + re-run merge phase + retry
    /// merge against the still-open PR).
    /// </summary>
    public int? ExistingPullRequestNumber { get; init; }
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
    /// <summary>
    /// True when auto-merge requested but the forge rejected the merge call
    /// with a "PR not mergeable" race (GitHub HTTP 405 on PUT /pulls/N/merge).
    /// The orchestrator treats this as a retryable race against upstream base
    /// motion: re-fetch base, re-run the merge phase, and retry the merge.
    /// </summary>
    public bool AutoMergeRaced { get; init; }
}

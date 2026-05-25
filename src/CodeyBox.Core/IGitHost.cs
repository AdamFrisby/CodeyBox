namespace CodeyBox.Core;

/// <summary>
/// Manages the host-side bare repositories that sandboxes clone from and push
/// to. Provides the URL/endpoint sandboxes use, plus host-side operations
/// the orchestrator needs (push to upstream, inspect refs).
/// </summary>
public interface IGitHost
{
    /// <summary>
    /// Ensures a host-side bare repo exists for the given work item, seeded
    /// from <paramref name="seedFromUrl"/> if provided (typically the
    /// configured upstream). Returns a stable repository id.
    /// </summary>
    Task<string> EnsureRepositoryAsync(WorkItemId id, string? seedFromUrl, CancellationToken ct = default);

    /// <summary>
    /// Ensures a host-side bare repo exists and, when already present, refreshes
    /// the configured base branch from <paramref name="seedFromUrl"/> without
    /// overwriting work-item branches. If no base branch is supplied, the
    /// upstream's advertised default branch is refreshed.
    /// </summary>
    Task<string> EnsureRepositoryAsync(
        WorkItemId id,
        string? seedFromUrl,
        string? baseBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Describes how a sandbox should be wired up to reach this repository.
    /// Encapsulates whichever transport the host has chosen (path bind-mount,
    /// git-daemon over network, etc.) so callers stay provider-agnostic.
    /// </summary>
    SandboxRepositoryAccess GetSandboxAccess(string repositoryId);

    /// <summary>Returns the host filesystem path for a managed bare repository.</summary>
    string GetRepoPath(string repositoryId)
        => throw new NotSupportedException("This git host does not expose a host repository path.");

    /// <summary>Returns the resolved default branch name for the repo.</summary>
    Task<string> GetDefaultBranchAsync(string repositoryId, CancellationToken ct = default);

    /// <summary>Pushes a branch from the host bare repo to a configured upstream URL.</summary>
    Task PushToUpstreamAsync(
        string repositoryId,
        string upstreamUrl,
        string branch,
        IReadOnlyDictionary<string, string> upstreamEnv,
        UpstreamPushReconcileStrategy reconcileStrategy = UpstreamPushReconcileStrategy.Rebase,
        CancellationToken ct = default);

    /// <summary>Discards the host-side state for a finished work item.</summary>
    Task DisposeRepositoryAsync(string repositoryId, CancellationToken ct = default);

    /// <summary>
    /// Returns true if a host-side bare repo for the given work item id is
    /// present and usable. Used by the retry endpoint to validate that
    /// "resume from a later phase" is actually possible — the resumed
    /// phases need the prior phase's branch/merge state in the bare repo.
    /// </summary>
    Task<bool> RepositoryExistsAsync(WorkItemId id, CancellationToken ct = default);

    /// <summary>
    /// Returns true if <paramref name="branch"/> resolves to a commit in the
    /// host bare repo. Used by the retry endpoint to decide whether a
    /// post-work-phase resume (audit/merge/upstream) actually has the work
    /// branch the pipeline will try to check out — when the work phase died
    /// before producing a commit, the branch is absent and the next phase
    /// would fail with "pathspec did not match any file(s)".
    ///
    /// Default returns <c>true</c> ("don't know, assume yes") so test fakes
    /// that don't implement the check behave as before.
    /// </summary>
    Task<bool> BranchExistsAsync(string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(true);

    /// <summary>
    /// Returns true when <paramref name="workBranch"/> has at least one commit
    /// not reachable from <paramref name="baseBranch"/> in the host bare repo
    /// (equivalent to <c>git rev-list --count base..work &gt; 0</c>). Used by
    /// the retry endpoint to auto-pick a sensible resume phase: a work branch
    /// with prior commits should re-audit before discarding work, an empty
    /// work branch should re-run the work phase.
    ///
    /// Returns <c>false</c> when the repo or either branch is missing — the
    /// auto-pick falls through to "from=work" in that case, which is the
    /// pre-existing default and matches a fresh-clone retry. Never throws.
    ///
    /// Default returns <c>false</c> ("don't know, assume no commits ahead")
    /// so test fakes that don't implement the check behave as before.
    /// </summary>
    Task<bool> BranchHasCommitsAheadAsync(
        string repositoryId, string baseBranch, string workBranch, CancellationToken ct = default)
        => Task.FromResult(false);

    /// <summary>
    /// Returns <c>git diff --stat</c> and <c>git diff</c> output comparing
    /// <paramref name="baseBranch"/> and <paramref name="workBranch"/> in the
    /// host bare repo. Returns empty strings when the diff cannot be computed
    /// (e.g. repo not found, branch does not exist). Never throws.
    /// </summary>
    Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch,
        CancellationToken ct = default);

    /// <summary>
    /// Computes git's canonical host-side merge tree for two commits in the
    /// host bare repo. A non-zero git exit from content conflicts is returned
    /// as <see cref="GitMergeTreeResult.HasConflicts"/> rather than thrown.
    /// </summary>
    Task<GitMergeTreeResult> ComputeMergeTreeAsync(
        string repositoryId,
        string mainCommit,
        string workCommit,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side merge-tree verification.");

    /// <summary>Resolves a ref or commit expression in the host bare repo.</summary>
    Task<string> ResolveCommitAsync(string repositoryId, string commitish, CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side commit resolution.");

    /// <summary>
    /// Resets <paramref name="workBranch"/> in the host bare repo so it points
    /// at <paramref name="baseBranch"/>'s head, discarding any prior-attempt
    /// commits on the work branch. If the work branch does not exist, this is
    /// a no-op. The base branch must resolve to a commit; otherwise the call
    /// throws. Implementations must verify the post-reset tip and fail loudly
    /// if it does not equal the base head.
    ///
    /// Called from the retry-from-work flow so the agent's next invocation
    /// observes a pristine base state — the bug this guards against is a
    /// fail-quiet "Agent produced no changes to commit" outcome when the
    /// retried agent picks up its prior failed-attempt's commits and decides
    /// the work is already done.
    /// </summary>
    Task ResetWorkBranchToBaseAsync(
        string repositoryId,
        string workBranch,
        string baseBranch,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side work-branch reset.");

    /// <summary>Returns the tree object for a commit or tree-ish expression.</summary>
    Task<string> ResolveTreeAsync(string repositoryId, string treeish, CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side tree resolution.");

    /// <summary>Reads a text file from a commit or tree in the host bare repo.</summary>
    Task<string> ReadTextFileAsync(string repositoryId, string treeish, string path, CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side file reads.");

    /// <summary>Lists repository-relative files under a path prefix from a commit or tree.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(string repositoryId, string treeish, string pathPrefix, CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side file listing.");

    /// <summary>Returns name-status changes between two commits or trees in the host bare repo.</summary>
    Task<IReadOnlyList<GitChangedPath>> GetChangedPathsAsync(
        string repositoryId,
        string fromTreeish,
        string toTreeish,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side diff inspection.");

    /// <summary>Returns a zero-context diff for one path between two commits or trees.</summary>
    Task<string> GetUnifiedDiffAsync(
        string repositoryId,
        string fromTreeish,
        string toTreeish,
        string path,
        CancellationToken ct = default)
        => throw new NotSupportedException("This git host does not support host-side diff inspection.");
}

public sealed record GitMergeTreeResult(
    bool HasConflicts,
    string TreeSha,
    IReadOnlyList<string> ConflictedFiles,
    string RawOutput);

public sealed record GitChangedPath(
    string Status,
    string Path,
    string? OldPath = null);

/// <summary>
/// All the bits the orchestrator needs to fold into a <see cref="SandboxSpec"/>
/// so a sandbox can clone/push the repository: any mounts the sandbox should
/// receive, the network allowance for git traffic, and the URL the sandbox
/// should pass to "git clone" once those are in place.
/// </summary>
public sealed record SandboxRepositoryAccess(
    string CloneUrlInsideSandbox,
    IReadOnlyList<SandboxMount> Mounts,
    SandboxNetworkPolicy Network);

/// <summary>How a stale local branch should be reconciled after an upstream non-fast-forward rejection.</summary>
public enum UpstreamPushReconcileStrategy
{
    /// <summary>Replay local commits on top of the latest upstream tip before retrying the push.</summary>
    Rebase,

    /// <summary>Merge the latest upstream tip into the local branch before retrying the push.</summary>
    Merge,
}

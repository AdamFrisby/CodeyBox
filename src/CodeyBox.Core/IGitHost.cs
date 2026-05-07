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
    /// overwriting work-item branches.
    /// </summary>
    Task<string> EnsureRepositoryAsync(
        WorkItemId id,
        string? seedFromUrl,
        string? baseBranch,
        CancellationToken ct = default)
        => EnsureRepositoryAsync(id, seedFromUrl, ct);

    /// <summary>
    /// Describes how a sandbox should be wired up to reach this repository.
    /// Encapsulates whichever transport the host has chosen (path bind-mount,
    /// git-daemon over network, etc.) so callers stay provider-agnostic.
    /// </summary>
    SandboxRepositoryAccess GetSandboxAccess(string repositoryId);

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
    /// Returns <c>git diff --stat</c> and <c>git diff</c> output comparing
    /// <paramref name="baseBranch"/> and <paramref name="workBranch"/> in the
    /// host bare repo. Returns empty strings when the diff cannot be computed
    /// (e.g. repo not found, branch does not exist). Never throws.
    /// </summary>
    Task<(string DiffStat, string FullDiff)> GetDiffAsync(
        string repositoryId, string baseBranch, string workBranch,
        CancellationToken ct = default);
}

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

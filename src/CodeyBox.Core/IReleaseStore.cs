namespace CodeyBox.Core;

/// <summary>
/// Durable store for <see cref="Release"/> records. Implementations must be
/// thread-safe; the orchestrator may call any method from multiple concurrent workers.
/// </summary>
public interface IReleaseStore
{
    Task CreateAsync(Release release, CancellationToken ct = default);
    Task UpdateAsync(Release release, CancellationToken ct = default);
    Task<Release?> GetAsync(ReleaseId id, CancellationToken ct = default);
    Task<Release?> GetByNameAsync(ProjectId projectId, string name, CancellationToken ct = default);

    /// <summary>
    /// Lists releases, optionally filtered by project and/or state.
    /// Returns newest-first.
    /// </summary>
    Task<IReadOnlyList<Release>> ListAsync(
        ProjectId? projectId = null,
        ReleaseState? state = null,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically sets <c>branch_name</c> and <c>base_commit_sha</c> only when the current
    /// <c>branch_name</c> is NULL (SETIFNULL). Returns true when this call won the race
    /// and the values were written; false when another worker already set the branch.
    /// </summary>
    Task<bool> TrySetBranchAsync(ReleaseId id, string branchName, string baseCommitSha, CancellationToken ct = default);
}

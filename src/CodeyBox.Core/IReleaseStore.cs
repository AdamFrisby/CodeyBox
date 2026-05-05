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
    /// Returns newest-first. <paramref name="limit"/> caps the number of rows returned
    /// (max 1000); <paramref name="offset"/> skips the first N rows for cursor-based paging.
    /// </summary>
    Task<IReadOnlyList<Release>> ListAsync(
        ProjectId? projectId = null,
        ReleaseState? state = null,
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically sets <c>branch_name</c> and <c>base_commit_sha</c> only when the current
    /// <c>branch_name</c> is NULL (SETIFNULL). Returns true when this call won the race
    /// and the values were written; false when another worker already set the branch.
    /// </summary>
    Task<bool> TrySetBranchAsync(ReleaseId id, string branchName, string baseCommitSha, CancellationToken ct = default);

    /// <summary>
    /// Atomically transitions to the state in <paramref name="release"/> only when the
    /// current persisted state equals <paramref name="expectedCurrentState"/> (compare-and-swap).
    /// Returns true when the row was updated; false when the state did not match, indicating
    /// a concurrent transition already occurred.
    /// </summary>
    Task<bool> TryTransitionStateAsync(Release release, ReleaseState expectedCurrentState, CancellationToken ct = default);

    /// <summary>Persists one completed deep-audit iteration for the release timeline.</summary>
    Task SaveAuditIterationAsync(ReleaseAuditIteration iteration, CancellationToken ct = default);

    /// <summary>Returns all stored deep-audit iterations for a release, ordered by iteration number.</summary>
    Task<IReadOnlyList<ReleaseAuditIteration>> ListAuditIterationsAsync(ReleaseId releaseId, CancellationToken ct = default);
}

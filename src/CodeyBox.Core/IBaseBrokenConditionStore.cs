namespace CodeyBox.Core;

/// <summary>
/// How the base branch tip proved broken: a failing required build, or
/// failing tests on a tree that builds (a merged tree whose tests regress
/// even though every parent branch was green on its own base).
/// </summary>
public enum BaseBrokenFailureKind
{
    /// <summary>The base tip fails the required build.</summary>
    Build = 0,

    /// <summary>
    /// The base tip builds but its tests fail (forge CI red for the tip, or
    /// audit test failures that genuinely reproduce on the base). The
    /// dispatcher hold, fix-item dedupe, and monitor sweep treat both kinds
    /// identically; only the clear signal differs (build pass vs CI pass).
    /// </summary>
    Test = 1,
}

/// <summary>
/// A recorded "base branch is broken" condition for a project — either it
/// does not build, or it builds but its tests fail. Build-kind rows are
/// created when a required-build failure on a work item cannot be attributed
/// to the item's diff and the base branch tip reproduces the same failure;
/// test-kind rows are created when the post-merge CI watch reports the
/// landed tip red, or when an item's test-only audit failures all reproduce
/// on the base while the tip's CI is red. While a row is active
/// (<see cref="ClearedAt"/> is null) the dispatcher holds build-dependent
/// phases (work / audit / merge) for the project — except the auto-filed
/// fix item (<see cref="FixWorkItemId"/>), which must run to repair the
/// base. Rows are keyed by base tip SHA: a base that moved to a different
/// commit is a different condition.
/// </summary>
public sealed record BaseBrokenCondition
{
    public required ProjectId ProjectId { get; init; }

    /// <summary>The base branch whose tip proved broken.</summary>
    public required string BaseBranch { get; init; }

    /// <summary>The base branch tip SHA that reproduced the failure.</summary>
    public required string BaseSha { get; init; }

    /// <summary>
    /// Host bare-repo id used to re-resolve the base tip when sweeping.
    /// Equal to the fix item's work-item id when one exists (its repo is
    /// refreshed from upstream on each sweep), otherwise the id of the item
    /// that first observed the failure.
    /// </summary>
    public required string RepositoryId { get; init; }

    /// <summary>
    /// Bounded, sanitized summary of the failing evidence: base build output
    /// for <see cref="BaseBrokenFailureKind.Build"/>, failing test/check
    /// names plus suspect commits for
    /// <see cref="BaseBrokenFailureKind.Test"/>.
    /// </summary>
    public string? ErrorSummary { get; init; }

    /// <summary>
    /// Which evidence proved the base broken. Pre-existing rows (written
    /// before this field existed) read back as
    /// <see cref="BaseBrokenFailureKind.Build"/>.
    /// </summary>
    public BaseBrokenFailureKind FailureKind { get; init; } = BaseBrokenFailureKind.Build;

    /// <summary>
    /// Failing test or CI-check names for
    /// <see cref="BaseBrokenFailureKind.Test"/> (sanitized, bounded at the
    /// write site). Empty for build-kind rows. Pre-existing rows read back
    /// empty.
    /// </summary>
    public IReadOnlyList<string> FailingNames { get; init; } = [];

    /// <summary>The auto-filed fix item for this base SHA, once filed.</summary>
    public WorkItemId? FixWorkItemId { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>
    /// Set when the base is healthy again (tip builds for build-kind, tip CI
    /// passes for test-kind); null while the hold is active.
    /// </summary>
    public DateTimeOffset? ClearedAt { get; init; }
}

/// <summary>
/// Durable store of <see cref="BaseBrokenCondition"/> rows so the
/// dispatcher hold survives an orchestrator restart.
/// </summary>
public interface IBaseBrokenConditionStore
{
    /// <summary>
    /// Inserts the condition or updates the error summary / repository id of
    /// an existing row for the same (project, base SHA). Never resurrects a
    /// cleared row: a cleared (project, sha) stays cleared — a later broken
    /// tip produces a different SHA and therefore a different row.
    /// </summary>
    Task UpsertAsync(BaseBrokenCondition condition, CancellationToken ct = default);

    /// <summary>Records the fix item and repository id on an active row. No-op when no active row matches.</summary>
    Task AttachFixItemAsync(
        ProjectId projectId,
        string baseSha,
        WorkItemId fixWorkItemId,
        string repositoryId,
        CancellationToken ct = default);

    /// <summary>Marks the active row for (project, sha) cleared. No-op when no active row matches.</summary>
    Task ClearAsync(
        ProjectId projectId,
        string baseSha,
        DateTimeOffset clearedAt,
        CancellationToken ct = default);

    /// <summary>All conditions that have not been cleared.</summary>
    Task<IReadOnlyList<BaseBrokenCondition>> ListActiveAsync(CancellationToken ct = default);

    /// <summary>Active conditions for one project.</summary>
    Task<IReadOnlyList<BaseBrokenCondition>> ListActiveForProjectAsync(
        ProjectId projectId,
        CancellationToken ct = default);
}

/// <summary>
/// Read-side provider exposing the currently held (active) base-broken
/// conditions for queue-status reporting. Implemented by the orchestrator's
/// condition tracker.
/// </summary>
public interface IBaseBrokenConditionStatusProvider
{
    IReadOnlyList<BaseBrokenCondition> GetActiveConditions();
}

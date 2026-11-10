using CodeyBox.Core;

namespace CodeyBox.Projects;

/// <summary>
/// Live host inputs for project shared-folder load-time validation. The
/// allow-list roots are read through an accessor on every config build so an
/// operator edit to the Incus <c>AllowedHostMountRoots</c> applies to the
/// next project reload without a restart; the defense-in-depth mount-time
/// authorization (<c>IncusMountStaging.Prepare</c>) re-checks the same roots
/// on every sandbox creation regardless of when the project was last rebuilt.
/// </summary>
public sealed record ProjectSharedFolderPolicy
{
    /// <summary>
    /// Allow-list roots shared host paths must resolve under. Empty (the
    /// default) rejects every host path, mirroring the Incus empty-means-reject
    /// posture. Wire to the Incus <c>AllowedHostMountRoots</c> plus the
    /// provider staging root.
    /// </summary>
    public Func<IReadOnlyList<string>> AllowedRoots { get; init; } = () => [];

    /// <summary>Orchestrator bare-repo root. Null skips the repos-overlap check.</summary>
    public Func<string?> RepositoriesDirectory { get; init; } = () => null;

    /// <summary>Orchestrator state-database path. Null skips the state-overlap check.</summary>
    public Func<string?> StateDatabasePath { get; init; } = () => null;

    /// <summary>Shared validation context for one config build.</summary>
    public SharedFolderValidationContext ToValidationContext() => new()
    {
        AllowedRoots = AllowedRoots() ?? [],
        RepositoriesDirectory = RepositoriesDirectory(),
        StateDatabasePath = StateDatabasePath(),
    };
}

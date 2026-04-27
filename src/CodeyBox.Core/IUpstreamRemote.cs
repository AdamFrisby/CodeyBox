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
}

public sealed record UpstreamPushResult(bool Success, string? Error);

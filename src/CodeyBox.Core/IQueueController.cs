namespace CodeyBox.Core;

public enum QueueState { Running, Paused }

/// <summary>
/// Global queue gate and per-project queue gate. Pausing blocks new work-item
/// pickup without cancelling in-flight items. Persisted so restarts preserve
/// operator intent.
/// </summary>
public interface IQueueController
{
    // ── Global gate ───────────────────────────────────────────────────────────

    QueueState State { get; }
    DateTimeOffset? PausedAt { get; }
    string? PausedReason { get; }

    Task PauseAsync(string reason, CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);

    // ── Per-project gate ──────────────────────────────────────────────────────

    /// <summary>
    /// Pauses pickup for a single project. Idempotent: calling while already
    /// paused updates the reason but does not raise an error.
    /// </summary>
    Task PauseProjectAsync(ProjectId projectId, string reason, CancellationToken ct = default);

    /// <summary>Resumes pickup for a single project. No-op if not paused.</summary>
    Task ResumeProjectAsync(ProjectId projectId, CancellationToken ct = default);

    /// <summary>
    /// Returns the per-project pause state, or null if no row exists for the project
    /// (which means it is running).
    /// </summary>
    Task<ProjectQueueState?> GetProjectStateAsync(ProjectId projectId, CancellationToken ct = default);
}

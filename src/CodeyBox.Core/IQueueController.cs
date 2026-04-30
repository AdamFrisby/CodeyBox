namespace CodeyBox.Core;

public enum QueueState { Running, Paused }

/// <summary>
/// Global queue gate. Pausing blocks new work-item pickup without cancelling
/// in-flight items. Persisted so a restart preserves operator intent.
/// </summary>
public interface IQueueController
{
    QueueState State { get; }
    DateTimeOffset? PausedAt { get; }
    string? PausedReason { get; }

    Task PauseAsync(string reason, CancellationToken ct = default);
    Task ResumeAsync(CancellationToken ct = default);
}

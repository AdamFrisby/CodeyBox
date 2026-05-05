namespace CodeyBox.Core;

public sealed record ProjectQueueState(
    ProjectId Project,
    bool Paused,
    DateTimeOffset? PausedAt,
    string? PausedReason);

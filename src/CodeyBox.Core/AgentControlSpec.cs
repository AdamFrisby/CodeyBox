namespace CodeyBox.Core;

/// <summary>
/// Runtime control action represented as a queued work item.
/// </summary>
public sealed record AgentControlSpec
{
    public required AgentControlAction Action { get; init; }
    public required string Agent { get; init; }
    public string? Reason { get; init; }
    public int? DurationSeconds { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}

public enum AgentControlAction
{
    Pause = 0,
    Resume = 1,
}

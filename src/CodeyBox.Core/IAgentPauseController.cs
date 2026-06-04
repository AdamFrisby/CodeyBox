namespace CodeyBox.Core;

/// <summary>
/// Runtime operator control for excluding one agent kind from new dispatches.
/// Pauses are persisted by the host implementation so intent survives restart.
/// In-flight agent runs are not cancelled by this contract.
/// </summary>
public interface IAgentPauseController
{
    Task<AgentPauseState> PauseAsync(
        AgentKind agent,
        string reason,
        string pausedBy,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default);

    Task<bool> ResumeAsync(
        AgentKind agent,
        string resumedBy,
        string? reason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Returns active pause state for <paramref name="agent"/>, or null when
    /// the agent is dispatchable. Implementations may lazily clear expired
    /// pauses before answering.
    /// </summary>
    Task<AgentPauseState?> GetAgentStateAsync(AgentKind agent, CancellationToken ct = default);

    /// <summary>
    /// Returns every currently active pause. Implementations may lazily clear
    /// expired pauses before answering.
    /// </summary>
    Task<IReadOnlyList<AgentPauseState>> ListPausedAsync(CancellationToken ct = default);
}

/// <summary>
/// Lightweight wake signal emitted whenever operator pause state changes,
/// including auto-expiry. Consumers use it to re-evaluate parked work.
/// </summary>
public interface IAgentPauseSignal
{
    event Action? AgentPauseChanged;
}

public sealed record AgentPauseState(
    AgentKind Agent,
    bool Paused,
    DateTimeOffset? PausedAt,
    string? PausedReason,
    string? PausedBy,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset UpdatedAt);

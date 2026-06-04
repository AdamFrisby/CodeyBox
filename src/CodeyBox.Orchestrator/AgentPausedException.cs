using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised when a new agent dispatch is blocked because every viable target for
/// the current phase is paused by an operator.
/// </summary>
public sealed class AgentPausedException : Exception
{
    public string Phase { get; }
    public AgentKind Agent { get; }
    public string PauseReason { get; }

    public AgentPausedException(string phase, AgentKind agent, string pauseReason)
        : base($"agent '{agent.Value}' is {pauseReason}")
    {
        Phase = phase;
        Agent = agent;
        PauseReason = pauseReason;
    }
}

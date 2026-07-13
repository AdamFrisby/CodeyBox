using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal sealed class AgentInfrastructureFailureException : Exception
{
    public AgentInfrastructureFailureException(
        AgentKind agent,
        string phase,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Agent = agent;
        Phase = phase;
    }

    public AgentKind Agent { get; }
    public string Phase { get; }
}

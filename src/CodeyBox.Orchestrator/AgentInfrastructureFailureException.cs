using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal sealed class AgentInfrastructureFailureException : Exception
{
    public AgentInfrastructureFailureException(AgentKind agent, string phase, string message)
        : base(message)
    {
        Agent = agent;
        Phase = phase;
    }

    public AgentKind Agent { get; }
    public string Phase { get; }
}

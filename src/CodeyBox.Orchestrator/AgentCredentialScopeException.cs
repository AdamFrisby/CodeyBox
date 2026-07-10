using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal sealed class AgentCredentialScopeException : Exception
{
    public AgentCredentialScopeException(AgentKind agent, string reason)
        : base($"Resolver credential scope for agent '{agent.Value}' is invalid: {reason}")
    {
        Agent = agent;
    }

    public AgentKind Agent { get; }
}

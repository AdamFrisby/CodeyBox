using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal sealed class AgentInfrastructureFailureException : Exception, IExecutionTransportLoss
{
    public AgentInfrastructureFailureException(
        AgentKind agent,
        string phase,
        string message,
        Exception? innerException = null,
        bool executionUnavailable = false)
        : base(message, innerException)
    {
        Agent = agent;
        Phase = phase;
        ExecutionUnavailable = executionUnavailable;
    }

    public AgentKind Agent { get; }
    public string Phase { get; }

    /// <summary>
    /// True when the sandbox's execution transport was severed underneath the
    /// phase (VM destroyed — e.g. by the leak reaper — host crash, or an
    /// unreachable exec channel). The item did nothing wrong and a fresh
    /// sandbox reproduces a working environment, so the RunAsync catch parks
    /// it for bounded transient retry with an infrastructure classification
    /// instead of writing a terminal failure and letting the clone reaper
    /// delete the working tree.
    /// </summary>
    public bool ExecutionUnavailable { get; }
}

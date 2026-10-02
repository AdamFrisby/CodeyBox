using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal sealed class AgentInfrastructureFailureException : Exception, IExecutionTransportLoss
{
    public AgentInfrastructureFailureException(
        AgentKind agent,
        string phase,
        string message,
        Exception? innerException = null,
        bool executionUnavailable = false,
        bool isOutputBound = false)
        : base(message, innerException)
    {
        Agent = agent;
        Phase = phase;
        ExecutionUnavailable = executionUnavailable;
        IsOutputBound = isOutputBound;
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

    /// <summary>
    /// True when the failure is an output-volume bound (a bounded exec was
    /// killed or cut for exceeding its output cap). Still infrastructure —
    /// the agent did nothing wrong — but reported under the distinct
    /// <c>output-bound</c> failure kind so a volume kill is never
    /// misreported as generic termination (for example exit 137).
    /// </summary>
    public bool IsOutputBound { get; }
}

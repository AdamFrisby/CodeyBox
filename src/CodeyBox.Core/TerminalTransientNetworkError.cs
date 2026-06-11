namespace CodeyBox.Core;

public sealed class TerminalTransientNetworkError : Exception
{
    public AgentKind Agent { get; }
    public string? Phase { get; }
    public AgentFailureClassification Classification { get; }

    public TerminalTransientNetworkError(
        AgentKind agent,
        string? phase,
        AgentFailureClassification classification,
        string message)
        : base(message)
    {
        Agent = agent;
        Phase = phase;
        Classification = classification;
    }
}

using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Single source for the distinct output-bound failure shape. A bounded exec
/// that is killed (or cut) for exceeding its output cap is infrastructure —
/// the agent did nothing wrong — but it must surface as the
/// <c>output-bound</c> kind, never as generic termination (for example exit
/// 137). Agent-turn streaming execs never trip this: they stream without a
/// cumulative kill threshold.
/// </summary>
internal static class AgentOutputBoundFailure
{
    /// <summary>
    /// True when the agent result carries a provider output-bound signal,
    /// regardless of the process exit code a volume kill produced.
    /// </summary>
    internal static bool IsOutputBound(AgentResult result) => result.OutputLimitExceeded;

    /// <summary>Failure detail for an agent turn ended by an output bound.</summary>
    internal static string DescribeTurn(AgentKind agent, string phase) =>
        $"Agent {agent.Value} exceeded its output bound ({WorkItemFailureKinds.OutputBound}) in {phase}";

    /// <summary>Failure detail after session recovery met an output bound.</summary>
    internal static string DescribeResumeExhausted(AgentKind agent) =>
        $"Agent {agent.Value} exhausted native session recovery after exceeding its output bound ({WorkItemFailureKinds.OutputBound})";
}

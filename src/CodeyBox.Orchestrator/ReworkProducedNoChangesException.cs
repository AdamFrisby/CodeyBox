using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised by <see cref="PipelineRunner"/>'s rework phase when the agent exited
/// successfully but committed no changes AND no infrastructure (auth / quota)
/// signature explains the empty result. The audit/rework loop catches this and
/// applies converge-aware handling (escalation re-dispatch, park, or terminal
/// fail) so a single empty pass on a converging item does not discard the
/// remaining iteration budget.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from the initial-work no-changes path, which still terminal-fails
/// fast through <see cref="InvalidOperationException"/>. There is no audit loop
/// behind initial work to recover from "agent declined to do anything," so the
/// asymmetry is deliberate.
/// </para>
/// <para>
/// Carries the rework agent's stdout / stderr so callers can attach the agent's
/// last word to follow-up telemetry (park-for-operator messages, audit log
/// entries) without re-querying the dead sandbox.
/// </para>
/// </remarks>
public sealed class ReworkProducedNoChangesException : Exception
{
    public AgentKind Agent { get; }
    public string? AgentStdout { get; }
    public string? AgentStderr { get; }

    public ReworkProducedNoChangesException(
        AgentKind agent,
        string? agentStdout,
        string? agentStderr,
        string message)
        : base(message)
    {
        Agent = agent;
        AgentStdout = agentStdout;
        AgentStderr = agentStderr;
    }
}

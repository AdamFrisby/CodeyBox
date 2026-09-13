using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised by <see cref="PipelineRunner"/>'s initial work phase when the agent
/// exited successfully with no diff AND reported — via the structured
/// <c>.codeybox/no-action-required.json</c> protocol — that no action is
/// warranted (e.g. a conditional item whose precondition does not hold).
/// <see cref="PipelineRunner.RunAsync"/> catches this and resolves the item
/// terminally to <see cref="WorkItemState.NoActionRequired"/> instead of
/// failing it: the outcome is the agent's explicit determination, not a
/// silent failure, so it must neither feed the no-changes circuit breaker
/// nor re-enter the queue.
/// </summary>
/// <remarks>
/// Propagates straight through the quota-fallback wrapper (only quota, auth,
/// timeout, and resume-exhaustion trigger fallback) because the determination
/// is precondition-dependent, not agent-dependent — re-running another agent
/// would burn quota re-establishing the same conclusion.
/// </remarks>
internal sealed class NoActionRequiredException : Exception
{
    public AgentKind Agent { get; }

    public string Reason { get; }

    public string? Precondition { get; }

    public NoActionRequiredException(
        AgentKind agent,
        string reason,
        string? precondition = null)
        : base($"Agent reported no action required: {reason}")
    {
        Agent = agent;
        Reason = reason;
        Precondition = precondition;
    }
}

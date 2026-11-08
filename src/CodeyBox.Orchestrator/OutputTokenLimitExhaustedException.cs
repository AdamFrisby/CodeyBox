using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised by <see cref="PipelineRunner"/>'s work phase when the runner
/// reports <see cref="AgentResult.OutputTokenLimitExhausted"/>: the model
/// hit its per-turn output-token ceiling and the runner's bounded
/// same-session continuations were spent without the turn completing (see
/// <see cref="OutputTokenLimit"/>). <see cref="PipelineRunner.RunAsync"/>
/// catches this and fails the item terminally with failure kind
/// <see cref="WorkItemFailureKinds.OutputLimitExhausted"/> — the agent was
/// cut off by a provider ceiling, not declining to work, so the outcome
/// must never surface as quota, transient, or "produced no changes".
///
/// <para>Propagates straight through the quota-fallback wrapper like
/// <see cref="NoActionRequiredException"/> (only quota, auth, timeout, and
/// resume-exhaustion trigger fallback): the ceiling is a property of the
/// task's fit against the provider limit, not of the agent choice, and the
/// continuation budget is already spent — re-running another agent restarts
/// the same oversized task.</para>
/// </summary>
internal sealed class OutputTokenLimitExhaustedException : Exception
{
    public AgentKind Agent { get; }

    public string? Phase { get; }

    public OutputTokenLimitExhaustedException(
        AgentKind agent,
        string? phase,
        string detail)
        : base(detail)
    {
        Agent = agent;
        Phase = phase;
    }
}

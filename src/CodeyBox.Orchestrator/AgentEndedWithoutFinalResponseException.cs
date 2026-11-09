using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised by <see cref="PipelineRunner"/>'s work phase when the runner
/// reports <see cref="AgentResult.EndedWithoutFinalResponse"/>: a clean-exit
/// turn ended with no final assistant message and no recorded edits, and the
/// runner's bounded same-session continuations were spent (or no continuation
/// was possible) without the turn producing either. <see cref="PipelineRunner.RunAsync"/>
/// catches this and fails the item terminally with failure kind
/// <see cref="WorkItemFailureKinds.EndedWithoutFinalResponse"/> — the turn
/// died mid-investigation, not declining to work, so the outcome must never
/// surface as quota, transient, or "produced no changes".
///
/// <para>Propagates straight through the quota-fallback wrapper like
/// <see cref="NoActionRequiredException"/> (only quota, auth, timeout, and
/// resume-exhaustion trigger fallback): the dead turn is a property of how
/// that run ended, not of the agent choice, and the continuation budget is
/// already spent.</para>
/// </summary>
internal sealed class AgentEndedWithoutFinalResponseException : Exception
{
    public AgentKind Agent { get; }

    public string? Phase { get; }

    public AgentEndedWithoutFinalResponseException(
        AgentKind agent,
        string? phase,
        string detail)
        : base(detail)
    {
        Agent = agent;
        Phase = phase;
    }
}

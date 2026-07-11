using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised when a work item cannot be dispatched to an agent because
/// pre-dispatch availability, credential, smoke-gate, or routing checks
/// rejected the available candidates. The pickup-time rebase resolver is one
/// caller, but normal work/audit/merge phase dispatch can raise the same
/// exception before any agent reasoning loop starts.
///
/// <para>A single rejected runner is recorded as
/// <c>failureKind=agent_unavailable</c>; aggregate no-candidate routing misses
/// are recorded as <c>failureKind=agent_routing_unavailable</c>.</para>
/// </summary>
public sealed class AgentUnavailableException : Exception
{
    /// <summary>
    /// Agent most directly responsible for the unavailability when the failure
    /// is attributable to a single rejected runner. Null for aggregate "no
    /// candidate" cases.
    /// </summary>
    public AgentKind? Agent { get; }

    /// <summary>
    /// Comma-separated short reasons explaining which candidates were
    /// considered and why each was rejected (e.g.
    /// <c>"gemini: GEMINI_API_KEY is required; claude: CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY is required"</c>).
    /// Diagnostic context for callers to embed in the exception message or logs.
    /// </summary>
    public string CandidateReasons { get; }

    public AgentUnavailableException(string message, string candidateReasons, AgentKind? agent = null)
        : base(message)
    {
        Agent = agent;
        CandidateReasons = candidateReasons;
    }
}

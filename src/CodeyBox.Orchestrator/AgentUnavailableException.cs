using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised when an internal sub-phase (currently: the pickup-time rebase
/// conflict resolver) needs a text-only agent invocation but no registered
/// runner has a viable credential for it. Distinct from
/// <see cref="MergeConflictResolutionFailedException"/>: the resolver never
/// got to run, so the work item is parked at the failure with
/// <c>failureKind=agent_unavailable</c> rather than
/// <see cref="WorkItemState.MergeConflictResolutionFailed"/> — the latter
/// implies the resolver ran but couldn't reconcile the conflict.
/// </summary>
public sealed class AgentUnavailableException : Exception
{
    /// <summary>
    /// Comma-separated short reasons explaining which candidates were
    /// considered and why each was rejected (e.g.
    /// <c>"gemini: GEMINI_API_KEY is required; claude: CLAUDE_CODE_OAUTH_TOKEN or ANTHROPIC_API_KEY is required"</c>).
    /// Surfaced verbatim in <c>WorkItem.LastError</c>.
    /// </summary>
    public string CandidateReasons { get; }

    public AgentUnavailableException(string message, string candidateReasons)
        : base(message)
    {
        CandidateReasons = candidateReasons;
    }
}

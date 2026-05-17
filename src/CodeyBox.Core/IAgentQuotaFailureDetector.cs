namespace CodeyBox.Core;

/// <summary>
/// Per-provider quota-failure detector. Each agent library implements one to
/// recognise its own CLI's quota and rate-limit error shapes (stderr strings,
/// stream-json wrapped errors, exotic payload fields). The orchestrator
/// dispatches to the matching detector by <see cref="Kind"/>; this keeps
/// provider-specific text patterns out of the orchestrator.
/// </summary>
public interface IAgentQuotaFailureDetector
{
    /// <summary>The agent kind this detector recognises.</summary>
    AgentKind Kind { get; }

    /// <summary>
    /// Inspects captured stderr/stdout and returns a detection if the output
    /// matches a known quota/rate-limit/auth failure for this agent.
    /// Returns null when nothing matches. Must never throw.
    /// </summary>
    QuotaDetection? Detect(string? stderr, string? stdout);
}

/// <summary>
/// Classification result for a detected quota failure. <see cref="ResetAt"/>
/// is populated only when the source text included a parseable reset/retry
/// interval (e.g. "reset after 21h41m24s").
/// </summary>
public sealed record QuotaDetection(QuotaFailureKind Kind, DateTimeOffset? ResetAt = null);

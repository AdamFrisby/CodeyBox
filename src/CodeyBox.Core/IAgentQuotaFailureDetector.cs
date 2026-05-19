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

    /// <summary>
    /// Optional hook: emit agent-specific advisory audit-log events for
    /// non-quota failure signals — e.g. Claude's 401 (shared-OAuth refresh
    /// race / expired access token), which is intentionally <em>not</em>
    /// classified as a quota event but is still operationally interesting.
    /// Called by the orchestrator at most once per agent failure. The default
    /// implementation does nothing. Must never throw.
    /// </summary>
    void EmitAdvisoryAuditEvents(string? stderr, string? stdout, string phase, string? sandboxName) { }
}

/// <summary>
/// Classification result for a detected quota failure. <see cref="ResetAt"/>
/// is populated only when the source text included a parseable reset/retry
/// interval (e.g. "reset after 21h41m24s").
/// </summary>
public sealed record QuotaDetection(QuotaFailureKind Kind, DateTimeOffset? ResetAt = null);

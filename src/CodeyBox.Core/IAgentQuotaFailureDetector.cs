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

    /// <summary>
    /// Optional hook: returns true when the captured streams represent a
    /// terminal agent/API crash that is <em>not</em> a quota or rate-limit
    /// signal (e.g. Claude's API 400 thinking-block modification crash).
    /// The orchestrator calls this before <see cref="Detect"/>; a true result
    /// short-circuits classification so the work item enters normal recovery
    /// rather than being parked in WaitingForQuotaReset. The default
    /// implementation returns false. Must never throw.
    /// </summary>
    bool IsTerminalNonQuotaCrash(string? stderr, string? stdout) => false;

    /// <summary>
    /// Optional hook: scopes <paramref name="stdout"/> to the slice the detector
    /// should consider for quota classification. Defaults to returning the input
    /// unchanged. Claude overrides this to narrow long multi-turn NDJSON buffers
    /// to their terminal error line, so stale rate-limit text from earlier
    /// events cannot false-positive the final failure. Must never throw.
    /// </summary>
    string? ScopeStdoutForQuotaDetection(string? stdout) => stdout;
}

/// <summary>
/// Classification result for a detected quota failure. <see cref="ResetAt"/>
/// is populated only when the source text included a parseable reset/retry
/// interval (e.g. "reset after 21h41m24s").
/// </summary>
public sealed record QuotaDetection(QuotaFailureKind Kind, DateTimeOffset? ResetAt = null);

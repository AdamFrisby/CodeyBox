namespace CodeyBox.Core;

/// <summary>
/// High-level quota classification returned by <see cref="IQuotaFailureClassifier"/>.
/// <see cref="TerminalNonQuota"/> represents deterministic terminal API failures
/// that must not be parked as quota failures and also should not be retried via
/// CLI-native session resume.
/// </summary>
public enum QuotaFailureClassificationKind
{
    None,
    Quota,
    TerminalNonQuota,
}

public sealed record QuotaFailureClassification(
    QuotaFailureClassificationKind Kind,
    QuotaDetection? Detection)
{
    public static readonly QuotaFailureClassification None =
        new(QuotaFailureClassificationKind.None, Detection: null);

    public static readonly QuotaFailureClassification TerminalNonQuota =
        new(QuotaFailureClassificationKind.TerminalNonQuota, Detection: null);

    public static QuotaFailureClassification Quota(QuotaDetection detection)
    {
        ArgumentNullException.ThrowIfNull(detection);
        return new QuotaFailureClassification(QuotaFailureClassificationKind.Quota, detection);
    }
}

/// <summary>
/// Dispatches quota-failure detection to the per-provider
/// <see cref="IAgentQuotaFailureDetector"/> registered for a given
/// <see cref="AgentKind"/>. Returns <see cref="QuotaFailureClassification.None"/>
/// when no detector is registered for the agent or no signal matches.
/// </summary>
public interface IQuotaFailureClassifier
{
    QuotaFailureClassification Classify(AgentKind agent, string? stderr, string? stdout);

    QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout);

    /// <summary>
    /// Dispatches to the per-agent detector's
    /// <see cref="IAgentQuotaFailureDetector.EmitAdvisoryAuditEvents"/> hook so
    /// per-provider non-quota failure signals (e.g. Claude 401) can produce
    /// agent-specific audit-log lines without leaking provider knowledge into
    /// callers. Safe to call regardless of whether the agent failed.
    /// </summary>
    void EmitAdvisoryAuditEvents(AgentKind agent, string? stderr, string? stdout, string phase, string? sandboxName);
}

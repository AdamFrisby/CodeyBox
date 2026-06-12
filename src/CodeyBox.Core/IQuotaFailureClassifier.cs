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
///
/// <para>
/// This interface is the classification-only contract: callers that only need
/// to know whether a failed agent run is a quota/rate event (e.g. agent
/// runners gating session resume) depend on this surface. Per-agent
/// audit-event emission is exposed separately via
/// <see cref="IQuotaFailureAuditEmitter"/> so the agent layer is not coupled
/// to orchestrator-only audit responsibilities. Composite implementations may
/// implement both.
/// </para>
/// </summary>
public interface IQuotaFailureClassifier
{
    QuotaFailureClassification Classify(AgentKind agent, string? stderr, string? stdout);

    QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout);
}

/// <summary>
/// Orchestrator-side audit emitter for per-agent non-quota failure signals
/// (e.g. Claude 401, opencode credit warnings). Lives in a separate interface
/// from <see cref="IQuotaFailureClassifier"/> so runners that only need
/// classification do not depend on phase/sandbox audit parameters they never
/// supply. Implementations dispatch to
/// <see cref="IAgentQuotaFailureDetector.EmitAdvisoryAuditEvents"/>; safe to
/// call regardless of whether the agent failed.
/// </summary>
public interface IQuotaFailureAuditEmitter
{
    void EmitAdvisoryAuditEvents(AgentKind agent, string? stderr, string? stdout, string phase, string? sandboxName);
}

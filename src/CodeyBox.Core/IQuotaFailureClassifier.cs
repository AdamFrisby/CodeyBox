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

public sealed class CompositeQuotaFailureClassifier : IQuotaFailureClassifier
{
    private readonly IReadOnlyDictionary<AgentKind, IAgentQuotaFailureDetector> _detectors;

    public CompositeQuotaFailureClassifier(IEnumerable<IAgentQuotaFailureDetector> detectors)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        _detectors = detectors.ToDictionary(d => d.Kind);
    }

    public QuotaFailureClassification Classify(AgentKind agent, string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return QuotaFailureClassification.None;

        if (!_detectors.TryGetValue(agent, out var detector))
            return QuotaFailureClassification.None;

        // Provider-specific terminal API crashes (e.g. Claude 400 thinking-block
        // modification) are not quota exhaustion. The detector decides, keeping
        // this dispatch path agnostic of any single provider's stream-json shape.
        if (detector.IsTerminalNonQuotaCrash(stderr, stdout))
            return QuotaFailureClassification.TerminalNonQuota;

        var scopedStdout = detector.ScopeStdoutForQuotaDetection(stdout);
        var detection = detector.Detect(stderr, scopedStdout);
        return detection is null
            ? QuotaFailureClassification.None
            : QuotaFailureClassification.Quota(detection);
    }

    public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout)
        => Classify(agent, stderr, stdout).Detection;

    public void EmitAdvisoryAuditEvents(AgentKind agent, string? stderr, string? stdout, string phase, string? sandboxName)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return;
        if (_detectors.TryGetValue(agent, out var detector))
            detector.EmitAdvisoryAuditEvents(stderr, stdout, phase, sandboxName);
    }
}

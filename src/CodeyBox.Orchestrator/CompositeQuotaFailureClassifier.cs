using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatches quota-failure detection to the per-provider detectors registered
/// by the composition root.
/// </summary>
public sealed class CompositeQuotaFailureClassifier : IQuotaFailureClassifier, IQuotaFailureAuditEmitter
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
        if (detection is null)
            return QuotaFailureClassification.None;

        // Evidence-trust split: a marker on process stderr is provider-emitted
        // (the agent cannot plant it in its own prose), so it keeps the legacy
        // quota path unconditionally. A stdout/stream-only match is
        // agent-quotable — the agent may be discussing, committing, or forging
        // provider errors — so callers must corroborate it against the live
        // quota probe before parking. Re-running Detect on stderr alone (rather
        // than threading match sources through every detector) keeps this in
        // one place; the primary detection above is unchanged.
        var providerSurfaceMatch = !string.IsNullOrEmpty(stderr)
            && detector.Detect(stderr, null) is not null;
        return QuotaFailureClassification.Quota(detection, providerSurfaceMatch);
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

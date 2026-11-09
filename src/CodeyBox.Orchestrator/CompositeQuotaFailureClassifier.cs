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

        // Evidence-trust split: process stderr is the CLI's own surface, but for
        // Antigravity it ALSO carries the CLI-log terminal region folded in by
        // AntigravityAgentRunner (the glog interleaves verbatim model/tool
        // output, so a tool-output line echoing quota prose plus a genuine
        // auth signal would otherwise park on a fake reset instead of benching
        // compromised credentials). Because classifier-facing Stderr cannot
        // distinguish process bytes from folded glog bytes, Antigravity stderr
        // matches are NEVER trusted provider-surface: callers must corroborate
        // them against the live quota probe before parking (a healthy probe
        // vetoes the park) and before quota suppresses auth. Other agents keep
        // the legacy trusted-stderr path. A stdout/stream-only match is
        // agent-quotable for every agent, so it is always untrusted.
        // Re-running Detect on stderr alone (rather than threading match
        // sources through every detector) keeps this in one place; the primary
        // detection above is unchanged.
        var stderrMatch = !string.IsNullOrEmpty(stderr)
            && detector.Detect(stderr, null) is not null;
        var providerSurfaceMatch = stderrMatch && agent != AgentKind.Antigravity;
        return QuotaFailureClassification.Quota(detection, providerSurfaceMatch);
    }

    public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout)
        => Classify(agent, stderr, stdout).Detection;

    public ProviderTransientDetection? DetectProviderTransient(
        AgentKind agent,
        string? stderr,
        string? stdout,
        string? summary = null)
    {
        // The stream-shaped entry point carries no envelope provenance, so
        // it is treated as an unframed capture: detectors that need
        // StdoutIsEnvelopeFramed only trust envelope structure on results
        // the runner stamped (see the AgentResult overload below). The
        // empty-input guard, detector lookup, and exception contract live
        // once in that overload.
        return DetectProviderTransient(agent, new AgentResult(
            Success: false,
            Summary: summary ?? string.Empty,
            Stdout: stdout,
            Stderr: stderr));
    }

    /// <summary>
    /// Result-shaped dispatch: forwards <see cref="AgentResult.StdoutIsEnvelopeFramed"/>
    /// so detectors can gate envelope-structure signatures on real shim
    /// provenance. The stream-shaped overload above carries no provenance and
    /// is treated as an unframed capture.
    /// </summary>
    public ProviderTransientDetection? DetectProviderTransient(AgentKind agent, AgentResult result)
    {
        if (string.IsNullOrEmpty(result.Stderr) && string.IsNullOrEmpty(result.Stdout) && string.IsNullOrEmpty(result.Summary))
            return null;
        try
        {
            if (!_detectors.TryGetValue(agent, out var detector))
                return null;
            return detector.DetectProviderTransient(result);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void EmitAdvisoryAuditEvents(AgentKind agent, string? stderr, string? stdout, string phase, string? sandboxName)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return;
        if (_detectors.TryGetValue(agent, out var detector))
            detector.EmitAdvisoryAuditEvents(stderr, stdout, phase, sandboxName);
    }
}

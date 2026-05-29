using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Dispatches quota-failure detection to the per-provider
/// <see cref="IAgentQuotaFailureDetector"/> registered for a given
/// <see cref="AgentKind"/>. Returns null when no detector is registered for
/// the agent — callers treat that as "not a recognised quota failure".
/// </summary>
public interface IQuotaFailureClassifier
{
    QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout);

    Task RecordIfQuotaFailureAsync(
        IQuotaFailureStore? store,
        AgentKind agent,
        string? modelId,
        string? summary,
        string? stderr,
        DateTimeOffset observedAt,
        TimeSpan retention,
        CancellationToken ct,
        ProjectId? projectId = null,
        string? stdout = null);

    /// <summary>
    /// Dispatches to the per-agent detector's
    /// <see cref="IAgentQuotaFailureDetector.EmitAdvisoryAuditEvents"/> hook so
    /// per-provider non-quota failure signals (e.g. Claude 401) can produce
    /// agent-specific audit-log lines without leaking provider knowledge into
    /// the orchestrator. Safe to call regardless of whether the agent failed.
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

    public QuotaDetection? Detect(AgentKind agent, string? stderr, string? stdout)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return null;

        if (!_detectors.TryGetValue(agent, out var detector))
            return null;

        // Provider-specific terminal API crashes (e.g. Claude 400 thinking-block
        // modification) are not quota exhaustion. The detector decides — keeping
        // this dispatch path agnostic of any single provider's stream-json shape.
        if (detector.IsTerminalNonQuotaCrash(stderr, stdout))
            return null;

        var scopedStdout = detector.ScopeStdoutForQuotaDetection(stdout);
        return detector.Detect(stderr, scopedStdout);
    }

    public void EmitAdvisoryAuditEvents(AgentKind agent, string? stderr, string? stdout, string phase, string? sandboxName)
    {
        if (string.IsNullOrEmpty(stderr) && string.IsNullOrEmpty(stdout))
            return;
        if (_detectors.TryGetValue(agent, out var detector))
            detector.EmitAdvisoryAuditEvents(stderr, stdout, phase, sandboxName);
    }

    public async Task RecordIfQuotaFailureAsync(
        IQuotaFailureStore? store,
        AgentKind agent,
        string? modelId,
        string? summary,
        string? stderr,
        DateTimeOffset observedAt,
        TimeSpan retention,
        CancellationToken ct,
        ProjectId? projectId = null,
        string? stdout = null)
    {
        if (store is null)
            return;

        if (!IsAgentExited1Summary(summary))
            return;

        var detection = Detect(agent, stderr, stdout);
        if (detection is null)
            return;

        if (projectId is { } scopedProject)
            await store.RecordForProjectAsync(agent, modelId, scopedProject, detection.Kind, observedAt, ct);
        else
            await store.RecordAsync(agent, modelId, detection.Kind, observedAt, ct);

        await store.PruneOlderThanAsync(observedAt - retention, ct);
    }

    /// <summary>
    /// Matches the "agent exited 1" guard shape, allowing a single appended
    /// diagnostic tail of the form <c>"agent exited 1: &lt;stderr-fragment&gt;"</c>.
    /// Provider runners (notably <c>GeminiAgentRunner</c>) now enrich the
    /// summary so operators can tell quota from auth from transport without
    /// reading the audit log; the persistent observed-failure store still
    /// needs to recognise those summaries as exit-1 failures, otherwise the
    /// next pickup wouldn't skip a Gemini member that just exhausted quota
    /// and the iteration would be burned re-discovering exhaustion.
    /// Other failure shapes (e.g. <c>"failed to materialise gemini auth: exit 1"</c>)
    /// remain excluded — they are infrastructure failures, not provider quota
    /// signals.
    /// </summary>
    internal static bool IsAgentExited1Summary(string? summary)
    {
        if (string.IsNullOrEmpty(summary)) return false;
        var trimmed = summary.Trim();
        const string Base = "agent exited 1";
        if (string.Equals(trimmed, Base, StringComparison.OrdinalIgnoreCase))
            return true;
        return trimmed.Length > Base.Length
            && trimmed.StartsWith(Base, StringComparison.OrdinalIgnoreCase)
            && trimmed[Base.Length] == ':';
    }
}

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

        return _detectors.TryGetValue(agent, out var detector)
            ? detector.Detect(stderr, stdout)
            : null;
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

        if (!string.Equals(summary?.Trim(), "agent exited 1", StringComparison.OrdinalIgnoreCase))
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
}

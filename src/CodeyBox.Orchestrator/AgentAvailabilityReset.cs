using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Default <see cref="IAgentAvailabilityReset"/>: composes the availability
/// registry and the in-VM smoke cache so the two are always reset together.
/// Also owns the quota-exhaustion side of the reset: the router's in-process
/// gates plus probe-side runtime 429 overrides, so a cached exhaustion verdict
/// never survives every administrative action but a process restart.
/// </summary>
public sealed class AgentAvailabilityReset : IAgentAvailabilityReset
{
    private readonly ISmokeAvailabilityRegistry _registry;
    private readonly IInVmSmokeCache _cache;
    private readonly IAgentRestorePublisher _restorePublisher;
    private readonly AgentClassRouter? _classRouter;
    private readonly IReadOnlyList<IAgentQuotaProbe> _quotaProbes;
    private readonly ILogger<AgentAvailabilityReset>? _log;

    public AgentAvailabilityReset(
        ISmokeAvailabilityRegistry registry,
        IInVmSmokeCache cache,
        IAgentRestorePublisher restorePublisher,
        AgentClassRouter? classRouter = null,
        IEnumerable<IAgentQuotaProbe>? quotaProbes = null,
        ILogger<AgentAvailabilityReset>? log = null)
    {
        _registry = registry;
        _cache = cache;
        _restorePublisher = restorePublisher;
        _classRouter = classRouter;
        _quotaProbes = quotaProbes?.ToList() ?? [];
        _log = log;
    }

    public void Reset(AgentKind kind)
    {
        var restored = _registry.Reset(kind);
        // Drop any cached in-VM verdict too, so the next sweep / dispatch re-execs
        // the CLI rather than replaying a result captured before the operator's
        // fix (which would otherwise reconcile straight back onto the registry).
        _cache.Invalidate(kind);
        if (restored is not null)
            _restorePublisher.PublishRestored(restored);
    }

    public QuotaExhaustionResetResult ResetQuotaExhaustion(AgentKind kind, string clearedBy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clearedBy);
        var evidence = new List<string>();
        var routerCleared = 0;
        if (_classRouter is not null)
        {
            var removed = _classRouter.ClearExhaustionForAgent(kind);
            routerCleared = removed.Count;
            evidence.AddRange(removed.Select(entry =>
                $"{entry.Key.RouteKey}/{entry.Key.ModelId}: {entry.Value.Evidence?.ToString() ?? "(no recorded evidence)"}"));
        }

        var probeCleared = 0;
        foreach (var probe in _quotaProbes)
        {
            if (probe is not IAgentQuotaExhaustionReset resettable)
                continue;
            try
            {
                probeCleared += resettable.ClearRuntimeExhaustion(kind);
            }
            catch (Exception ex)
            {
                // One probe's clear must not abort the rest; the router gate
                // (the dispatch refusing verdict) is already cleared above.
                _log?.LogWarning(ex, "Clearing probe runtime exhaustion failed for {Agent}", kind.Value);
            }
        }

        var result = new QuotaExhaustionResetResult(routerCleared, probeCleared, evidence);
        AuditLog.AgentQuotaExhaustionCleared(kind, clearedBy, evidence);
        _log?.LogInformation(
            "Operator {ClearedBy} cleared cached quota exhaustion for {Agent}: router={RouterCleared} probes={ProbeCleared}",
            clearedBy, kind.Value, routerCleared, probeCleared);
        return result;
    }
}

using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Default <see cref="IAgentAvailabilityReset"/>: composes the availability
/// registry and the in-VM smoke cache so the two are always reset together.
/// </summary>
public sealed class AgentAvailabilityReset : IAgentAvailabilityReset
{
    private readonly AgentAvailabilityRegistry _registry;
    private readonly IInVmSmokeCache _cache;

    public AgentAvailabilityReset(AgentAvailabilityRegistry registry, IInVmSmokeCache cache)
    {
        _registry = registry;
        _cache = cache;
    }

    public void Reset(AgentKind kind)
    {
        _registry.Reset(kind);
        // Drop any cached in-VM verdict too, so the next sweep / dispatch re-execs
        // the CLI rather than replaying a result captured before the operator's
        // fix (which would otherwise reconcile straight back onto the registry).
        _cache.Invalidate(kind);
    }
}

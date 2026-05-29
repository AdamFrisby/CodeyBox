using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Single operator-reset port for an agent's availability. Resetting an agent
/// after correcting an exclusion (installing the missing binary, rotating
/// credentials) must clear BOTH the availability registry <em>and</em> the in-VM
/// smoke cache as one operation. If a caller cleared only the registry, the next
/// gated dispatch would replay a stale cached pass via
/// <see cref="InVmSmokeProber"/>'s cache-hit reconciliation and re-assert the
/// pre-fix verdict, so the operator's fix would not actually be re-verified until
/// the cache TTL elapsed. Exposing one contract keeps that pairing from leaking
/// into — and being forgotten by — callers such as the admin HTTP endpoint.
/// </summary>
public interface IAgentAvailabilityReset
{
    /// <summary>
    /// Clears <paramref name="kind"/>'s exclusion state and fast-fail counters
    /// and invalidates every cached in-VM smoke verdict for it, so the next
    /// sweep / dispatch re-execs the CLI from scratch.
    /// </summary>
    void Reset(AgentKind kind);
}

/// <summary>
/// Default <see cref="IAgentAvailabilityReset"/>: composes the availability
/// registry and the in-VM smoke cache so the two are always reset together.
/// </summary>
public sealed class AgentAvailabilityReset : IAgentAvailabilityReset
{
    private readonly IAgentAvailabilityRegistry _registry;
    private readonly IInVmSmokeCache _cache;

    public AgentAvailabilityReset(IAgentAvailabilityRegistry registry, IInVmSmokeCache cache)
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

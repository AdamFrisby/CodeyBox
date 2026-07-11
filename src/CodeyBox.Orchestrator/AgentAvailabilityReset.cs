using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Default <see cref="IAgentAvailabilityReset"/>: composes the availability
/// registry and the in-VM smoke cache so the two are always reset together.
/// </summary>
public sealed class AgentAvailabilityReset : IAgentAvailabilityReset
{
    private readonly ISmokeAvailabilityRegistry _registry;
    private readonly IInVmSmokeCache _cache;
    private readonly IAgentRestorePublisher _restorePublisher;

    public AgentAvailabilityReset(
        ISmokeAvailabilityRegistry registry,
        IInVmSmokeCache cache,
        IAgentRestorePublisher restorePublisher)
    {
        _registry = registry;
        _cache = cache;
        _restorePublisher = restorePublisher;
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
}

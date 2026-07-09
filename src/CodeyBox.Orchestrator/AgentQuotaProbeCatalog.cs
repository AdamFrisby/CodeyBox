using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class AgentQuotaProbeCatalog
{
    public static IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> BuildSubscriptionProbeKindLookup(IEnumerable<IAgentQuotaProbe> probes)
    {
        return probes
            .Where(UsesSubscriptionProbeKindLookup)
            .ToDictionary(p => p.Kind);
    }

    private static bool UsesSubscriptionProbeKindLookup(IAgentQuotaProbe probe) =>
        probe is not PayPerApiQuotaProbe and not NullQuotaProbe;
}

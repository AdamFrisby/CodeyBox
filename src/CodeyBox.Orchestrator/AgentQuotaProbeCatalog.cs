using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class AgentQuotaProbeCatalog
{
    public static IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> BuildKindLookup(IEnumerable<IAgentQuotaProbe> probes)
    {
        return probes
            .Where(UsesKindLookup)
            .ToDictionary(p => p.Kind);
    }

    private static bool UsesKindLookup(IAgentQuotaProbe probe) =>
        probe is not PayPerApiQuotaProbe and not NullQuotaProbe;
}

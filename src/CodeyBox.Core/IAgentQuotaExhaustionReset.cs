namespace CodeyBox.Core;

/// <summary>
/// Optional capability for <see cref="IAgentQuotaProbe"/> implementations
/// that keep runtime exhaustion overrides written by
/// <see cref="IAgentQuotaProbe.MarkExhaustedAsync"/> (e.g. a synthetic 0%
/// gate after a live 429). The operator reset path
/// (<c>POST /admin/agent/{name}/reset</c>) calls this so a cached verdict can
/// be cleared without restarting the process. Probes without runtime gates
/// (the default no-op <c>MarkExhaustedAsync</c>) do not implement this.
/// </summary>
public interface IAgentQuotaExhaustionReset
{
    /// <summary>
    /// Clears runtime exhaustion overrides for <paramref name="kind"/> (all
    /// route keys / models / credential scopes belonging to the agent).
    /// Returns the number of overrides removed. Must never throw.
    /// </summary>
    int ClearRuntimeExhaustion(AgentKind kind);
}

using CodeyBox.Core;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Placeholder quota probe for crock. Always returns Unknown / Permanent
/// because the cost/usage signal for the Anthropic Message Batches API path
/// crock rides has not been wired into a programmatic endpoint here yet.
///
/// <para>The router's <c>QuotaUnknownPolicy</c> (default
/// <c>UseObservedFailures</c>) gates dispatch off observed failure history
/// for this agent until the dependent follow-up replaces this with a real
/// cost / usage probe.</para>
///
/// <para>Registering an explicit per-agent instance instead of relying on
/// a null fallback keeps the DI graph symmetrical with the other agents and
/// gives future contributors a clear seam to swap in the live implementation.</para>
/// </summary>
public sealed class CrockQuotaProbe : IAgentQuotaProbe
{
    public AgentKind Kind => AgentKind.Crock;

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
    {
        _ = member;
        return Task.FromResult(AgentQuotaSnapshot.UnknownSnapshot(
            QuotaUnknownReason.Permanent, "no probe endpoint"));
    }
}

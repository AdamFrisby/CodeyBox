namespace CodeyBox.Core;

/// <summary>
/// Focused quota-availability gate for consumers that need to know whether a
/// probed subscription member is currently routable without depending on the
/// full router implementation.
/// </summary>
public interface IAgentQuotaGate
{
    bool Allows(AgentMembership member, AgentQuotaSnapshot snapshot, DateTimeOffset nowUtc);
}

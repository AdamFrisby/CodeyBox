namespace CodeyBox.Core;

public readonly record struct AgentQuotaMemberKey(string RouteKey, AgentKind Agent, string ModelId)
{
    public static AgentQuotaMemberKey From(AgentMembership member) =>
        new(member.RouteKey, member.Agent, member.ModelId ?? string.Empty);
}

namespace CodeyBox.Core;

public readonly record struct AgentQuotaMemberKey
{
    public AgentQuotaMemberKey(string routeKey, AgentKind agent, string? modelId)
    {
        RouteKey = NormalizeRouteKey(routeKey);
        Agent = agent;
        ModelId = modelId ?? string.Empty;
    }

    public string RouteKey { get; }
    public AgentKind Agent { get; }
    public string ModelId { get; }

    public static AgentQuotaMemberKey From(AgentMembership member) =>
        new(member.RouteKey, member.Agent, member.ModelId);

    public static string NormalizeRouteKey(string? routeKey) =>
        (routeKey ?? string.Empty).Trim().ToLowerInvariant();
}

using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal static class DirectAgentMembership
{
    public const int DefaultQualityScore = 100;

    public static bool IsDirectRoute(WorkItem item, Project? project) =>
        string.IsNullOrWhiteSpace(item.AgentClassId ?? project?.DefaultAgentClass);

    public static AgentMembership? TryCreate(WorkItem item, Project? project)
    {
        var agent = item.Agent ?? project?.DefaultAgent;
        if (agent is null)
            return null;

        return new AgentMembership
        {
            Agent = agent.Value,
            InstanceId = item.AgentInstanceId,
            ModelId = item.ModelId,
            ReasoningMode = item.ReasoningMode,
            Billing = AgentBilling.Subscription,
            QualityScore = DefaultQualityScore,
        };
    }

    public static bool SameQuotaBucket(AgentMembership left, AgentMembership right) =>
        AgentQuotaMemberKey.From(left).Equals(AgentQuotaMemberKey.From(right));
}

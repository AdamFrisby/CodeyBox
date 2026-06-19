using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true when any registered agent is excluded because a runtime
/// invocation emitted an interactive auth/login prompt.
/// </summary>
public sealed class AgentAuthRequiredCondition : ICondition, IDisposable
{
    public const string Condition = "agent_auth_required";

    private readonly IAgentAuthRequiredAvailabilityReader _availability;
    private readonly IAgentRegistry _agents;

    public string Id => Condition;

    public AgentAuthRequiredCondition(
        IAgentAuthRequiredAvailabilityReader availability,
        IAgentRegistry agents)
    {
        _availability = availability;
        _agents = agents;
    }

    public Task<bool> EvaluateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(GetAuthRequiredAgents(_availability, _agents).Count > 0);
    }

    internal static IReadOnlyList<(AgentKind Agent, string Reason)> GetAuthRequiredAgents(
        IAgentAuthRequiredAvailabilityReader availability,
        IAgentRegistry agents)
    {
        var matches = new List<(AgentKind Agent, string Reason)>();
        foreach (var agent in agents.Available)
        {
            var current = availability.GetAuthRequiredAvailability(agent);
            if (current.AuthRequired)
                matches.Add((agent, current.Reason ?? "auth required"));
        }

        return matches;
    }

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the agent_auth_required condition.
/// </summary>
public sealed class AgentAuthRequiredNotificationBuilder : INotificationBuilder, IConditionAwareBuilder
{
    private readonly IAgentAuthRequiredAvailabilityReader _availability;
    private readonly IAgentRegistry _agents;

    public string ConditionId => AgentAuthRequiredCondition.Condition;

    public AgentAuthRequiredNotificationBuilder(
        IAgentAuthRequiredAvailabilityReader availability,
        IAgentRegistry agents)
    {
        _availability = availability;
        _agents = agents;
    }

    public Notification Build(DateTimeOffset evaluatedAt)
    {
        var matches = AgentAuthRequiredCondition.GetAuthRequiredAgents(_availability, _agents);
        var agentNames = matches.Select(m => m.Agent.Value).ToArray();
        var summary = agentNames.Length == 0
            ? "An agent was marked unauthenticated."
            : $"Agent authentication is required for: {string.Join(", ", agentNames)}.";
        var reasonLines = matches.Count == 0
            ? "No current auth-required reason was available when this notification was built."
            : string.Join(Environment.NewLine, matches.Select(m => $"- {m.Agent.Value}: {m.Reason}"));

        return new Notification
        {
            ConditionId = AgentAuthRequiredCondition.Condition,
            Title = agentNames.Length == 1
                ? $"Agent {agentNames[0]} is unauthenticated"
                : "One or more agents are unauthenticated",
            Summary = summary,
            Body = $"As of {evaluatedAt:R}, CodeyBox has benched agent CLI dispatch because an interactive login prompt was detected." +
                   Environment.NewLine + Environment.NewLine +
                   reasonLines +
                   Environment.NewLine + Environment.NewLine +
                   "Re-authenticate the affected agent on the orchestrator host or update its credential material, then reset the agent availability.",
            Severity = NotificationSeverity.Critical,
            Timestamp = evaluatedAt,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agents"] = string.Join(",", agentNames),
                ["action"] = "reauthenticate_agent",
            },
        };
    }
}

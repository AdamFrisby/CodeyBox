using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Builds and interprets <see cref="WorkItem.QuotaRetryScope"/> keys.
///
/// A scope key identifies the single quota bucket (agent + route + model)
/// that a work item's <see cref="WorkItem.QuotaRetryAttempts"/> were accrued
/// against. The quota-retry budget is per bucket: when the scheduler finds
/// the item routable on a different bucket than the recorded scope, it resets
/// the counter so one exhausted agent cannot consume another agent's budget.
/// Keys are opaque to every other component; only this class constructs them
/// and <see cref="DescribeExhaustedAgent"/> reads the agent back out for
/// operator-facing failure messages.
/// </summary>
internal static class QuotaRetryScope
{
    private const string Prefix = "quota-bucket:v1";

    /// <summary>
    /// Builds the scope key for the class member the item is currently
    /// routable on.
    /// </summary>
    public static string ForAdmission(QuotaRetryAdmissionPoolKey pool)
    {
        var agent = (pool.Agent.Value ?? string.Empty).Trim().ToLowerInvariant();
        return $"{Prefix}:agent={agent};route={pool.RouteKey};model={pool.ModelId}";
    }

    /// <summary>
    /// Builds the scope key for a directly-routed item (no agent class).
    /// Returns null when no direct agent can be resolved.
    /// </summary>
    public static string? ForDirectRoute(WorkItem item, Project? project)
    {
        if (!DirectAgentMembership.IsDirectRoute(item, project))
            return null;

        var member = DirectAgentMembership.TryCreate(item, project);
        if (member is null)
            return null;

        return ForAdmission(QuotaRetryAdmissionPoolKey.FromMembership(member));
    }

    /// <summary>
    /// Names the agent whose quota exhaustion owns the current budget, for
    /// operator-facing failure messages. Falls back to the item's current
    /// agent (which may be a healthy fallback peer) only when the scope is
    /// missing or unparseable — and to "unknown" when neither is available.
    /// The exhausted owner is preferred because the last-routed agent can be
    /// a healthy fallback that had quota available, which misattributes the
    /// failure and costs diagnosis time.
    /// </summary>
    public static string DescribeExhaustedAgent(string? scope, AgentKind? currentAgent)
    {
        var owner = TryParseAgent(scope);
        if (!string.IsNullOrWhiteSpace(owner))
            return owner!;

        var current = currentAgent?.Value?.Trim();
        return string.IsNullOrWhiteSpace(current) ? "unknown" : current!;
    }

    private static string? TryParseAgent(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope) || !scope.StartsWith(Prefix, StringComparison.Ordinal))
            return null;

        foreach (var segment in scope.Substring(Prefix.Length).Split(';'))
        {
            var trimmed = segment.Trim().TrimStart(':');
            if (trimmed.StartsWith("agent=", StringComparison.Ordinal))
            {
                var agent = trimmed.Substring("agent=".Length).Trim();
                return string.IsNullOrWhiteSpace(agent) ? null : agent;
            }
        }

        return null;
    }
}

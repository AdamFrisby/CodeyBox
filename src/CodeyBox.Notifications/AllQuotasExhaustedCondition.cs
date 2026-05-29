using CodeyBox.Core;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true when every configured agent with a subscription quota probe
/// reports available percentage below <paramref name="minQuotaPct"/>.
/// Clears when at least one agent reports above the threshold.
/// </summary>
public sealed class AllQuotasExhaustedCondition : ICondition, IDisposable
{
    private readonly IEnumerable<IAgentQuotaProbe> _probes;
    private readonly double _minQuotaPct;
    private readonly IAgentRegistry _agentRegistry;

    public string Id => "all_quotas_exhausted";

    public AllQuotasExhaustedCondition(
        IEnumerable<IAgentQuotaProbe> probes,
        double minQuotaPct,
        IAgentRegistry agentRegistry)
    {
        _probes = probes;
        _minQuotaPct = minQuotaPct;
        _agentRegistry = agentRegistry;
    }

    public async Task<bool> EvaluateAsync(CancellationToken ct)
    {
        var probes = _probes
            .Where(p => _agentRegistry.Available.Contains(p.Kind))
            .ToList();

        if (probes.Count == 0)
            return false;

        foreach (var probe in probes)
        {
            try
            {
                var snapshot = await probe.GetAvailabilityAsync(
                    new AgentMembership
                    {
                        Agent = probe.Kind,
                        Billing = AgentBilling.Subscription,
                        QualityScore = 100,
                    }, ct);

                if (snapshot.AvailablePct < 0 || snapshot.AvailablePct >= _minQuotaPct)
                    return false;
            }
            catch
            {
                return false;
            }
        }

        return probes.Count > 0;
    }

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the all_quotas_exhausted condition.
/// </summary>
public sealed class AllQuotasExhaustedNotificationBuilder : INotificationBuilder
{
    private readonly IEnumerable<IAgentQuotaProbe> _probes;
    private readonly double _minQuotaPct;

    public AllQuotasExhaustedNotificationBuilder(
        IEnumerable<IAgentQuotaProbe> probes,
        double minQuotaPct)
    {
        _probes = probes;
        _minQuotaPct = minQuotaPct;
    }

    public Notification Build(DateTimeOffset evaluatedAt)
    {
        var agentNames = _probes
            .Select(p => p.Kind.Value)
            .ToList();

        return new Notification
        {
            ConditionId = "all_quotas_exhausted",
            Title = $"All agent quotas exhausted (threshold: {_minQuotaPct:F0}%)",
            Summary = $"Every configured subscription agent ({string.Join(", ", agentNames)}) " +
                      $"is below the {_minQuotaPct:F0}% minimum threshold.",
            Body = $"As of {evaluatedAt:R}, all subscription agent quotas are below " +
                   $"the configured minimum ({_minQuotaPct:F0}%). " +
                   $"Agents monitored: {string.Join(", ", agentNames)}. " +
                   "The orchestrator will not dispatch new work items until at least one " +
                   "agent recovers.",
            Severity = NotificationSeverity.Critical,
            Timestamp = evaluatedAt,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["minQuotaPct"] = _minQuotaPct.ToString("F0"),
                ["agents"] = string.Join(",", agentNames),
            },
        };
    }
}

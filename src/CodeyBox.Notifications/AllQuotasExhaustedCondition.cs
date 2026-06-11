using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Notifications;

/// <summary>
/// Evaluates true when every configured agent with a subscription quota probe
/// is denied by the shared quota gate. Clears when at least one agent is
/// routable. Routes through <see cref="IAgentQuotaGate.AllowsAsync"/> so the
/// gate's observed-failure breaker (consulted internally) gates this evaluation
/// the same way it gates dispatch — without it the condition could report
/// quotas available while every dispatch candidate is blocked by the breaker.
/// </summary>
public sealed class AllQuotasExhaustedCondition : ICondition, IDisposable
{
    private readonly IEnumerable<IAgentQuotaProbe> _probes;
    private readonly IAgentQuotaGate _quotaGate;
    private readonly IAgentRegistry _agentRegistry;
    private readonly ILogger<AllQuotasExhaustedCondition> _log;

    public string Id => "all_quotas_exhausted";

    public AllQuotasExhaustedCondition(
        IEnumerable<IAgentQuotaProbe> probes,
        IAgentQuotaGate quotaGate,
        IAgentRegistry agentRegistry,
        ILogger<AllQuotasExhaustedCondition> log)
    {
        _probes = probes;
        _quotaGate = quotaGate;
        _agentRegistry = agentRegistry;
        _log = log;
    }

    public async Task<bool> EvaluateAsync(CancellationToken ct)
    {
        var probes = _probes
            .Where(p => _agentRegistry.Available.Contains(p.Kind))
            .ToList();

        if (probes.Count == 0)
            return false;

        var now = DateTimeOffset.UtcNow;
        foreach (var probe in probes)
        {
            var member = new AgentMembership
            {
                Agent = probe.Kind,
                Billing = AgentBilling.Subscription,
                QualityScore = 100,
            };

            AgentQuotaSnapshot snapshot;
            try
            {
                snapshot = await probe.GetAvailabilityAsync(member, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "AllQuotasExhaustedCondition: probe {AgentKind} failed; treating as below threshold", probe.Kind);
                continue;
            }

            if (await _quotaGate.AllowsAsync(member, snapshot, now, ct))
                return false;
        }

        return true;
    }

    public void Dispose() { }
}

/// <summary>
/// Notification builder for the all_quotas_exhausted condition.
/// </summary>
public sealed class AllQuotasExhaustedNotificationBuilder : INotificationBuilder, IConditionAwareBuilder
{
    public string ConditionId => "all_quotas_exhausted";

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
            Title = "All agent quotas denied by quota gate",
            Summary = $"Every configured subscription agent ({string.Join(", ", agentNames)}) " +
                      "is currently denied by the effective quota gate.",
            Body = $"As of {evaluatedAt:R}, all subscription agent quotas are denied " +
                   "by the effective gate policy, including any ramped, per-window, " +
                   "or per-agent floors. " +
                   $"Agents monitored: {string.Join(", ", agentNames)}. " +
                   "The orchestrator will not dispatch new work items until at least one " +
                   "agent recovers.",
            Severity = NotificationSeverity.Critical,
            Timestamp = evaluatedAt,
            Fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["globalMinQuotaPct"] = _minQuotaPct.ToString("F0"),
                ["gate"] = "effective",
                ["agents"] = string.Join(",", agentNames),
            },
        };
    }
}

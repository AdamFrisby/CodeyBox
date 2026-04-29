using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Resolves which agent member to use for a work item by probing quota across
/// the members of the requested <see cref="AgentClass"/> in preference order.
///
/// Resolution algorithm (per pickup attempt):
/// <list type="number">
///   <item>Determine the class: <see cref="WorkItem.AgentClassId"/> → <see cref="Project.DefaultAgentClass"/> → null (no routing).</item>
///   <item>For each member in preference order: probe quota via the registered <see cref="IAgentQuotaProbe"/>.</item>
///   <item>PayPerApi members are handled by <see cref="PayPerApiQuotaProbe"/> (always 100%).</item>
///   <item>Subscription members with no registered probe fall back to <see cref="NullQuotaProbe"/> (unknown → fail-open).</item>
///   <item>Pick the first member where AvailablePct &lt; 0 (unknown → fail-open) or AvailablePct ≥ MinQuotaPct.</item>
///   <item>If no member qualifies and at least one Subscription member exists → ShouldWait=true, re-enqueue later.</item>
///   <item>If only PayPerApi members exist → fire the first member regardless (exceeding quota costs money; it never fails the call).</item>
/// </list>
///
/// Called on every pickup attempt; all quota reads go through cached
/// <see cref="IAgentQuotaProbe"/> implementations to keep the hot path cheap.
/// </summary>
public sealed class AgentClassRouter
{
    private readonly IReadOnlyDictionary<string, AgentClass> _catalog;
    private readonly IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> _probesByKind;
    private readonly IAgentQuotaProbe _payPerApiProbe;
    private readonly IAgentQuotaProbe _nullProbe;
    private readonly QuotaRouterOptions _opts;
    private readonly ILogger<AgentClassRouter> _log;

    public AgentClassRouter(
        IReadOnlyList<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        QuotaRouterOptions opts,
        ILogger<AgentClassRouter> log)
    {
        _catalog = catalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        var probeList = probes.ToList();
        // PayPerApiQuotaProbe and NullQuotaProbe are selected by billing type, not kind;
        // exclude them from the kind-based lookup to avoid polluting the dictionary.
        _probesByKind = probeList
            .Where(p => p is not PayPerApiQuotaProbe and not NullQuotaProbe)
            .ToDictionary(p => p.Kind);
        _payPerApiProbe = probeList.OfType<PayPerApiQuotaProbe>().FirstOrDefault() ?? new PayPerApiQuotaProbe();
        _nullProbe = probeList.OfType<NullQuotaProbe>().FirstOrDefault() ?? new NullQuotaProbe();
        _opts = opts;
        _log = log;

    }

    /// <summary>
    /// Resolves the agent to use for <paramref name="item"/>.
    /// Returns <see cref="AgentRoutingDecision.Chosen"/> = null and
    /// <see cref="AgentRoutingDecision.ShouldWait"/> = false when no agent
    /// class applies — the caller falls back to direct agent pick with no
    /// quota probe, preserving legacy behaviour exactly.
    /// </summary>
    public async Task<AgentRoutingDecision> ResolveAsync(
        WorkItem item, Project? project, CancellationToken ct)
    {
        var classId = item.AgentClassId ?? project?.DefaultAgentClass;
        if (classId is null)
            return new AgentRoutingDecision { Reason = "no agent class configured" };

        if (!_catalog.TryGetValue(classId, out var agentClass))
        {
            _log.LogWarning(
                "Work item {Id}: unknown agent class '{ClassId}'; falling through to direct agent pick",
                item.Id, classId);
            return new AgentRoutingDecision { Reason = $"unknown agent class '{classId}'" };
        }

        var hasSubscription = agentClass.Members.Any(m => m.Billing == AgentBilling.Subscription);

        foreach (var member in agentClass.Members)
        {
            var snapshot = await ProbeAsync(member, ct);

            // Include notes so PayPerApi synthetic probes are distinguishable from real
            // measurements in the audit log.
            AuditLog.QuotaProbed(member.Agent, classId, snapshot.AvailablePct, snapshot.ResetAt, snapshot.Notes);

            // AvailablePct < 0 means unknown → fail-open (treat as available).
            if (snapshot.AvailablePct < 0 || snapshot.AvailablePct >= _opts.MinQuotaPct)
            {
                _log.LogInformation(
                    "Work item {Id}: routed to {Agent}/{Billing} (available={Avail:F1}%)",
                    item.Id, member.Agent, member.Billing, snapshot.AvailablePct);
                return new AgentRoutingDecision
                {
                    Chosen = member,
                    Reason = $"{member.Agent}/{member.Billing}: {snapshot.AvailablePct:F1}% available",
                };
            }
        }

        // No member is above the threshold.
        if (hasSubscription)
        {
            AuditLog.QuotaRouterWaiting(classId, item.Id, _opts.QuotaRecheckInterval);
            return new AgentRoutingDecision
            {
                ShouldWait = true,
                SuggestedRecheckIn = _opts.QuotaRecheckInterval,
                Reason = $"all members of class '{classId}' are below {_opts.MinQuotaPct}% threshold",
            };
        }

        // Only PayPerApi members reached here — this path is unreachable in
        // normal operation (PayPerApi always returns 100%), but guard against
        // unusual custom probes returning low values for PayPerApi members.
        var fallback = agentClass.Members[0];
        _log.LogWarning(
            "Work item {Id}: all members below threshold but class '{ClassId}' has no Subscription members; firing {Agent} anyway",
            item.Id, classId, fallback.Agent);
        return new AgentRoutingDecision
        {
            Chosen = fallback,
            Reason = "only PayPerApi members — firing despite apparent low quota",
        };
    }

    private Task<AgentQuotaSnapshot> ProbeAsync(AgentMembership member, CancellationToken ct)
    {
        if (member.Billing == AgentBilling.PayPerApi)
            return _payPerApiProbe.GetAvailabilityAsync(member, ct);
        if (_probesByKind.TryGetValue(member.Agent, out var probe))
            return probe.GetAvailabilityAsync(member, ct);
        return _nullProbe.GetAvailabilityAsync(member, ct);
    }
}

/// <summary>Routing decision returned by <see cref="AgentClassRouter"/>.</summary>
public sealed record AgentRoutingDecision
{
    /// <summary>
    /// The chosen member, or null when no agent class was configured (the caller
    /// should fall through to its direct agent pick — no quota probe happened).
    /// </summary>
    public AgentMembership? Chosen { get; init; }

    /// <summary>
    /// True when all subscription-billed members are exhausted and the item
    /// should be re-enqueued after <see cref="SuggestedRecheckIn"/>.
    /// </summary>
    public bool ShouldWait { get; init; }

    /// <summary>Suggested delay before re-attempting pickup.</summary>
    public TimeSpan SuggestedRecheckIn { get; init; }

    /// <summary>Human-readable reason for log output.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Configuration for the quota-aware agent class router.
/// Bound from <c>CodeyBox:QuotaRouter</c>.
/// </summary>
public sealed class QuotaRouterOptions
{
    /// <summary>
    /// Minimum percentage of quota remaining for a member to be considered
    /// available. Members below this threshold are skipped in favour of the
    /// next class member. Default 10.
    /// </summary>
    public double MinQuotaPct { get; set; } = 10.0;

    /// <summary>
    /// How long to wait before re-probing when all subscription-billed members
    /// are exhausted. Default 5 minutes.
    /// </summary>
    public TimeSpan QuotaRecheckInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a quota probe result is cached before a new HTTP call is made.
    /// Shared across all probe implementations via constructor injection.
    /// Default 60 seconds.
    /// </summary>
    public TimeSpan QuotaCacheTtl { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Quota probe for <see cref="AgentBilling.PayPerApi"/> members. Always returns
/// <c>AvailablePct = 100</c> — pay-per-API usage costs money but never hard-fails
/// the call, so the orchestrator never gates on it.
/// </summary>
public sealed class PayPerApiQuotaProbe : IAgentQuotaProbe
{
    public AgentKind Kind => new("pay-per-api");

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => Task.FromResult(new AgentQuotaSnapshot
        {
            AvailablePct = 100.0,
            Notes = "PayPerApi — never gated",
        });
}

/// <summary>
/// Fallback quota probe used when no probe is registered for an agent kind.
/// Returns <c>AvailablePct = -1</c> (unknown) so the router fails open rather
/// than blocking work items.
/// </summary>
public sealed class NullQuotaProbe : IAgentQuotaProbe
{
    public AgentKind Kind => new("null");

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => Task.FromResult(new AgentQuotaSnapshot
        {
            AvailablePct = -1,
            Notes = $"no probe registered for {member.Agent}",
        });
}

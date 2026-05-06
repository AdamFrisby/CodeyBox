using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Resolves which agent member to use for a work item by applying a scalar
/// quality-score model across the members of the requested <see cref="AgentClass"/>.
///
/// Resolution algorithm (per pickup attempt):
/// <list type="number">
///   <item>Determine the class: <see cref="WorkItem.AgentClassId"/> → <see cref="Project.DefaultAgentClass"/> → null (no routing).</item>
///   <item>Filter members to those whose base <see cref="AgentMembership.QualityScore"/> ≥ <see cref="WorkItem.MinModelScore"/> (eligibility gate; TOD modifiers do not affect the floor check).</item>
///   <item>If no member is eligible, fail with <c>ROUTING_NO_ELIGIBLE</c> — no silent downgrade.</item>
///   <item>Compute each eligible member's effective score: base + sum of applicable time-of-day modifiers.</item>
///   <item>Sort descending by effective score; ties broken by Subscription before PayPerApi, then original config order.</item>
///   <item>Probe quota in sorted order; pick the first member at or above <see cref="QuotaRouterOptions.MinQuotaPct"/>.</item>
///   <item>PayPerApi members use <see cref="PayPerApiQuotaProbe"/> (always 100%).</item>
///   <item>Subscription members with no registered probe fall back to <see cref="NullQuotaProbe"/> and follow the configured unknown policy.</item>
///   <item>If all eligible subscription members are exhausted → ShouldWait=true, re-enqueue later.</item>
///   <item>If only PayPerApi eligible members remain → fire the first regardless (costs money; never hard-fails).</item>
/// </list>
///
/// Called on every pickup attempt; all quota reads go through cached
/// <see cref="IAgentQuotaProbe"/> implementations to keep the hot path cheap.
/// TOD windows are pre-parsed at construction time so evaluation is allocation-free.
/// <see cref="TimeProvider"/> is the clock source; inject a fake for tests.
/// </summary>
public sealed class AgentClassRouter
{
    private readonly IReadOnlyDictionary<string, AgentClass> _catalog;
    private readonly IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> _probesByKind;
    private readonly IAgentQuotaProbe _payPerApiProbe;
    private readonly IAgentQuotaProbe _nullProbe;
    private readonly QuotaRouterOptions _opts;
    private readonly ILogger<AgentClassRouter> _log;
    private readonly TimeProvider _time;
    private readonly IQuotaFailureStore? _quotaFailures;
    // Pre-parsed TOD modifiers: evaluated on every pickup, zero-alloc.
    private readonly IReadOnlyList<ParsedTodModifier> _todModifiers;

    public AgentClassRouter(
        IReadOnlyList<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        QuotaRouterOptions opts,
        ILogger<AgentClassRouter> log,
        TimeProvider? timeProvider = null,
        IReadOnlyList<ParsedTodModifier>? todModifiers = null,
        IQuotaFailureStore? quotaFailures = null)
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
        _time = timeProvider ?? TimeProvider.System;
        _todModifiers = todModifiers ?? [];
        _quotaFailures = quotaFailures;
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

        // Step 1: filter by base QualityScore — TOD modifiers do not affect eligibility.
        var eligible = agentClass.Members
            .Select((m, idx) => (Member: m, ConfigIndex: idx))
            .Where(x => x.Member.QualityScore >= item.MinModelScore)
            .ToList();

        if (eligible.Count == 0)
        {
            var best = agentClass.Members.Count > 0
                ? agentClass.Members.Max(m => m.QualityScore)
                : 0;
            var reason = $"ROUTING_NO_ELIGIBLE: no member of class '{classId}' meets " +
                         $"MinModelScore={item.MinModelScore} (best available={best})";
            _log.LogError("Work item {Id}: {Reason}", item.Id, reason);
            // Emit scored audit event so below-floor rejects appear in the audit log.
            var nowUtcFloor = _time.GetUtcNow();
            var belowFloor = agentClass.Members
                .Select(m => (
                    Agent: m.Agent,
                    ModelId: m.ModelId,
                    EffectiveScore: m.QualityScore + ComputeTodModifier(m.Agent, nowUtcFloor),
                    RejectReason: $"below floor ({m.QualityScore} < {item.MinModelScore})"))
                .ToList();
            AuditLog.QuotaRouterNoEligible(item.Id, classId, item.MinModelScore, belowFloor);
            return new AgentRoutingDecision { Reason = reason, NoEligibleMembers = true };
        }

        // Step 2: compute effective scores (base + TOD modifier).
        var nowUtc = _time.GetUtcNow();
        var scored = eligible.Select(x => new ScoredMember(
            Member: x.Member,
            BaseScore: x.Member.QualityScore,
            EffectiveScore: x.Member.QualityScore + ComputeTodModifier(x.Member.Agent, nowUtc),
            ConfigIndex: x.ConfigIndex
        )).ToList();

        // Step 3: sort — highest effective score first; ties: Subscription before PayPerApi, then config order.
        var sorted = scored
            .OrderByDescending(x => x.EffectiveScore)
            .ThenBy(x => x.Member.Billing == AgentBilling.Subscription ? 0 : 1)
            .ThenBy(x => x.ConfigIndex)
            .ToList();

        // Rejected members accumulate for the audit event.
        var rejected = new List<(AgentKind Agent, string? ModelId, int EffectiveScore, string RejectReason)>();

        // Also track which below-floor members were filtered out.
        foreach (var m in agentClass.Members)
        {
            if (m.QualityScore < item.MinModelScore)
            {
                var eff = m.QualityScore + ComputeTodModifier(m.Agent, nowUtc);
                rejected.Add((m.Agent, m.ModelId, eff, $"below floor ({m.QualityScore} < {item.MinModelScore})"));
            }
        }

        var hasSubscription = sorted.Any(x => x.Member.Billing == AgentBilling.Subscription);

        // Step 4: probe quota in sorted order; pick the first viable member.
        foreach (var entry in sorted)
        {
            var member = entry.Member;
            if (member.Billing == AgentBilling.Subscription
                && _quotaFailures is not null
                && await _quotaFailures.HasRecentAsync(member.Agent, member.ModelId, _opts.ObservedFailureWindow, _time.GetUtcNow(), ct))
            {
                rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, "recent quota-shaped failure"));
                continue;
            }

            var snapshot = await ProbeAsync(member, ct);
            var quota = ResolveMemberQuota(snapshot, member);

            AuditLog.QuotaProbed(member.Agent, classId, quota.AvailablePct, quota.ResetAt, snapshot.Notes);

            var gate = await EvaluateGateAsync(member, quota.AvailablePct, ct);
            if (gate.Allow)
            {
                // Mark all remaining sorted entries as "ranked lower" for the audit event.
                foreach (var other in sorted.Where(x => x != entry))
                    rejected.Add((other.Member.Agent, other.Member.ModelId, other.EffectiveScore, "ranked lower"));

                var modDesc = DescribeModifiers(member.Agent, nowUtc);
                AuditLog.QuotaRouterScored(
                    item.Id, classId,
                    member.Agent, member.ModelId,
                    entry.BaseScore, entry.EffectiveScore, modDesc,
                    rejected);

                _log.LogInformation(
                    "Work item {Id}: routed to {Agent}/{Billing} model={Model} " +
                    "baseScore={Base} effectiveScore={Eff} (available={Avail:F1}%)",
                    item.Id, member.Agent, member.Billing,
                    member.ModelId ?? "(default)", entry.BaseScore, entry.EffectiveScore,
                    quota.AvailablePct);

                return new AgentRoutingDecision
                {
                    Chosen = member,
                    Reason = $"{member.Agent}/{member.Billing} score={entry.EffectiveScore}: {quota.AvailablePct:F1}% available",
                };
            }

            rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, gate.Reason));
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

        // Only PayPerApi members reached here — unreachable in normal operation
        // (PayPerApi always returns 100%), but guard against unusual custom probes.
        var fallback = sorted[0].Member;
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

    private async Task<QuotaGateDecision> EvaluateGateAsync(AgentMembership member, double availablePct, CancellationToken ct)
    {
        if (availablePct >= _opts.MinQuotaPct)
            return new QuotaGateDecision(true, "quota available");

        if (availablePct >= 0)
            return new QuotaGateDecision(false, "quota exhausted");

        return _opts.UnknownPolicy switch
        {
            QuotaUnknownPolicy.FailOpen => new QuotaGateDecision(true, "quota unknown; fail-open"),
            QuotaUnknownPolicy.FailCautious => new QuotaGateDecision(false, "quota unknown; fail-cautious"),
            _ => await EvaluateObservedFailuresAsync(member, ct),
        };
    }

    private async Task<QuotaGateDecision> EvaluateObservedFailuresAsync(AgentMembership member, CancellationToken ct)
    {
        if (_quotaFailures is not null
            && await _quotaFailures.HasRecentAsync(member.Agent, member.ModelId, _opts.ObservedFailureWindow, _time.GetUtcNow(), ct))
            return new QuotaGateDecision(false, "quota unknown; recent quota-shaped failure");

        return new QuotaGateDecision(true, "quota unknown; no recent quota-shaped failure");
    }

    internal static EffectiveQuota ResolveMemberQuota(AgentQuotaSnapshot snapshot, AgentMembership member)
    {
        if (!string.IsNullOrWhiteSpace(member.ModelId)
            && snapshot.PerModel.TryGetValue(member.ModelId, out var modelQuota))
        {
            return new EffectiveQuota(modelQuota.AvailablePct, modelQuota.ResetAt, modelQuota.Window);
        }

        return new EffectiveQuota(snapshot.AvailablePct, snapshot.ResetAt, null);
    }

    private int ComputeTodModifier(AgentKind agent, DateTimeOffset nowUtc)
    {
        var total = 0;
        foreach (var mod in _todModifiers)
        {
            if (mod.Agent != agent) continue;
            foreach (var window in mod.Windows)
            {
                if (IsInWindow(window, nowUtc))
                    total += mod.Modifier;
            }
        }
        return total;
    }

    private string DescribeModifiers(AgentKind agent, DateTimeOffset nowUtc)
    {
        var parts = new List<string>();
        foreach (var mod in _todModifiers)
        {
            if (mod.Agent != agent) continue;
            foreach (var window in mod.Windows)
            {
                if (IsInWindow(window, nowUtc))
                    parts.Add($"{mod.Modifier:+0;-0}(tod)");
            }
        }
        return parts.Count == 0 ? "none" : string.Join(",", parts);
    }

    private static bool IsInWindow(ParsedTimeWindow window, DateTimeOffset nowUtc)
    {
        if (!window.Days.Contains(nowUtc.DayOfWeek)) return false;
        var t = nowUtc.TimeOfDay;
        // Wrap-around window (e.g. 22:00–02:00): active outside the gap.
        return window.Start <= window.End
            ? t >= window.Start && t < window.End
            : t >= window.Start || t < window.End;
    }

    private sealed record ScoredMember(
        AgentMembership Member,
        int BaseScore,
        int EffectiveScore,
        int ConfigIndex);

    private sealed record QuotaGateDecision(bool Allow, string Reason);
}

public sealed record EffectiveQuota(double AvailablePct, DateTimeOffset? ResetAt, string? Window);

/// <summary>
/// A pre-parsed time-of-day modifier entry, built once at startup from
/// <c>CodeyBox:AgentScoreModifiers:ByTimeOfDay</c> config.
/// </summary>
public sealed record ParsedTodModifier(
    AgentKind Agent,
    int Modifier,
    IReadOnlyList<ParsedTimeWindow> Windows);

/// <summary>
/// A pre-parsed UTC time window. <see cref="Start"/> &gt; <see cref="End"/>
/// indicates a wrap-around window (e.g. 22:00–02:00).
/// </summary>
public sealed record ParsedTimeWindow(
    IReadOnlySet<DayOfWeek> Days,
    TimeSpan Start,
    TimeSpan End);

/// <summary>Routing decision returned by <see cref="AgentClassRouter"/>.</summary>
public sealed record AgentRoutingDecision
{
    /// <summary>
    /// The chosen member, or null when no agent class was configured (the caller
    /// should fall through to its direct agent pick — no quota probe happened),
    /// or when <see cref="NoEligibleMembers"/> is true (item should fail fast).
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

    /// <summary>
    /// True when no class member meets the work item's MinModelScore floor.
    /// The caller must fail the item immediately rather than waiting or routing.
    /// </summary>
    public bool NoEligibleMembers { get; init; }
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

    public QuotaUnknownPolicy UnknownPolicy { get; set; } = QuotaUnknownPolicy.UseObservedFailures;

    public TimeSpan ObservedFailureWindow { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan ObservedFailureRetention { get; set; } = TimeSpan.FromMinutes(30);
}

public enum QuotaUnknownPolicy
{
    FailOpen,
    FailCautious,
    UseObservedFailures,
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
/// Returns <c>AvailablePct = -1</c> (unknown); the configured
/// <see cref="QuotaUnknownPolicy"/> decides whether pickup is allowed.
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

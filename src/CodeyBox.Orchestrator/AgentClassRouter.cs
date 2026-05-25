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
///   <item>Probe quota in sorted order; pick the first subscription member at or above <see cref="QuotaRouterOptions.MinQuotaPct"/> with enough projected headroom after in-flight reservations.</item>
///   <item>PayPerApi members use <see cref="PayPerApiQuotaProbe"/> (always 100%).</item>
///   <item>Subscription members with no registered probe fall back to <see cref="NullQuotaProbe"/> and follow the configured unknown policy.</item>
///   <item>If all eligible subscription members are exhausted or lack projected headroom and no PayPerApi fallback is eligible → ShouldWait=true, re-enqueue later.</item>
///   <item>PayPerApi eligible members always fire when reached (costs money; never hard-fails on quota).</item>
/// </list>
///
/// Called on every pickup attempt; all quota reads go through cached
/// <see cref="IAgentQuotaProbe"/> implementations to keep the hot path cheap.
/// TOD windows are pre-parsed at construction time so evaluation is allocation-free.
/// <see cref="TimeProvider"/> is the clock source; inject a fake for tests.
/// </summary>
public sealed class AgentClassRouter : IQuotaResetResolver
{
    private readonly IReadOnlyDictionary<string, AgentClass> _catalog;
    private readonly IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> _probesByKind;
    private readonly IAgentQuotaProbe _payPerApiProbe;
    private readonly IAgentQuotaProbe _nullProbe;
    private readonly QuotaRouterOptions _opts;
    private readonly ILogger<AgentClassRouter> _log;
    private readonly TimeProvider _time;
    private readonly IQuotaFailureStore? _quotaFailures;
    private readonly IQuotaHeadroomManager? _headroomManager;
    // Pre-parsed TOD modifiers: evaluated on every pickup, zero-alloc.
    private readonly IReadOnlyList<ParsedTodModifier> _todModifiers;
    // In-process short-lived exhaustion cache populated by mid-iteration fallback.
    // Keyed by (agent kind, model id ?? ""); value is the UTC instant at which
    // the suppression expires. Survives only the current process lifetime —
    // QuotaRetryScheduler / IQuotaFailureStore cover cross-restart durability.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(AgentKind Agent, string ModelId), DateTimeOffset> _exhausted
        = new();

    public AgentClassRouter(
        IReadOnlyList<AgentClass> catalog,
        IEnumerable<IAgentQuotaProbe> probes,
        QuotaRouterOptions opts,
        ILogger<AgentClassRouter> log,
        TimeProvider? timeProvider = null,
        IReadOnlyList<ParsedTodModifier>? todModifiers = null,
        IQuotaFailureStore? quotaFailures = null,
        IQuotaHeadroomManager? headroomManager = null)
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
        _headroomManager = headroomManager;
    }

    /// <summary>
    /// Resolves the agent to use for <paramref name="item"/>.
    /// Returns <see cref="AgentRoutingDecision.Chosen"/> = null and
    /// <see cref="AgentRoutingDecision.ShouldWait"/> = false when no agent
    /// class applies — the caller falls back to direct agent pick with no
    /// quota probe, preserving legacy behaviour exactly.
    /// </summary>
    public async Task<AgentRoutingDecision> ResolveAsync(
        WorkItem item, Project? project, CancellationToken ct) =>
        await ResolveAsync(item, project, reserve: false, ct);

    /// <summary>
    /// Resolves the agent to use for <paramref name="item"/>, optionally
    /// reserving quota headroom via <see cref="IQuotaHeadroomManager.TryReserveAsync"/>.
    /// </summary>
    public async Task<AgentRoutingDecision> ResolveAsync(
        WorkItem item, Project? project, bool reserve, CancellationToken ct)
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
        DateTimeOffset? earliestRetryAt = null;
        var sawInsufficientHeadroom = false;

        void TrackRetryAt(DateTimeOffset? retryAt)
        {
            if (retryAt is null) return;
            if (earliestRetryAt is null || retryAt.Value < earliestRetryAt.Value)
                earliestRetryAt = retryAt.Value;
        }

        // Step 4: probe quota in sorted order; pick the first viable member.
        foreach (var entry in sorted)
        {
            var member = entry.Member;
            // Mid-iteration fallback may have marked this member exhausted in the
            // current process. Skip it immediately so we don't burn a probe round-trip
            // re-discovering what we just learned from a live failure.
            if (IsExhausted(member, nowUtc))
            {
                rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, "in-process exhaustion cache"));
                continue;
            }
            if (member.Billing == AgentBilling.Subscription && _quotaFailures is not null)
            {
                var observedAt = await _quotaFailures.GetMostRecentAsync(
                    member.Agent, member.ModelId, _opts.ObservedFailureWindow, _time.GetUtcNow(), ct);
                if (observedAt is { } seenAt)
                {
                    var reason = FormatObservedFailureReason(member, seenAt, _time.GetUtcNow());
                    _log.LogInformation("Work item {Id}: rejected: {Reason}", item.Id, reason);
                    rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, reason));
                    continue;
                }
            }

            var snapshot = await ProbeAsync(member, ct);
            var quota = AgentQuotaResolver.ResolveMemberQuota(snapshot, member);

            AuditLog.QuotaProbed(member.Agent, classId, quota.AvailablePct, quota.ResetAt, snapshot.Notes);

            var gate = await EvaluateGateAsync(member, item.ProjectId, quota.AvailablePct, quota.ResetAt, reserve, ct);
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
                    Reason = $"{member.Agent}/{member.Billing} score={entry.EffectiveScore}: {gate.Reason}",
                    Reservation = gate.Reservation,
                };
            }

            TrackRetryAt(gate.RetryAt);
            if (gate.InsufficientHeadroom)
                sawInsufficientHeadroom = true;
            rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, gate.Reason));
        }

        // No member is above the threshold.
        if (hasSubscription)
        {
            var nowForWait = _time.GetUtcNow();
            var suggestedRetryAt = earliestRetryAt ?? nowForWait.Add(_opts.QuotaRecheckInterval);
            var suggestedRecheckIn = suggestedRetryAt > nowForWait
                ? suggestedRetryAt - nowForWait
                : TimeSpan.Zero;
            var waitReason = sawInsufficientHeadroom
                ? $"all members of class '{classId}' lack projected quota headroom"
                : $"all members of class '{classId}' are below {_opts.MinQuotaPct}% threshold";
            AuditLog.QuotaRouterWaiting(classId, item.Id, suggestedRecheckIn, waitReason);
            return new AgentRoutingDecision
            {
                ShouldWait = true,
                SuggestedRecheckIn = suggestedRecheckIn,
                SuggestedRetryAt = suggestedRetryAt,
                Reason = waitReason,
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

    /// <summary>
    /// Looks up the canonical <see cref="AgentMembership"/> for a class member.
    /// Returns null if the class is unknown or no member matches the (agent, model)
    /// pair. Match is exact on Agent and ModelId (treating null and "" as
    /// equivalent).
    /// <para>
    /// Used by the in-iteration fallback wrapper so it can call
    /// <see cref="MarkExhausted"/> and <see cref="IAgentQuotaProbe.MarkExhaustedAsync"/>
    /// with the real catalog record (correct Billing / QualityScore /
    /// ReasoningMode) instead of fabricating a placeholder.
    /// </para>
    /// </summary>
    public AgentMembership? FindMember(string classId, AgentKind agent, string? modelId)
    {
        if (!_catalog.TryGetValue(classId, out var agentClass)) return null;
        var normalisedModel = modelId ?? string.Empty;
        foreach (var m in agentClass.Members)
        {
            if (m.Agent != agent) continue;
            var memberModel = m.ModelId ?? string.Empty;
            if (string.Equals(memberModel, normalisedModel, StringComparison.Ordinal))
                return m;
        }
        return null;
    }

    /// <summary>
    /// Returns the eligible class members for <paramref name="item"/> in the
    /// router's preferred order, *minus* members that this process has marked
    /// exhausted via <see cref="MarkExhausted"/> within the active TTL window.
    ///
    /// <para>
    /// The pipeline calls this when a mid-iteration agent invocation classifies
    /// as <see cref="AgentFailureKind.QuotaExhausted"/>: the next member in the
    /// returned list is the same one a fresh pickup would have routed to, so
    /// the caller can swap runners and retry the iteration without the work
    /// item leaving Working.
    /// </para>
    /// <para>
    /// Returns an empty list when no class is configured, the class has no
    /// members above the work item's <see cref="WorkItem.MinModelScore"/>, or
    /// every eligible member is currently marked exhausted in this process.
    /// </para>
    /// </summary>
    public IReadOnlyList<AgentMembership> OrderedFallbackCandidates(WorkItem item, Project? project)
    {
        var classId = item.AgentClassId ?? project?.DefaultAgentClass;
        if (classId is null || !_catalog.TryGetValue(classId, out var agentClass))
            return [];

        var nowUtc = _time.GetUtcNow();
        // Drop expired exhaustion entries lazily so the cache doesn't grow unbounded
        // across long-running processes. TryRemove(KeyValuePair) only removes when
        // the value still matches what we observed — a concurrent MarkExhausted that
        // refreshed the expiry between the read and the remove is preserved.
        foreach (var key in _exhausted.Keys.ToList())
        {
            if (_exhausted.TryGetValue(key, out var expiry) && expiry <= nowUtc)
                _exhausted.TryRemove(new KeyValuePair<(AgentKind Agent, string ModelId), DateTimeOffset>(key, expiry));
        }

        return agentClass.Members
            .Select((m, idx) => (Member: m, ConfigIndex: idx))
            .Where(x => x.Member.QualityScore >= item.MinModelScore)
            .Where(x => !IsExhausted(x.Member, nowUtc))
            .Select(x => new
            {
                x.Member,
                x.ConfigIndex,
                EffectiveScore = x.Member.QualityScore + ComputeTodModifier(x.Member.Agent, nowUtc),
            })
            .OrderByDescending(x => x.EffectiveScore)
            .ThenBy(x => x.Member.Billing == AgentBilling.Subscription ? 0 : 1)
            .ThenBy(x => x.ConfigIndex)
            .Select(x => x.Member)
            .ToList();
    }

    /// <summary>
    /// Marks a class member as exhausted in this process for <paramref name="ttl"/>
    /// (or until <paramref name="resetAt"/>, whichever is sooner). Subsequent
    /// calls to <see cref="OrderedFallbackCandidates"/> and
    /// <see cref="ResolveAsync"/> will skip the member while the suppression is
    /// active. Always combine with <see cref="IAgentQuotaProbe.MarkExhaustedAsync"/>
    /// so the suppression also reaches any probe-side cache.
    /// </summary>
    public void MarkExhausted(AgentMembership member, TimeSpan ttl, DateTimeOffset? resetAt = null)
    {
        if (ttl <= TimeSpan.Zero) return;
        var nowUtc = _time.GetUtcNow();
        var until = nowUtc + ttl;
        // Cap by resetAt when known — including a past resetAt, which means
        // the agent's own reset hint says we're already through the window.
        if (resetAt is { } reset && reset < until)
            until = reset;
        if (until <= nowUtc) return; // expired already; nothing to suppress
        var key = (member.Agent, member.ModelId ?? string.Empty);
        _exhausted.AddOrUpdate(key, until, (_, existing) => existing > until ? existing : until);
    }

    private bool IsExhausted(AgentMembership member, DateTimeOffset nowUtc)
    {
        var key = (member.Agent, member.ModelId ?? string.Empty);
        return _exhausted.TryGetValue(key, out var expiry) && expiry > nowUtc;
    }

    private Task<AgentQuotaSnapshot> ProbeAsync(AgentMembership member, CancellationToken ct)
    {
        if (member.Billing == AgentBilling.PayPerApi)
            return _payPerApiProbe.GetAvailabilityAsync(member, ct);
        if (_probesByKind.TryGetValue(member.Agent, out var probe))
            return probe.GetAvailabilityAsync(member, ct);
        return _nullProbe.GetAvailabilityAsync(member, ct);
    }

    /// <summary>
    /// Returns the earliest known reset time across all currently-exhausted
    /// subscription members of the class that <paramref name="item"/> would route
    /// to. Used by the quota retry scheduler to set <c>NextQuotaRetryAt</c> to the
    /// soonest moment any class member can plausibly become eligible — rather
    /// than the last-tried agent's reset, which is often the latest reset and
    /// can leave items idle for many hours after an earlier-refilling member
    /// (e.g. claude's 5h cap) clears.
    /// </summary>
    /// <returns>
    /// The minimum <see cref="EffectiveQuota.ResetAt"/> across exhausted members
    /// with a known reset, or <c>null</c> when no useful reset is known (no
    /// class configured, no probes returned a reset, or all members are
    /// available).
    /// </returns>
    public async Task<DateTimeOffset?> ComputeEarliestExhaustedResetAsync(
        WorkItem item, Project? project, CancellationToken ct)
    {
        var classId = item.AgentClassId ?? project?.DefaultAgentClass;
        if (classId is null) return null;
        if (!_catalog.TryGetValue(classId, out var agentClass)) return null;

        DateTimeOffset? earliest = null;
        foreach (var member in agentClass.Members)
        {
            // PayPerApi members never park on quota.
            if (member.Billing == AgentBilling.PayPerApi) continue;

            AgentQuotaSnapshot snapshot;
            try
            {
                snapshot = await ProbeAsync(member, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Quota reset probe failed for {Agent}/{Model}; skipping member while computing earliest exhausted reset",
                    member.Agent.Value,
                    member.ModelId ?? "(default)");
                continue;
            }

            var quota = AgentQuotaResolver.ResolveMemberQuota(snapshot, member);
            // Skip unknown (probe failed / no data) and members above the
            // threshold (would have been chosen by the router and so don't
            // need to gate park-time).
            if (quota.AvailablePct < 0) continue;
            if (quota.AvailablePct >= _opts.MinQuotaPct) continue;
            if (quota.ResetAt is not { } resetAt) continue;

            if (earliest is null || resetAt < earliest.Value)
                earliest = resetAt;
        }
        return earliest;
    }

    private async Task<QuotaGateDecision> EvaluateGateAsync(
        AgentMembership member,
        ProjectId projectId,
        double availablePct,
        DateTimeOffset? resetAt,
        bool reserve,
        CancellationToken ct)
    {
        if (member.Billing == AgentBilling.PayPerApi)
            return new QuotaGateDecision(true, "pay-per-api member is never quota-gated", resetAt);

        var reservedPct = _headroomManager?.GetReservedHeadroomPct(projectId, member.Agent) ?? 0;
        if (availablePct >= 0)
        {
            if (!QuotaRouter.WouldAllow(availablePct, false, _opts, reservedQuotaPct: reservedPct))
                return new QuotaGateDecision(false, "quota exhausted", resetAt);

            if (_headroomManager is null)
                return new QuotaGateDecision(true, $"{availablePct:F1}% available", resetAt);

            var request = new QuotaHeadroomGateRequest(
                projectId,
                member,
                availablePct,
                resetAt,
                MinRemainingPct: _opts.MinQuotaPct);

            var result = await (reserve
                ? _headroomManager.TryReserveAsync(request, ct)
                : _headroomManager.EvaluateAsync(request, ct));

            return new QuotaGateDecision(
                result.Allow,
                result.Reason,
                result.RetryAt,
                result.InsufficientHeadroom,
                result.Reservation);
        }

        if (reservedPct > 0)
        {
            return new QuotaGateDecision(
                false,
                $"quota unknown while {reservedPct:F1}% reserved headroom is pending release",
                resetAt,
                InsufficientHeadroom: true);
        }

        return _opts.UnknownPolicy switch
        {
            QuotaUnknownPolicy.FailOpen => new QuotaGateDecision(true, "quota unknown; fail-open", null),
            QuotaUnknownPolicy.FailCautious => new QuotaGateDecision(false, "quota unknown; fail-cautious", null),
            _ => await EvaluateObservedFailuresAsync(member, ct),
        };
    }

    private async Task<QuotaGateDecision> EvaluateObservedFailuresAsync(AgentMembership member, CancellationToken ct)
    {
        if (_quotaFailures is null)
            return new QuotaGateDecision(true, "quota unknown; no recent quota-shaped failure", null);

        var observedAt = await _quotaFailures.GetMostRecentAsync(
            member.Agent, member.ModelId, _opts.ObservedFailureWindow, _time.GetUtcNow(), ct);
        if (observedAt is { } seenAt)
            return new QuotaGateDecision(false, $"quota unknown; {FormatObservedFailureReason(member, seenAt, _time.GetUtcNow())}", null);

        return new QuotaGateDecision(true, "quota unknown; no recent quota-shaped failure", null);
    }

    /// <summary>
    /// Builds the rejection reason for an observed-failure breaker hit.
    /// Format: <c>"observed quota failure 8 minutes ago" on agent/model</c>.
    /// Distinct from <c>"quota exhausted"</c> (probe-derived) and
    /// <c>"below floor"</c> so audit log readers can tell breaker hits apart.
    /// </summary>
    internal static string FormatObservedFailureReason(AgentMembership member, DateTimeOffset observedAt, DateTimeOffset now)
    {
        var ageSeconds = Math.Max(0, (long)(now - observedAt).TotalSeconds);
        string ageDesc;
        if (ageSeconds < 60)
            ageDesc = $"{ageSeconds} seconds ago";
        else if (ageSeconds < 3600)
            ageDesc = $"{ageSeconds / 60} minutes ago";
        else
            ageDesc = $"{ageSeconds / 3600}h{ageSeconds % 3600 / 60}m ago";
        var modelDesc = string.IsNullOrEmpty(member.ModelId) ? member.Agent.Value : $"{member.Agent.Value}/{member.ModelId}";
        return $"{modelDesc} observed quota failure {ageDesc}";
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

    private sealed record QuotaGateDecision(
        bool Allow,
        string Reason,
        DateTimeOffset? RetryAt,
        bool InsufficientHeadroom = false,
        IQuotaReservationLease? Reservation = null);
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
    /// True when every eligible subscription-billed member is exhausted or lacks
    /// projected headroom, and no PayPerApi fallback member is eligible. The
    /// item should be parked until <see cref="SuggestedRetryAt"/> or
    /// rechecked after <see cref="SuggestedRecheckIn"/>.
    /// </summary>
    public bool ShouldWait { get; init; }

    /// <summary>
    /// Optional quota reservation lease acquired during routing. The caller
    /// must release this lease when the work item finishes or is cancelled.
    /// </summary>
    public IQuotaReservationLease? Reservation { get; init; }

    /// <summary>Suggested delay before re-attempting pickup.</summary>
    public TimeSpan SuggestedRecheckIn { get; init; }

    /// <summary>Suggested absolute retry time when the wait can be aligned to a quota reset.</summary>
    public DateTimeOffset? SuggestedRetryAt { get; init; }

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

    public bool HeadroomProjectionEnabled { get; set; } = true;

    public int HeadroomHistoryItemCount { get; set; } = 20;

    public TimeSpan HeadroomHistoryWindow { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Conservative token-to-quota conversion used when provider APIs expose
    /// quota only as a percentage. The default treats 100% as roughly one
    /// million input+output tokens, so a 100k-token iteration consumes 10%.
    /// Operators should override this per agent when provider quota units are
    /// known to be materially larger or smaller.
    /// </summary>
    public double HeadroomTokensPerQuotaPct { get; set; } = 10_000.0;

    public Dictionary<string, double> HeadroomTokensPerQuotaPctByAgent { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rejects single cost rows above the estimated one-iteration blast radius.
    /// This prevents corrupted or whole-job aggregate rows from reserving most
    /// of the quota window from one history sample.
    /// </summary>
    public int HeadroomMaxTokensPerCostRow { get; set; } = 500_000;

    /// <summary>
    /// Caps each per-item sample after rows are grouped by work item. The
    /// default is intentionally below the implied full quota window so one
    /// unusually large item cannot dominate the moving average.
    /// </summary>
    public int HeadroomMaxTokensPerIteration { get; set; } = 500_000;
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

    public bool SupportsHeadroom => false;

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

    public bool SupportsHeadroom => false;

    public Task<AgentQuotaSnapshot> GetAvailabilityAsync(AgentMembership member, CancellationToken ct)
        => Task.FromResult(new AgentQuotaSnapshot
        {
            AvailablePct = -1,
            Notes = $"no probe registered for {member.Agent}",
        });
}

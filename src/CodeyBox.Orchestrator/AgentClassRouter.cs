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
    private readonly IAgentBurnEstimator? _burnEstimator;
    private readonly IAgentRunningCounters? _runningCounters;
    private readonly AgentAvailabilityRegistry? _availability;
    // Default fit when no historical samples exist (spec: "fits 2 concurrent
    // burns" so the queue does not stall on cold start). Exposed as a constant
    // so /concurrency surface and tests reference the same value.
    public const double DefaultColdStartFitInWindow = 2.0;
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
        IAgentBurnEstimator? burnEstimator = null,
        IAgentRunningCounters? runningCounters = null,
        AgentAvailabilityRegistry? availability = null)
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
        _burnEstimator = burnEstimator;
        _runningCounters = runningCounters;
        _availability = availability;
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
            // Mid-iteration fallback may have marked this member exhausted in the
            // current process. Skip it immediately so we don't burn a probe round-trip
            // re-discovering what we just learned from a live failure.
            if (IsExhausted(member, nowUtc))
            {
                rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, "in-process exhaustion cache"));
                continue;
            }
            // Smoke gate / fast-fail circuit breaker excluded this agent. Skip
            // it without probing — the binary or credentials are known-broken
            // and a dispatch would either exit 127 immediately or fail auth.
            if (_availability is { } reg)
            {
                var av = reg.GetAvailability(member.Agent);
                if (!av.Available)
                {
                    var smokeReason = $"smoke gate: {av.Reason}";
                    _log.LogInformation("Work item {Id}: rejected: {Reason}", item.Id, smokeReason);
                    rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, smokeReason));
                    continue;
                }
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
            var quota = ResolveMemberQuota(snapshot, member);

            AuditLog.QuotaProbed(member.Agent, classId, quota.AvailablePct, quota.ResetAt, snapshot.Notes);

            var gate = await EvaluateGateAsync(member, item.ProjectId, quota.AvailablePct, ct);
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
            .Where(x => _availability is null || _availability.GetAvailability(x.Member.Agent).Available)
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
            catch
            {
                continue;
            }

            var quota = ResolveMemberQuota(snapshot, member);
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

    private async Task<QuotaGateDecision> EvaluateGateAsync(AgentMembership member, ProjectId projectId, double availablePct, CancellationToken ct)
    {
        if (availablePct >= _opts.MinQuotaPct)
        {
            var rateAware = await EvaluateRateAwareGateAsync(member, availablePct, ct);
            return rateAware ?? new QuotaGateDecision(true, "quota available");
        }

        if (availablePct >= 0)
            return new QuotaGateDecision(false, "quota exhausted");

        return _opts.UnknownPolicy switch
        {
            QuotaUnknownPolicy.FailOpen => new QuotaGateDecision(true, "quota unknown; fail-open"),
            QuotaUnknownPolicy.FailCautious => new QuotaGateDecision(false, "quota unknown; fail-cautious"),
            _ => await EvaluateObservedFailuresAsync(member, ct),
        };
    }

    /// <summary>
    /// Rate-aware gate: returns a denying <see cref="QuotaGateDecision"/> when
    /// the number of items already running on <paramref name="member"/>'s agent
    /// already meets or exceeds the number of additional concurrent burns that
    /// will fit in the remaining quota window. Returns null when no rate-aware
    /// inputs are wired (legacy callers preserve their existing fail-open) or
    /// when the gate would let the dispatch through.
    ///
    /// <para>
    /// Formula (spec part B): <c>FitInWindow = AvailablePct / AvgBurnPctPerItem</c>.
    /// When the estimator has no historical samples yet, the cold-start
    /// fallback at <see cref="DefaultColdStartFitInWindow"/> is used so the
    /// queue does not stall on first boot. PayPerApi members are never gated
    /// here — pay-per-API has no window to overrun.
    /// </para>
    /// </summary>
    private async Task<QuotaGateDecision?> EvaluateRateAwareGateAsync(
        AgentMembership member, double availablePct, CancellationToken ct)
    {
        if (_burnEstimator is null || _runningCounters is null) return null;
        if (member.Billing == AgentBilling.PayPerApi) return null;
        if (availablePct < 0) return null;

        AgentBurnEstimate estimate;
        try { estimate = await _burnEstimator.GetEstimateAsync(member.Agent, ct); }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Rate-aware gate: burn estimator threw for {Agent}; treating as no-data fallback",
                member.Agent.Value);
            estimate = new AgentBurnEstimate { AvgBurnPctPerItem = -1, SampleCount = 0 };
        }

        double fit;
        if (estimate.SampleCount <= 0 || estimate.AvgBurnPctPerItem <= 0)
        {
            fit = DefaultColdStartFitInWindow;
        }
        else
        {
            fit = availablePct / estimate.AvgBurnPctPerItem;
        }

        var running = _runningCounters.GetRunning(member.Agent);
        if (running < fit) return null;

        var reason =
            $"rate-aware gate: running={running} >= fit={fit:F2} " +
            $"(avgBurn={estimate.AvgBurnPctPerItem:F1}% available={availablePct:F1}% samples={estimate.SampleCount})";
        AuditLog.RateAwareGated(member.Agent, member.ModelId, running, fit, estimate.AvgBurnPctPerItem, availablePct, estimate.SampleCount);
        return new QuotaGateDecision(false, reason);
    }

    /// <summary>
    /// Computes the rate-aware fit estimate for every subscription-billed
    /// member of <paramref name="classId"/> using the same formula
    /// <see cref="EvaluateRateAwareGateAsync"/> applies. Pure-read; used by the
    /// <c>/concurrency</c> endpoint to surface the router's current view.
    /// </summary>
    public async Task<IReadOnlyList<MemberFitView>> SummariseFitsAsync(string classId, CancellationToken ct = default)
    {
        var results = new List<MemberFitView>();
        if (!_catalog.TryGetValue(classId, out var agentClass)) return results;
        if (_burnEstimator is null) return results;

        foreach (var member in agentClass.Members)
        {
            if (member.Billing == AgentBilling.PayPerApi) continue;

            AgentQuotaSnapshot snapshot;
            try { snapshot = await ProbeAsync(member, ct); }
            catch { continue; }
            var quota = ResolveMemberQuota(snapshot, member);

            AgentBurnEstimate est;
            try { est = await _burnEstimator.GetEstimateAsync(member.Agent, ct); }
            catch { est = new AgentBurnEstimate { AvgBurnPctPerItem = -1, SampleCount = 0 }; }

            double fit;
            if (est.SampleCount <= 0 || est.AvgBurnPctPerItem <= 0) fit = DefaultColdStartFitInWindow;
            else if (quota.AvailablePct < 0) fit = double.NaN;
            else fit = quota.AvailablePct / est.AvgBurnPctPerItem;

            results.Add(new MemberFitView(
                ClassId: classId,
                Agent: member.Agent,
                ModelId: member.ModelId,
                AvailablePct: quota.AvailablePct,
                AvgBurnPctPerItem: est.AvgBurnPctPerItem,
                SampleCount: est.SampleCount,
                FitInWindow: fit,
                RunningOnAgent: _runningCounters?.GetRunning(member.Agent) ?? 0));
        }
        return results;
    }

    /// <summary>Returns every class id known to the router. Used by /concurrency to enumerate fits.</summary>
    public IReadOnlyCollection<string> ClassIds => _catalog.Keys.ToList();

    private async Task<QuotaGateDecision> EvaluateObservedFailuresAsync(AgentMembership member, CancellationToken ct)
    {
        if (_quotaFailures is null)
            return new QuotaGateDecision(true, "quota unknown; no recent quota-shaped failure");

        var observedAt = await _quotaFailures.GetMostRecentAsync(
            member.Agent, member.ModelId, _opts.ObservedFailureWindow, _time.GetUtcNow(), ct);
        if (observedAt is { } seenAt)
            return new QuotaGateDecision(false, $"quota unknown; {FormatObservedFailureReason(member, seenAt, _time.GetUtcNow())}");

        return new QuotaGateDecision(true, "quota unknown; no recent quota-shaped failure");
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

    /// <summary>Sentinel ModelId meaning "any model in the bucket list is acceptable".</summary>
    internal const string AutoModelSentinel = "auto";

    internal static EffectiveQuota ResolveMemberQuota(AgentQuotaSnapshot snapshot, AgentMembership member)
    {
        if (string.IsNullOrWhiteSpace(member.ModelId))
            return new EffectiveQuota(snapshot.AvailablePct, snapshot.ResetAt, null);

        if (snapshot.PerModel.TryGetValue(member.ModelId, out var modelQuota))
            return new EffectiveQuota(modelQuota.AvailablePct, modelQuota.ResetAt, modelQuota.Window);

        // ModelId is set but not in PerModel.
        //
        // For the "auto" sentinel (gemini ModelRouterService picks per-turn from the
        // available pool), best-of-fleet across the bucket list is the right reading —
        // any single model with quota is enough for auto-routing to succeed.
        if (string.Equals(member.ModelId, AutoModelSentinel, StringComparison.OrdinalIgnoreCase)
            && snapshot.PerModel.Count > 0)
        {
            ModelQuota? best = null;
            foreach (var q in snapshot.PerModel.Values)
            {
                if (best is null || q.AvailablePct > best.AvailablePct)
                    best = q;
            }
            // ResetAt is the earliest reset across all bucket entries (the soonest a
            // currently-walled member will become available again).
            DateTimeOffset? earliestReset = null;
            foreach (var q in snapshot.PerModel.Values)
            {
                if (q.ResetAt is { } r && (earliestReset is null || r < earliestReset))
                    earliestReset = r;
            }
            return new EffectiveQuota(best!.AvailablePct, earliestReset, best.Window);
        }

        // Unknown model id on a probe that DOES provide per-model data — the operator
        // configured a model the probe has no signal for. Fail safe: surface as
        // unknown so QuotaUnknownPolicy gates it, rather than silently falling back
        // to the overall account percentage.
        if (snapshot.PerModel.Count > 0)
            return new EffectiveQuota(-1, null, null);

        // Probe returned no per-model breakdown at all (e.g. NullQuotaProbe, or a
        // provider whose API has no per-model dimension). Fall back to overall.
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
/// Snapshot of the router's rate-aware view for one class member, surfaced via
/// the <c>/concurrency</c> endpoint. <see cref="FitInWindow"/> is the number
/// of additional concurrent burns the router believes will fit in the
/// remaining quota window. <see cref="RunningOnAgent"/> is the live in-flight
/// count compared against it.
/// </summary>
public sealed record MemberFitView(
    string ClassId,
    AgentKind Agent,
    string? ModelId,
    double AvailablePct,
    double AvgBurnPctPerItem,
    int SampleCount,
    double FitInWindow,
    int RunningOnAgent);

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

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
///   <item>Filter to eligible members: must declare every tag in <see cref="WorkItem.RequiredCapabilities"/> AND meet the legacy <see cref="WorkItem.MinModelScore"/> floor (both gates compose with AND during the transition window; TOD modifiers do not affect either check).</item>
///   <item>If no member is eligible, fail with <c>ROUTING_NO_ELIGIBLE</c> — no silent downgrade.</item>
///   <item>Compute each eligible member's effective score: base + sum of applicable time-of-day modifiers.</item>
///   <item>Sort descending by effective score; ties broken by Subscription before PayPerApi, then original config order.</item>
///   <item>Probe quota in sorted order; pick the first member at or above <see cref="QuotaRouterOptions.MinQuotaPct"/>.</item>
///   <item>When the caller supplies an <see cref="IAgentSlotGate"/>, each candidate that passes quota must also fit under its per-agent concurrency cap (the gate atomically test-and-reserves); if not, spill to the next eligible member instead of pinning the item to a saturated agent.</item>
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
public sealed class AgentClassRouter : IAgentQuotaAvailabilitySnapshot, IAgentQuotaAvailabilitySignal, IQuotaRetryRouter, IAgentRoutingReadiness
{
    // The class catalog and pre-parsed TOD modifiers are bundled into a single
    // record so the hot-reload coordinator can publish a coherent (catalog,
    // modifiers) pair via one atomic Volatile.Write. Every public entry point
    // takes one Volatile.Read into a local at method start to keep a
    // dispatch's view consistent if a reload races mid-call.
    private RoutingConfig _routingConfig;
    private readonly IReadOnlyDictionary<AgentKind, IAgentQuotaProbe> _probesByKind;
    private readonly IAgentQuotaProbe _payPerApiProbe;
    private readonly IAgentQuotaProbe _nullProbe;
    private readonly QuotaRouterOptions _opts;
    private readonly ILogger<AgentClassRouter> _log;
    private readonly TimeProvider _time;
    private readonly IQuotaFailureStore? _quotaFailures;
    private readonly IAgentBurnEstimator? _burnEstimator;
    private readonly IAgentRunningCounters? _runningCounters;
    private readonly IAgentAvailabilityRegistry? _availability;
    private readonly IAgentBudgetProvider? _budgetProvider;
    // Shared swappable holder for per-agent operator caps. Same instance is
    // held by OrchestratorService and PipelineRunner so hot-reload writes
    // propagate through one snapshot. Null when no concurrency state is wired
    // (legacy test fixtures) — the cap-spill check falls back to "no cap" and
    // the router behaves as before this feature.
    private readonly AgentConcurrencySnapshot? _concurrencySnapshot;
    private readonly IInVmSmokeGate? _inVmSmokeGate;
    private readonly InVmSmokeSandboxTarget? _configuredSmokeTarget;
    private readonly SmokeOptionsSnapshot? _smokeOptions;
    // Default fit when no historical samples exist (spec: "fits 2 concurrent
    // burns" so the queue does not stall on cold start). Exposed as a constant
    // so /concurrency surface and tests reference the same value.
    public const double DefaultColdStartFitInWindow = 2.0;
    // In-process short-lived exhaustion cache populated by mid-iteration fallback.
    // Keyed by (agent kind, model id ?? ""); value is the UTC instant at which
    // the suppression expires. Survives only the current process lifetime —
    // QuotaRetryScheduler / IQuotaFailureStore cover cross-restart durability.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(AgentKind Agent, string ModelId), DateTimeOffset> _exhausted
        = new();

    // Last quota-availability percentage observed per (agent, model) during
    // routing. Read by the OpenTelemetry observable gauge so dashboards can
    // chart subscription headroom without issuing fresh probe round-trips on
    // the metrics-collection thread. -1 means "unknown" (the probe could not
    // determine availability). Updated on every ProbeAsync result.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(AgentKind Agent, string ModelId), double> _lastAvailablePct
        = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(AgentKind Agent, string ModelId), bool> _lastQuotaUsable
        = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<WorkItemId, QuotaRetryAdmission> _quotaRetryAdmissions
        = new();

    private sealed record QuotaRetryAdmission(AgentKind Agent, string ModelId, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Raised when a routing probe observes an eligible member move from below
    /// the effective quota floor to usable. Exposed through
    /// <see cref="IAgentQuotaAvailabilitySignal"/> so consumers do not need the
    /// concrete router for quota wake-up notifications.
    /// </summary>
    public event Action? QuotaUsableThresholdCrossed;

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
        IAgentAvailabilityRegistry? availability = null,
        IAgentBudgetProvider? budgetProvider = null,
        AgentConcurrencySnapshot? concurrencySnapshot = null,
        IInVmSmokeGate? inVmSmokeGate = null,
        InVmSmokeSandboxTarget? configuredSmokeTarget = null,
        SmokeOptionsSnapshot? smokeOptions = null)
    {
        _routingConfig = new RoutingConfig(
            catalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase),
            todModifiers ?? []);
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
        _quotaFailures = quotaFailures;
        _burnEstimator = burnEstimator;
        _runningCounters = runningCounters;
        _availability = availability;
        _budgetProvider = budgetProvider;
        _concurrencySnapshot = concurrencySnapshot;
        _inVmSmokeGate = inVmSmokeGate;
        _configuredSmokeTarget = configuredSmokeTarget;
        _smokeOptions = smokeOptions;
    }

    /// <summary>
    /// Combines a probe-derived quota with the operator's local budget for the
    /// same (agent, model): takes MIN of the two available percentages so the
    /// stronger constraint gates. When the probe reading is unknown (-1) the
    /// budget percentage stands alone; when no budget is configured the probe
    /// reading is returned unchanged. <c>ResetAt</c> becomes the earlier of the
    /// two known resets so the retry scheduler wakes at the soonest opportunity.
    /// </summary>
    private async Task<BudgetAdjustedQuota> ApplyBudgetAsync(
        AgentMembership member, EffectiveQuota probeQuota, CancellationToken ct)
    {
        if (_budgetProvider is null) return new BudgetAdjustedQuota(probeQuota, false, null);

        AgentQuotaSnapshot? budget;
        try
        {
            budget = await _budgetProvider.GetBudgetSnapshotAsync(member.Agent, member.ModelId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The provider itself failing (as opposed to a configured-but-degraded
            // budget, which the provider already reports as 0%) means we cannot
            // verify the operator's spend cap. Fail closed: gate dispatch rather
            // than silently dropping the budget constraint on a transient error.
            // Mark the result budget-exhausted so a PayPerApi-only fallthrough parks
            // instead of firing while the cap is unverifiable. OperationCanceledException
            // (shutdown/abort) is NOT an accounting outage — let it propagate so
            // dispatch unwinds cleanly instead of being parked as quota-exhausted.
            _log.LogWarning(ex,
                "Budget gate: provider threw for {Agent}/{Model}; failing closed",
                member.Agent.Value, member.ModelId ?? "(default)");
            return new BudgetAdjustedQuota(probeQuota with { AvailablePct = 0.0 }, true, null);
        }

        if (budget is null) return new BudgetAdjustedQuota(probeQuota, false, null);

        var combinedPct = probeQuota.AvailablePct < 0
            ? budget.AvailablePct
            : Math.Min(probeQuota.AvailablePct, budget.AvailablePct);

        var reset = probeQuota.ResetAt;
        if (budget.ResetAt is { } br && (reset is null || br < reset))
            reset = br;

        // A configured budget that is itself below the gate threshold is a real
        // operator spend cap, not a transient probe quirk: callers use this flag to
        // refuse the PayPerApi fire-anyway fallthrough that otherwise fail-opens.
        var budgetExhausted = budget.AvailablePct < _opts.MinQuotaPct;
        return new BudgetAdjustedQuota(
            probeQuota with { AvailablePct = combinedPct, ResetAt = reset },
            budgetExhausted,
            budget.ResetAt);
    }

    /// <summary>
    /// Atomically replaces the in-memory class catalog and TOD modifier list
    /// with the supplied values. Called by the hot-reload coordinator after
    /// rebuilding from the latest <c>CodeyBox:AgentClasses</c> /
    /// <c>CodeyBox:AgentScoreModifiers</c> sections. Dispatches already past
    /// the entry-point Volatile.Read keep their consistent old-snapshot view
    /// for the rest of the call; new dispatches see the new config.
    /// </summary>
    public void ApplyConfigReload(
        IReadOnlyList<AgentClass> catalog,
        IReadOnlyList<ParsedTodModifier> todModifiers)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(todModifiers);
        var next = new RoutingConfig(
            catalog.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase),
            todModifiers);
        Volatile.Write(ref _routingConfig, next);
    }

    private sealed record RoutingConfig(
        IReadOnlyDictionary<string, AgentClass> Catalog,
        IReadOnlyList<ParsedTodModifier> TodModifiers);

    /// <summary>
    /// Resolves the agent to use for <paramref name="item"/>.
    /// Returns <see cref="AgentRoutingDecision.Chosen"/> = null and
    /// <see cref="AgentRoutingDecision.ShouldWait"/> = false when no agent
    /// class applies — the caller falls back to direct agent pick with no
    /// quota probe, preserving legacy behaviour exactly.
    ///
    /// <para>
    /// When <paramref name="slotGate"/> is supplied, the router treats the
    /// per-agent concurrency cap as an additional gate alongside quota and
    /// exclusion: each candidate that would otherwise win must first reserve
    /// a slot via <see cref="IAgentSlotGate.TryReserve"/>; members where the
    /// gate returns false are skipped (with a <c>"per-agent cap reached"</c>
    /// rejection) so a lower-ranked but free-and-eligible member can be
    /// picked instead. This spill prevents items from queuing behind a
    /// saturated top member while other eligible members sit idle.
    /// </para>
    /// <para>
    /// If every viable member is at cap, the decision returns
    /// <see cref="AgentRoutingDecision.ShouldWait"/> with
    /// <see cref="AgentRoutingDecision.AnyMemberAtCap"/> = true so the caller
    /// can pick a short cap-retry interval rather than the full quota
    /// recheck. On a successful return the slot is already held by the gate
    /// — the caller MUST <see cref="IAgentSlotGate.Release"/> on every exit
    /// path. The router never releases on its own.
    /// </para>
    /// </summary>
    public Task<AgentRoutingDecision> ResolveAsync(
        WorkItem item, Project? project, CancellationToken ct,
        IAgentSlotGate? slotGate = null)
        => ResolveCoreAsync(
            item,
            project,
            ct,
            slotGate,
            bypassRecentFailurePrecheck: false,
            bypassInProcessExhaustion: false);

    public async Task<AgentRoutingReadiness> CheckReadinessAsync(
        WorkItem item,
        Project? project,
        IAgentCapacitySnapshot capacity,
        CancellationToken ct)
    {
        var decision = await ResolveCoreAsync(
            item,
            project,
            ct,
            new ReadOnlyCapacitySlotGate(capacity),
            bypassRecentFailurePrecheck: false,
            bypassInProcessExhaustion: false,
            commitDispatchSideEffects: false);

        if (decision.Chosen is { } chosen)
            return AgentRoutingReadiness.Available(chosen.Agent, decision.Reason);

        if (decision.NoEligibleMembers || decision.ShouldWait)
            return AgentRoutingReadiness.Unavailable(decision.Reason);

        return AgentRoutingReadiness.NotApplicable(decision.Reason);
    }

    private sealed class ReadOnlyCapacitySlotGate : IAgentSlotGate
    {
        private readonly IAgentCapacitySnapshot _capacity;

        public ReadOnlyCapacitySlotGate(IAgentCapacitySnapshot capacity) => _capacity = capacity;

        public bool TryReserve(AgentKind agent) => _capacity.HasCapacity(agent);

        public void Release(AgentKind agent) { }
    }

    public async Task<QuotaRetryRoutingDecision> ResolveQuotaRetryAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct)
    {
        var decision = await ResolveCoreAsync(
            item,
            project,
            ct,
            slotGate: null,
            bypassRecentFailurePrecheck: true,
            bypassInProcessExhaustion: true,
            commitDispatchSideEffects: true);
        if (decision.Chosen is { } chosen)
            RecordQuotaRetryAdmission(item, chosen);

        return new QuotaRetryRoutingDecision(
            decision.ShouldWait,
            decision.NoEligibleMembers,
            decision.Reason);
    }

    private async Task<AgentRoutingDecision> ResolveCoreAsync(
        WorkItem item, Project? project, CancellationToken ct,
        IAgentSlotGate? slotGate,
        bool bypassRecentFailurePrecheck,
        bool bypassInProcessExhaustion,
        bool commitDispatchSideEffects = true)
    {
        var cfg = Volatile.Read(ref _routingConfig);
        var classId = item.AgentClassId ?? project?.DefaultAgentClass;
        if (classId is null)
            return new AgentRoutingDecision { Reason = "no agent class configured" };
        var smokeTarget = ResolveInitialSmokeTarget(item, project);

        if (!cfg.Catalog.TryGetValue(classId, out var agentClass))
        {
            _log.LogWarning(
                "Work item {Id}: unknown agent class '{ClassId}'; falling through to direct agent pick",
                item.Id, classId);
            return new AgentRoutingDecision { Reason = $"unknown agent class '{classId}'" };
        }

        // Step 1: filter by eligibility — both the legacy QualityScore floor and
        // the new capability gate must pass during the transition window.
        // TOD modifiers do not affect eligibility (they tune routing PREFERENCE only).
        var eligible = agentClass.Members
            .Select((m, idx) => (Member: m, ConfigIndex: idx))
            .Where(x => x.Member.QualityScore >= item.MinModelScore)
            .Where(x => MemberCoversRequiredCapabilities(x.Member, item.RequiredCapabilities))
            .ToList();

        if (eligible.Count == 0)
        {
            var best = agentClass.Members.Count > 0
                ? agentClass.Members.Max(m => m.QualityScore)
                : 0;
            var reason = $"ROUTING_NO_ELIGIBLE: no member of class '{classId}' meets " +
                         $"MinModelScore={item.MinModelScore} / RequiredCapabilities=[{string.Join(",", item.RequiredCapabilities)}] " +
                         $"(best available={best})";
            _log.LogError("Work item {Id}: {Reason}", item.Id, reason);
            // Emit scored audit event so eligibility rejects appear in the audit log.
            var nowUtcFloor = _time.GetUtcNow();
            var below = agentClass.Members
                .Select(m => (
                    Agent: m.Agent,
                    ModelId: m.ModelId,
                    EffectiveScore: m.QualityScore + ComputeTodModifier(cfg.TodModifiers, m.Agent, nowUtcFloor),
                    RejectReason: DescribeIneligibility(m, item)))
                .ToList();
            if (commitDispatchSideEffects)
                AuditLog.QuotaRouterNoEligible(item.Id, classId, item.MinModelScore, below);
            return new AgentRoutingDecision { Reason = reason, NoEligibleMembers = true };
        }

        // Step 2: compute effective scores (base + TOD modifier).
        var nowUtc = _time.GetUtcNow();
        PruneExpiredQuotaRetryAdmissions(nowUtc);
        var scored = eligible.Select(x => new ScoredMember(
            Member: x.Member,
            BaseScore: x.Member.QualityScore,
            EffectiveScore: x.Member.QualityScore + ComputeTodModifier(cfg.TodModifiers, x.Member.Agent, nowUtc),
            ConfigIndex: x.ConfigIndex
        )).ToList();

        // Step 3: sort — highest effective score first; ties: Subscription before PayPerApi, then config order.
        var sorted = scored
            .OrderByDescending(x => x.EffectiveScore)
            .ThenBy(x => x.Member.Billing == AgentBilling.Subscription ? 0 : 1)
            .ThenBy(x => x.ConfigIndex)
            .ToList();
        var quotaRetryAdmission = bypassRecentFailurePrecheck && bypassInProcessExhaustion
            ? null
            : GetQuotaRetryAdmission(item.Id, nowUtc);
        var quotaRetryAdmissionDeniedAfterProbe = false;

        // Rejected members accumulate for the audit event.
        var rejected = new List<(AgentKind Agent, string? ModelId, int EffectiveScore, string RejectReason)>();

        // Also track which members were filtered out by eligibility gates so the
        // audit log shows why they didn't make the sort list (separate from
        // gate/probe rejections that happen later).
        foreach (var m in agentClass.Members)
        {
            var failsScore = m.QualityScore < item.MinModelScore;
            var failsCaps = !MemberCoversRequiredCapabilities(m, item.RequiredCapabilities);
            if (!failsScore && !failsCaps) continue;
            var eff = m.QualityScore + ComputeTodModifier(cfg.TodModifiers, m.Agent, nowUtc);
            rejected.Add((m.Agent, m.ModelId, eff, DescribeIneligibility(m, item)));
        }

        var hasSubscription = sorted.Any(x => x.Member.Billing == AgentBilling.Subscription);
        // Track subscription members benched purely by the availability gate
        // (in-VM smoke / fast-fail breaker / missing-probe). If every
        // subscription member fell out for that reason — and none for quota —
        // the "wait" we return below is unblocked by the smoke sweep / operator
        // reset, NOT a quota recheck, so the reason text must say so rather than
        // claim a quota threshold.
        var subscriptionTotal = sorted.Count(x => x.Member.Billing == AgentBilling.Subscription);
        var subscriptionSmokeExcluded = 0;
        // Every member the availability gate benched (in-VM smoke / fast-fail /
        // missing-probe), regardless of billing. The PayPerApi-only fallback below
        // must never fire one of these: a smoke bench means the binary is broken,
        // which the "fire despite low quota" fallback exists to override only for
        // quota inaccuracy, not for a CLI that will exit 127 / fail auth (AC#1).
        var smokeExcluded = new HashSet<(AgentKind, string?)>();

        // PayPerApi members that an exhausted operator budget pushed below threshold:
        // these must NOT be fired by the no-Subscription fallthrough below (doing so
        // would fail-open the operator spend cap). Tracked with the soonest budget reset.
        var budgetExhaustedMembers = new HashSet<AgentMembership>();
        DateTimeOffset? earliestBudgetReset = null;

        // Members the operator's per-agent concurrency cap pushed past in this
        // pass at the PRE-PROBE check (running counters meet the cap): the cap
        // was at its ceiling so we skipped to a lower-ranked member rather than
        // DEFERring the work item. Recorded so the PayPerApi fire-anyway
        // fallthrough doesn't pick a cap-saturated member (the slot gate would
        // just refuse it) and so the post-loop wait interval shrinks to the
        // cap-retry window when cap was the only blocker — a slot opens far
        // sooner than a quota window resets.
        var capSaturatedMembers = new HashSet<AgentMembership>();

        // Agents whose per-agent cap blocked them at the POST-GATE slot gate
        // (the gate's atomic TryReserve returned false after the quota gate
        // passed). Surfaced via AtCapAgents on the routing decision so the
        // caller can emit per-agent audit events without re-deriving which
        // members were blocked, and used together with capSaturatedMembers to
        // drive AnyMemberAtCap.
        var atCapAgents = new List<AgentKind>();

        // Step 4: probe quota in sorted order; pick the first viable member.
        foreach (var entry in sorted)
        {
            var member = entry.Member;
            var quotaRetryAdmissionMatches = QuotaRetryAdmissionMatches(quotaRetryAdmission, member);
            // Mid-iteration fallback may have marked this member exhausted in the
            // current process. Skip it immediately so we don't burn a probe round-trip
            // re-discovering what we just learned from a live failure.
            if (!bypassInProcessExhaustion
                && !quotaRetryAdmissionMatches
                && IsExhausted(member, nowUtc))
            {
                rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, "in-process exhaustion cache"));
                continue;
            }
            // Smoke gate / fast-fail circuit breaker excluded this agent? Skip
            // it — the binary or credentials are known-broken and a dispatch
            // would either exit 127 immediately or fail auth. The in-VM gate
            // (when wired) also probes an apparently-Available-but-never-probed
            // agent here so the exit-127 / auth cascade is caught on the FIRST
            // dispatch, not on first run; a cache hit is free.
            var availability = await GetGatedAvailabilityAsync(member.Agent, smokeTarget, ct);
            if (availability is { Available: false })
            {
                var smokeReason = $"smoke gate: {availability.Reason}";
                if (commitDispatchSideEffects)
                    _log.LogInformation("Work item {Id}: rejected: {Reason}", item.Id, smokeReason);
                rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, smokeReason));
                smokeExcluded.Add((member.Agent, member.ModelId));
                if (member.Billing == AgentBilling.Subscription)
                    subscriptionSmokeExcluded++;
                continue;
            }
            if (!bypassRecentFailurePrecheck
                && !quotaRetryAdmissionMatches
                && member.Billing == AgentBilling.Subscription
                && _quotaFailures is not null)
            {
                var observedAt = await _quotaFailures.GetMostRecentAsync(
                    member.Agent, member.ModelId, _opts.ObservedFailureWindow, _time.GetUtcNow(), ct);
                if (observedAt is { } seenAt)
                {
                    var reason = FormatObservedFailureReason(member, seenAt, _time.GetUtcNow());
                    if (commitDispatchSideEffects)
                        _log.LogInformation("Work item {Id}: rejected: {Reason}", item.Id, reason);
                    rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, reason));
                    continue;
                }
            }

            // Per-agent operator concurrency cap: skip if the routed agent has
            // no free slot. This is the SPILL step — when the highest-quality
            // member is at cap, continue to the next eligible member rather than
            // returning a defer. Checked BEFORE the quota probe so we don't burn
            // a probe round-trip on a member we can't dispatch to anyway.
            // The orchestrator's TryReserveAgentSlot remains authoritative for
            // the actual reservation; a race where slots fill between this read
            // and that reservation still falls through to the orchestrator's
            // existing cap-defer (rare, and correctly bounded).
            if (IsAtAgentCap(member))
            {
                var cap = GetAgentCap(member.Agent);
                var running = _runningCounters?.GetRunning(member.Agent) ?? 0;
                var capReason = $"per-agent cap: running={running} cap={cap}";
                if (commitDispatchSideEffects)
                    _log.LogInformation("Work item {Id}: rejected: {Reason}", item.Id, capReason);
                rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, capReason));
                capSaturatedMembers.Add(member);
                if (commitDispatchSideEffects)
                    AuditLog.ConcurrencyGated(item.Id, member.Agent, running, cap);
                continue;
            }

            AgentQuotaSnapshot snapshot;
            try
            {
                snapshot = await ProbeAsync(member, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Probe threw (transient API error). Treat it as unknown (-1) and
                // still apply the local-budget MIN below rather than aborting the
                // whole routing pass: a healthy configured budget must still be
                // able to gate or admit the member when the subscription probe API
                // blips. Bypassing the budget here would fail-open the operator
                // spend cap on a probe error. OperationCanceledException
                // (shutdown/abort) is allowed to propagate.
                _log.LogDebug(ex,
                    "Quota probe for {Agent}/{Model} threw; treating as unknown",
                    member.Agent.Value, member.ModelId ?? "(default)");
                snapshot = new AgentQuotaSnapshot
                {
                    AvailablePct = -1,
                    Notes = $"probe threw: {ex.GetType().Name}",
                };
            }
            var quota = ResolveMemberQuota(snapshot, member);
            var budgeted = await ApplyBudgetAsync(member, quota, ct);
            quota = budgeted.Quota;

            if (member.Billing == AgentBilling.PayPerApi && budgeted.BudgetExhausted)
            {
                budgetExhaustedMembers.Add(member);
                if (budgeted.BudgetReset is { } r && (earliestBudgetReset is null || r < earliestBudgetReset))
                    earliestBudgetReset = r;
            }

            if (commitDispatchSideEffects)
                AuditLog.QuotaProbed(member.Agent, classId, quota.AvailablePct, quota.ResetAt, snapshot.Notes);

            var gate = await EvaluateGateAsync(member, item.ProjectId, quota, nowUtc, ct);
            if (commitDispatchSideEffects)
                RecordAvailabilityAndMaybeNotify(member, quota, gate);
            else
                RecordObservedAvailability(member, quota);
            if (gate.Allow)
            {
                // Per-agent concurrency cap: spill to the next eligible member
                // when the gate's atomic test-and-reserve refuses. The router
                // only commits the choice when the reservation actually
                // succeeds, so the caller skips its own redundant reserve and
                // the race between check and commit is closed by the gate's
                // atomic increment.
                if (slotGate is not null && !slotGate.TryReserve(member.Agent))
                {
                    var capReason = "per-agent cap reached";
                    if (commitDispatchSideEffects)
                    {
                        _log.LogInformation("Work item {Id}: spilling past {Agent}/{Model}: {Reason}",
                            item.Id, member.Agent, member.ModelId ?? "(default)", capReason);
                    }
                    rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, capReason));
                    atCapAgents.Add(member.Agent);
                    continue;
                }

                // Mark all remaining sorted entries as "ranked lower" for the audit event.
                foreach (var other in sorted.Where(x => x != entry))
                    rejected.Add((other.Member.Agent, other.Member.ModelId, other.EffectiveScore, "ranked lower"));

                var modDesc = DescribeModifiers(cfg.TodModifiers, member.Agent, nowUtc);
                if (commitDispatchSideEffects)
                {
                    AuditLog.QuotaRouterScored(
                        item.Id, classId,
                        member.Agent, member.ModelId,
                        entry.BaseScore, entry.EffectiveScore, modDesc,
                        rejected);
                }

                if (commitDispatchSideEffects)
                {
                    _log.LogInformation(
                        "Work item {Id}: routed to {Agent}/{Billing} model={Model} " +
                        "baseScore={Base} effectiveScore={Eff} (available={Avail:F1}%)",
                        item.Id, member.Agent, member.Billing,
                        member.ModelId ?? "(default)", entry.BaseScore, entry.EffectiveScore,
                        quota.AvailablePct);
                }

                if (commitDispatchSideEffects)
                    ConsumeQuotaRetryAdmission(item.Id, quotaRetryAdmission);
                return new AgentRoutingDecision
                {
                    Chosen = member,
                    SlotReserved = slotGate is not null,
                    Reason = $"{member.Agent}/{member.Billing} score={entry.EffectiveScore}: {quota.AvailablePct:F1}% available",
                };
            }

            if (quotaRetryAdmissionMatches)
                quotaRetryAdmissionDeniedAfterProbe = true;
            rejected.Add((member.Agent, member.ModelId, entry.EffectiveScore, gate.Reason));
        }

        if (commitDispatchSideEffects && quotaRetryAdmissionDeniedAfterProbe)
            ConsumeQuotaRetryAdmission(item.Id, quotaRetryAdmission);

        // No member was chosen. Distinguish stall reasons so the caller picks
        // the right defer interval and the audit log shows what actually
        // blocked dispatch:
        //  - cap-blocked: operator concurrency cap — clears in seconds, so
        //    surface AnyMemberAtCap and use the cap-retry interval. Even one
        //    cap-blocked member means a worker finishing on that agent will
        //    free up a routable slot within the cap-retry window.
        //  - smoke-excluded: cleared by the in-VM smoke sweep / operator
        //    reset, NOT by quota recovery — don't misreport it as quota.
        //  - quota-below-floor: clears when the quota window resets.
        var anyAtCap = atCapAgents.Count > 0;
        if (hasSubscription || anyAtCap)
        {
            // When a member was blocked only by per-agent cap (not quota), a
            // slot frees up far sooner than a quota window resets, so the
            // soonest of cap-retry vs quota-recheck is surfaced — the operator
            // may have configured a shorter QuotaRecheckInterval and we always
            // honour the earliest plausible retry.
            var capBlocked = atCapAgents.Count > 0 || capSaturatedMembers.Count > 0;
            var capRetry = _opts.QuotaRecheckInterval < _opts.CapRetryRecheckInterval
                ? _opts.QuotaRecheckInterval
                : _opts.CapRetryRecheckInterval;
            var hasNonCapRejection = rejected.Any(r =>
                r.RejectReason != "per-agent cap reached"
                && r.RejectReason != "ranked lower"
                && !r.RejectReason.StartsWith("per-agent cap:", StringComparison.Ordinal));
            var allSmokeExcluded = subscriptionTotal > 0 && subscriptionSmokeExcluded == subscriptionTotal;
            string reason;
            TimeSpan suggested;
            if (capBlocked && hasNonCapRejection)
            {
                reason = $"mixed defer: at least one eligible member of class '{classId}' is at its per-agent concurrency cap (others failed quota/availability gates)";
                suggested = capRetry;
            }
            else if (capBlocked)
            {
                reason = $"every quota-passing member of class '{classId}' is at its per-agent concurrency cap";
                suggested = capRetry;
            }
            else if (allSmokeExcluded)
            {
                reason = $"all subscription members of class '{classId}' are benched by the smoke gate / fast-fail breaker — "
                         + "waiting for the in-VM smoke sweep or an operator reset to clear them";
                suggested = _opts.QuotaRecheckInterval;
            }
            else
            {
                reason = $"all members of class '{classId}' are below the effective quota floor " +
                         $"(ramp {_opts.StartFloorPct:F1}%→{_opts.EndFloorPct:F1}%, fallback {_opts.MinQuotaPct:F1}%)";
                suggested = _opts.QuotaRecheckInterval;
            }
            if (commitDispatchSideEffects)
                AuditLog.QuotaRouterWaiting(classId, item.Id, suggested);
            return new AgentRoutingDecision
            {
                ShouldWait = true,
                SuggestedRecheckIn = suggested,
                AnyMemberAtCap = capBlocked,
                AtCapAgents = atCapAgents,
                Reason = reason,
            };
        }

        // Only PayPerApi members reached here. PayPerApi probes normally report 100%,
        // so a below-threshold member is usually an unusual custom probe — fire anyway.
        // EXCEPTIONS that must NOT be fired:
        //  - budget-exhausted: a configured operator spend cap (fail-open would burn money).
        //  - cap-saturated: the slot gate would refuse the dispatch.
        //  - smoke-excluded: the in-VM smoke / fast-fail breaker benched the CLI
        //    because the binary is broken; routing to it would reproduce the
        //    exit-127 / auth cascade the gate exists to catch (AC#1). The
        //    "fire despite low quota" fallback exists to override probe
        //    inaccuracy, not to dispatch to a known-broken binary.
        // Spill through eligible candidates in score order, honouring the caller's
        // slot gate so the per-agent cap remains an authoritative gate for
        // PayPerApi members too.
        foreach (var candidate in sorted)
        {
            if (budgetExhaustedMembers.Contains(candidate.Member)) continue;
            if (capSaturatedMembers.Contains(candidate.Member)) continue;
            if (smokeExcluded.Contains((candidate.Member.Agent, candidate.Member.ModelId))) continue;
            var fallback = candidate.Member;
            if (slotGate is not null && !slotGate.TryReserve(fallback.Agent))
            {
                atCapAgents.Add(fallback.Agent);
                continue;
            }

            if (commitDispatchSideEffects)
            {
                _log.LogWarning(
                    "Work item {Id}: all members below threshold but class '{ClassId}' has no Subscription members; firing {Agent} anyway",
                    item.Id, classId, fallback.Agent);
            }
            if (commitDispatchSideEffects)
                ConsumeQuotaRetryAdmission(item.Id, quotaRetryAdmission);
            return new AgentRoutingDecision
            {
                Chosen = fallback,
                SlotReserved = slotGate is not null,
                Reason = "only PayPerApi members — firing despite apparent low quota",
            };
        }

        // Build the park interval from whichever blocker is the soonest to clear.
        // budget-reset is typically minutes-to-hours; cap-retry is seconds. Both
        // can be active simultaneously (some PayPerApi members budget-exhausted,
        // others cap-saturated) so we take the soonest. A smoke-only bench is
        // cleared by the in-VM smoke sweep / operator reset, NOT by quota or
        // budget reset, so it uses the full quota recheck window.
        var budgetRecheck = _opts.QuotaRecheckInterval;
        if (earliestBudgetReset is { } budgetReset)
        {
            var untilReset = budgetReset - nowUtc;
            if (untilReset > TimeSpan.Zero && untilReset < budgetRecheck)
                budgetRecheck = untilReset;
        }
        var fallbackCapBlocked = capSaturatedMembers.Count > 0 || atCapAgents.Count > 0;
        if (fallbackCapBlocked && budgetRecheck > _opts.CapRetryRecheckInterval)
            budgetRecheck = _opts.CapRetryRecheckInterval;
        var allFallbackSmokeExcluded = sorted.Count > 0
            && sorted.All(x => smokeExcluded.Contains((x.Member.Agent, x.Member.ModelId)));
        string parkReason;
        if (allFallbackSmokeExcluded)
            parkReason = $"all PayPerApi members of class '{classId}' are benched by the smoke gate / fast-fail breaker — "
                         + "waiting for the in-VM smoke sweep or an operator reset to clear them";
        else if (budgetExhaustedMembers.Count > 0 && fallbackCapBlocked)
            parkReason = $"all PayPerApi members of class '{classId}' are budget-exhausted or at their per-agent concurrency cap";
        else if (fallbackCapBlocked)
            parkReason = $"all PayPerApi members of class '{classId}' are at their per-agent concurrency cap";
        else
            parkReason = $"all PayPerApi members of class '{classId}' are budget-exhausted";
        if (commitDispatchSideEffects)
        {
            _log.LogInformation(
                "Work item {Id}: class '{ClassId}' parking — {Reason}",
                item.Id, classId, parkReason);
        }
        if (commitDispatchSideEffects)
            AuditLog.QuotaRouterWaiting(classId, item.Id, budgetRecheck);
        return new AgentRoutingDecision
        {
            ShouldWait = true,
            SuggestedRecheckIn = budgetRecheck,
            AnyMemberAtCap = fallbackCapBlocked,
            AtCapAgents = atCapAgents,
            Reason = parkReason,
        };
    }

    /// <summary>
    /// Returns true when <paramref name="member"/>'s agent has an operator-
    /// configured per-agent cap and the live in-flight count is at or above
    /// that cap. Always false when either the cap config or the running
    /// counters are not wired — keeping router behaviour stable for fixtures
    /// that don't register concurrency.
    /// </summary>
    private bool IsAtAgentCap(AgentMembership member)
    {
        if (_runningCounters is null) return false;
        var cap = GetAgentCap(member.Agent);
        if (cap <= 0) return false;
        return _runningCounters.GetRunning(member.Agent) >= cap;
    }

    /// <summary>
    /// Reads the per-agent cap from the swappable snapshot. Returns 0 when no
    /// snapshot is wired or the agent has no entry (= unlimited within the
    /// global worker-pool ceiling). Defence-in-depth: rejects stored
    /// <c>MaxConcurrent &lt;= 0</c> values even though
    /// <see cref="AgentConcurrencyOptions.ValidateAndThrow"/> rejects them at
    /// load — test fixtures can build options directly without the validator.
    /// </summary>
    private int GetAgentCap(AgentKind agent)
    {
        var opts = _concurrencySnapshot?.Current;
        return opts is not null
            && opts.Members.TryGetValue(agent.Value, out var entry)
            && entry is { MaxConcurrent: > 0 }
            ? entry.MaxConcurrent
            : 0;
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
        var cfg = Volatile.Read(ref _routingConfig);
        if (!cfg.Catalog.TryGetValue(classId, out var agentClass)) return null;
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
    /// Returns the set of agent kinds in <paramref name="classId"/> that
    /// declare <paramref name="capability"/> in their
    /// <see cref="AgentMembership.Capabilities"/> list, or <c>null</c> when
    /// the class is unknown OR no member carries the tag (the
    /// "opt-out / legacy" signal — no member opted in, so callers should
    /// fall back to their pre-capability routing). Returns a non-empty set
    /// only when at least one member is tagged, so callers can treat a
    /// non-null result as "the pool is active and restrictive".
    /// <para>
    /// Used by <c>PipelineRunner.ResolveAuditAgentRunnerAsync</c> to gate the
    /// audit phase to <see cref="WellKnownCapabilities.Audit"/>-tagged
    /// members without hardcoding agent IDs in code. Capability comparison
    /// is ordinal, case-insensitive — matches
    /// <see cref="AgentMembershipExtensions.HasCapability"/>.
    /// </para>
    /// </summary>
    public IReadOnlySet<AgentKind>? GetCapabilityPool(string? classId, string capability)
    {
        if (string.IsNullOrEmpty(classId) || string.IsNullOrEmpty(capability)) return null;
        var cfg = Volatile.Read(ref _routingConfig);
        if (!cfg.Catalog.TryGetValue(classId, out var agentClass)) return null;
        var pool = new HashSet<AgentKind>();
        foreach (var member in agentClass.Members)
        {
            if (member.HasCapability(capability))
                pool.Add(member.Agent);
        }
        return pool.Count == 0 ? null : pool;
    }

    /// <inheritdoc />
    public IReadOnlyList<(AgentKind Agent, string? ModelId, double AvailablePct)> SnapshotQuotaAvailability()
    {
        var snap = new List<(AgentKind, string?, double)>(_lastAvailablePct.Count);
        foreach (var kv in _lastAvailablePct)
            snap.Add((kv.Key.Agent, kv.Key.ModelId.Length == 0 ? null : kv.Key.ModelId, kv.Value));
        return snap;
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
    /// Returns an empty list when no class is configured, no class member is
    /// eligible (fails the <see cref="WorkItem.MinModelScore"/> floor or does
    /// not cover the work item's <see cref="WorkItem.RequiredCapabilities"/>),
    /// or every eligible member is currently marked exhausted in this process.
    /// </para>
    /// <para>
    /// Like <see cref="ResolveAsync"/>, this gates each apparently-available
    /// candidate on a real in-sandbox CLI check (<see cref="IInVmSmokeGate"/>)
    /// before returning it, so a mid-iteration / audit / rebase fallback never
    /// hands work to an agent whose CLI was never in-VM smoke-checked (the
    /// exit-127 / auth cascade). A cache hit is free; an agent the probe
    /// benches is dropped from the returned list exactly as the primary path
    /// would skip it.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<AgentMembership>> OrderedFallbackCandidatesAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct,
        InVmSmokeSandboxTarget? smokeTarget = null)
    {
        var cfg = Volatile.Read(ref _routingConfig);
        var classId = item.AgentClassId ?? project?.DefaultAgentClass;
        if (classId is null || !cfg.Catalog.TryGetValue(classId, out var agentClass))
            return [];
        var target = smokeTarget ?? ResolveWorkSmokeTarget(project, item.BaselineImageRef);

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

        // Score + order the eligible, non-exhausted members first. Availability
        // (and the in-VM gate) is applied last, in score order, so we only probe
        // members we would actually return — and never burn a probe on a member
        // already filtered out by score or in-process exhaustion.
        var ordered = agentClass.Members
            .Select((m, idx) => (Member: m, ConfigIndex: idx))
            .Where(x => x.Member.QualityScore >= item.MinModelScore)
            .Where(x => MemberCoversRequiredCapabilities(x.Member, item.RequiredCapabilities))
            .Where(x => !IsExhausted(x.Member, nowUtc))
            .Select(x => new
            {
                x.Member,
                x.ConfigIndex,
                EffectiveScore = x.Member.QualityScore + ComputeTodModifier(cfg.TodModifiers, x.Member.Agent, nowUtc),
            })
            .OrderByDescending(x => x.EffectiveScore)
            .ThenBy(x => x.Member.Billing == AgentBilling.Subscription ? 0 : 1)
            .ThenBy(x => x.ConfigIndex)
            .Select(x => x.Member)
            .ToList();

        if (_availability is null && _inVmSmokeGate is null)
            return ordered;

        // Apply the same gate-or-registry verdict ResolveAsync uses, so a
        // mid-iteration / audit / rebase fallback never hands work to an agent
        // whose CLI was never in-VM smoke-checked (cache hit = free).
        var result = new List<AgentMembership>(ordered.Count);
        foreach (var member in ordered)
        {
            var av = await GetGatedAvailabilityAsync(member.Agent, target, ct);
            if (av is null || av.Available)
                result.Add(member);
        }
        return result;
    }

    /// <summary>
    /// Single source of truth for an agent's routable verdict on the dispatch
    /// path. When the in-VM smoke gate is wired it owns the read→probe→re-read
    /// (so an apparently-Available-but-never-probed agent is verified in-sandbox
    /// before it is trusted); otherwise the availability registry is read
    /// directly. Returns null only when neither is wired (no availability
    /// tracking → legacy behaviour, every candidate is routable). Centralised
    /// so primary routing and fallback selection cannot drift in gate semantics.
    /// <see cref="InVmSmokeSandboxTarget.BaselineRef"/> is set only when the
    /// requested target matches the work item's pinned work/headless baseline,
    /// so non-work or graphical targets resolve their own active baseline.
    /// </summary>
    private InVmSmokeSandboxTarget ResolveWorkSmokeTarget(Project? project, string? workBaselineRef = null)
    {
        if (project is null)
            return _configuredSmokeTarget ?? default;

        return SandboxTargetResolver.ToInVmSmokeTarget(
            project,
            SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work),
            workBaselineRef);
    }

    private InVmSmokeSandboxTarget ResolveInitialSmokeTarget(WorkItem item, Project? project)
    {
        if (project is null)
            return _configuredSmokeTarget ?? default;

        var target = item.JobType == JobType.CheckAndAct
            ? new SandboxTarget(project.NetworkProfiles.Work, SandboxProfileFlavor.Headless)
            : SandboxTargetResolver.ResolveProjectPhase(project, project.NetworkProfiles.Work);

        return SandboxTargetResolver.ToInVmSmokeTarget(project, target, item.BaselineImageRef);
    }

    private async Task<AgentAvailability?> GetGatedAvailabilityAsync(
        AgentKind kind,
        InVmSmokeSandboxTarget target,
        CancellationToken ct)
    {
        if (_smokeOptions?.Enabled == false)
            return _availability?.GetAvailability(kind, AgentAvailabilityReadMode.IgnoreSmokeGateExclusions);

        if (_inVmSmokeGate is not null)
            return await _inVmSmokeGate.EnsureAvailableAsync(kind, target, ct);
        if (_availability is not null)
            return _availability.GetAvailability(kind);
        return null;
    }

    /// <summary>
    /// Marks a class member as exhausted in this process for <paramref name="ttl"/>
    /// (or until <paramref name="resetAt"/>, whichever is sooner). Subsequent
    /// calls to <see cref="OrderedFallbackCandidatesAsync"/> and
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

    private void RecordQuotaRetryAdmission(WorkItem item, AgentMembership member)
    {
        var nowUtc = _time.GetUtcNow();
        PruneExpiredQuotaRetryAdmissions(nowUtc);
        var ttl = _opts.ObservedFailureWindow > TimeSpan.Zero
            ? _opts.ObservedFailureWindow
            : TimeSpan.FromMinutes(1);
        var admission = new QuotaRetryAdmission(
            member.Agent,
            member.ModelId ?? string.Empty,
            nowUtc + ttl);
        _quotaRetryAdmissions[item.Id] = admission;
    }

    private void PruneExpiredQuotaRetryAdmissions(DateTimeOffset nowUtc)
    {
        foreach (var entry in _quotaRetryAdmissions)
        {
            if (entry.Value.ExpiresAt <= nowUtc)
                _quotaRetryAdmissions.TryRemove(entry);
        }
    }

    private QuotaRetryAdmission? GetQuotaRetryAdmission(WorkItemId itemId, DateTimeOffset nowUtc)
    {
        if (!_quotaRetryAdmissions.TryGetValue(itemId, out var admission))
            return null;

        if (admission.ExpiresAt > nowUtc)
            return admission;

        _quotaRetryAdmissions.TryRemove(
            new KeyValuePair<WorkItemId, QuotaRetryAdmission>(itemId, admission));
        return null;
    }

    private static bool QuotaRetryAdmissionMatches(QuotaRetryAdmission? admission, AgentMembership member)
        => admission is not null
           && admission.Agent == member.Agent
           && string.Equals(admission.ModelId, member.ModelId ?? string.Empty, StringComparison.Ordinal);

    private void ConsumeQuotaRetryAdmission(WorkItemId itemId, QuotaRetryAdmission? admission)
    {
        if (admission is null)
            return;

        _quotaRetryAdmissions.TryRemove(
            new KeyValuePair<WorkItemId, QuotaRetryAdmission>(itemId, admission));
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
        var cfg = Volatile.Read(ref _routingConfig);
        var classId = item.AgentClassId ?? project?.DefaultAgentClass;
        if (classId is null) return null;
        if (!cfg.Catalog.TryGetValue(classId, out var agentClass)) return null;

        DateTimeOffset? earliest = null;
        foreach (var member in agentClass.Members)
        {
            // PayPerApi members never park on quota.
            if (member.Billing == AgentBilling.PayPerApi) continue;
            // Skip members the eligibility gates already rule out — there is no
            // point waiting for their quota to reset when they would still be
            // rejected at routing time.
            if (member.QualityScore < item.MinModelScore) continue;
            if (!MemberCoversRequiredCapabilities(member, item.RequiredCapabilities)) continue;

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
            quota = (await ApplyBudgetAsync(member, quota, ct)).Quota;
            // Skip unknown (probe failed / no data) and members above the
            // threshold (would have been chosen by the router and so don't
            // need to gate park-time). Uses the fixed MinQuotaPct fallback
            // here — this path computes retry-scheduling park time, not the
            // dispatch gate, and a stable threshold keeps the retry hint
            // independent of where in the ramp the member happens to be.
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
        EffectiveQuota quota,
        DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var availablePct = quota.AvailablePct;
        var resetAt = quota.ResetAt;

        // The time-based ramp is only meaningful for Subscription members:
        // their AvailablePct is driven by the agent's quota window, and
        // <paramref name="resetAt"/> is that window's reset. PayPerApi has
        // no agent quota window — its AvailablePct is either 100% (probe)
        // or the operator's local-budget MIN, and the budget defines its
        // own reset cycle. Falling back to the fixed MinQuotaPct keeps the
        // operator's spend cap honest regardless of where in the agent
        // window we happen to be.
        var floor = member.Billing == AgentBilling.Subscription
            ? ComputeEffectiveFloorPct(member.Agent, resetAt, nowUtc)
            : _opts.MinQuotaPct;
        if (availablePct >= floor)
        {
            // Per-window floor check sits alongside the aggregated/time-ramp
            // floor: a small window (e.g. claude five_hour) can be the binding
            // constraint during a burst even when the aggregated reading is
            // healthy, because 10 % of a 5 h window is thin headroom relative
            // to MaxConcurrent + cache-staleness overshoot. Only applies to
            // Subscription members — PayPerApi has no provider window concept.
            if (member.Billing == AgentBilling.Subscription
                && quota.Windows is { Count: > 0 } windows)
            {
                foreach (var w in windows)
                {
                    if (w.AvailablePct < 0) continue; // unknown — gated by aggregated check above
                    var windowFloor = ResolveWindowFloorPct(w.Name);
                    if (w.AvailablePct < windowFloor)
                    {
                        return new QuotaGateDecision(
                            false,
                            $"quota below window floor ({w.Name}: {w.AvailablePct:F1}% < {windowFloor:F1}%)");
                    }
                }
            }

            var rateAware = await EvaluateRateAwareGateAsync(member, availablePct, ct);
            return rateAware ?? new QuotaGateDecision(true, "quota available");
        }

        if (availablePct >= 0)
            return new QuotaGateDecision(false, $"quota below floor ({availablePct:F1}% < {floor:F1}%)");

        return _opts.UnknownPolicy switch
        {
            QuotaUnknownPolicy.FailOpen => new QuotaGateDecision(true, "quota unknown; fail-open"),
            QuotaUnknownPolicy.FailCautious => new QuotaGateDecision(false, "quota unknown; fail-cautious"),
            _ => await EvaluateObservedFailuresAsync(member, ct),
        };
    }

    private void RecordAvailabilityAndMaybeNotify(
        AgentMembership member,
        EffectiveQuota quota,
        QuotaGateDecision gate)
    {
        var key = RecordObservedAvailability(member, quota);
        if (RecordQuotaUsableTransition(key, gate.Allow))
            NotifyQuotaUsableThresholdCrossed();
    }

    private (AgentKind Agent, string ModelId) RecordObservedAvailability(
        AgentMembership member,
        EffectiveQuota quota)
    {
        var key = (member.Agent, member.ModelId ?? string.Empty);
        _lastAvailablePct[key] = quota.AvailablePct;
        return key;
    }

    private void NotifyQuotaUsableThresholdCrossed()
    {
        var handlers = QuotaUsableThresholdCrossed;
        if (handlers is null)
            return;

        foreach (Action handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Quota usable threshold subscriber threw; routing decision will continue");
            }
        }
    }

    private bool RecordQuotaUsableTransition((AgentKind Agent, string ModelId) key, bool isUsable)
    {
        while (true)
        {
            if (!_lastQuotaUsable.TryGetValue(key, out var previous))
            {
                if (_lastQuotaUsable.TryAdd(key, isUsable))
                    return false;
                continue;
            }

            if (previous == isUsable)
                return false;

            if (_lastQuotaUsable.TryUpdate(key, isUsable, previous))
                return !previous && isUsable;
        }
    }

    /// <summary>
    /// Returns the absolute floor for one provider window name (e.g. <c>five_hour</c>).
    /// Looks up <see cref="QuotaRouterOptions.MinQuotaPctByWindow"/>; falls
    /// back to <see cref="QuotaRouterOptions.MinQuotaPct"/> when the window is
    /// not listed. Case-insensitive match because providers vary on snake_case
    /// vs <c>5h-rolling</c> style names.
    /// </summary>
    internal double ResolveWindowFloorPct(string windowName)
    {
        if (string.IsNullOrEmpty(windowName)) return _opts.MinQuotaPct;
        if (_opts.MinQuotaPctByWindow is { } overrides
            && overrides.TryGetValue(windowName, out var perWindow))
            return perWindow;
        return _opts.MinQuotaPct;
    }

    /// <summary>
    /// Returns the effective quota floor for <paramref name="agent"/> at
    /// <paramref name="nowUtc"/> given the soonest known reset
    /// <paramref name="resetAt"/>. Replaces the fixed
    /// <see cref="QuotaRouterOptions.MinQuotaPct"/> with a ramp from
    /// <see cref="QuotaRouterOptions.StartFloorPct"/> (just after reset) down to
    /// <see cref="QuotaRouterOptions.EndFloorPct"/> (as reset approaches), so
    /// the gate preserves headroom for the operator's interactive session
    /// early in the window and drains the surplus before the weekly reset
    /// at the end. Falls back to <see cref="QuotaRouterOptions.MinQuotaPct"/>
    /// when no <paramref name="resetAt"/> or no ramp window is known for
    /// the agent — the original fixed-floor behaviour.
    /// <para>
    /// <paramref name="resetAt"/> is the binding-window reset surfaced by
    /// the probe (i.e. the same one the gate already keys on); the helper
    /// trusts the probe to have aggregated multi-window snapshots before
    /// handing one up. <c>fractionElapsed</c> is clamped to [0, 1] so a
    /// stale reset in the past or a reset further in the future than the
    /// configured window cannot push the floor past the endpoints.
    /// </para>
    /// </summary>
    internal double ComputeEffectiveFloorPct(AgentKind agent, DateTimeOffset? resetAt, DateTimeOffset nowUtc)
    {
        if (resetAt is not { } reset) return _opts.MinQuotaPct;
        var rampWindow = GetRampWindow(agent);
        if (rampWindow <= TimeSpan.Zero) return _opts.MinQuotaPct;

        var untilReset = reset - nowUtc;
        var fractionElapsed = 1.0 - untilReset.TotalSeconds / rampWindow.TotalSeconds;
        if (double.IsNaN(fractionElapsed) || double.IsInfinity(fractionElapsed))
            return _opts.MinQuotaPct;
        fractionElapsed = Math.Clamp(fractionElapsed, 0.0, 1.0);

        var floor = _opts.StartFloorPct + (_opts.EndFloorPct - _opts.StartFloorPct) * fractionElapsed;
        var lo = Math.Min(_opts.StartFloorPct, _opts.EndFloorPct);
        var hi = Math.Max(_opts.StartFloorPct, _opts.EndFloorPct);
        return Math.Clamp(floor, lo, hi);
    }

    private TimeSpan GetRampWindow(AgentKind agent)
    {
        if (_opts.RampWindowByAgent is { } overrides
            && overrides.TryGetValue(agent.Value, out var perAgent)
            && perAgent > TimeSpan.Zero)
            return perAgent;
        return _opts.RampWindow;
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
            fit = _opts.ColdStartFitInWindow;
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
        var cfg = Volatile.Read(ref _routingConfig);
        if (!cfg.Catalog.TryGetValue(classId, out var agentClass)) return results;
        if (_burnEstimator is null) return results;

        foreach (var member in agentClass.Members)
        {
            if (member.Billing == AgentBilling.PayPerApi) continue;

            AgentQuotaSnapshot snapshot;
            try { snapshot = await ProbeAsync(member, ct); }
            catch { continue; }
            var quota = ResolveMemberQuota(snapshot, member);
            quota = (await ApplyBudgetAsync(member, quota, ct)).Quota;

            AgentBurnEstimate est;
            try { est = await _burnEstimator.GetEstimateAsync(member.Agent, ct); }
            catch { est = new AgentBurnEstimate { AvgBurnPctPerItem = -1, SampleCount = 0 }; }

            double fit;
            if (est.SampleCount <= 0 || est.AvgBurnPctPerItem <= 0) fit = _opts.ColdStartFitInWindow;
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
    public IReadOnlyCollection<string> ClassIds => Volatile.Read(ref _routingConfig).Catalog.Keys.ToList();

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
            return new EffectiveQuota(snapshot.AvailablePct, snapshot.ResetAt, null, snapshot.Windows);

        if (snapshot.PerModel.TryGetValue(member.ModelId, out var modelQuota))
            return new EffectiveQuota(
                modelQuota.AvailablePct, modelQuota.ResetAt, modelQuota.Window,
                modelQuota.Windows.Count > 0 ? modelQuota.Windows : snapshot.Windows);

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
            return new EffectiveQuota(best!.AvailablePct, earliestReset, best.Window, snapshot.Windows);
        }

        // Unknown model id on a probe that DOES provide per-model data — the operator
        // configured a model the probe has no signal for. Fail safe: surface as
        // unknown so QuotaUnknownPolicy gates it, rather than silently falling back
        // to the overall account percentage.
        if (snapshot.PerModel.Count > 0)
            return new EffectiveQuota(-1, null, null);

        // Probe returned no per-model breakdown at all (e.g. NullQuotaProbe, or a
        // provider whose API has no per-model dimension). Fall back to overall.
        return new EffectiveQuota(snapshot.AvailablePct, snapshot.ResetAt, null, snapshot.Windows);
    }

    /// <summary>
    /// Returns true when <paramref name="member"/> declares every tag in
    /// <paramref name="required"/>. An empty <paramref name="required"/> list
    /// returns true (open-by-default). Comparison is ordinal, case-insensitive.
    /// </summary>
    internal static bool MemberCoversRequiredCapabilities(
        AgentMembership member, IReadOnlyList<string> required)
    {
        if (required.Count == 0) return true;
        var declared = member.Capabilities;
        if (declared.Count == 0) return false;
        foreach (var tag in required)
        {
            var hit = false;
            foreach (var have in declared)
            {
                if (string.Equals(have, tag, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }
            if (!hit) return false;
        }
        return true;
    }

    private static string DescribeIneligibility(AgentMembership member, WorkItem item)
    {
        var failsScore = member.QualityScore < item.MinModelScore;
        var failsCaps = !MemberCoversRequiredCapabilities(member, item.RequiredCapabilities);
        if (failsScore && failsCaps)
            return $"below floor ({member.QualityScore} < {item.MinModelScore}); " +
                   $"missing capabilities (required=[{string.Join(",", item.RequiredCapabilities)}], " +
                   $"declared=[{string.Join(",", member.Capabilities)}])";
        if (failsScore)
            return $"below floor ({member.QualityScore} < {item.MinModelScore})";
        return $"missing capabilities (required=[{string.Join(",", item.RequiredCapabilities)}], " +
               $"declared=[{string.Join(",", member.Capabilities)}])";
    }

    private static int ComputeTodModifier(IReadOnlyList<ParsedTodModifier> modifiers, AgentKind agent, DateTimeOffset nowUtc)
    {
        var total = 0;
        foreach (var mod in modifiers)
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

    private static string DescribeModifiers(IReadOnlyList<ParsedTodModifier> modifiers, AgentKind agent, DateTimeOffset nowUtc)
    {
        var parts = new List<string>();
        foreach (var mod in modifiers)
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

    /// <summary>
    /// Result of MIN-combining a probe quota with the local operator budget.
    /// <see cref="BudgetExhausted"/> is true only when a budget is configured and
    /// is itself below the gate threshold (or the provider failed and we fail
    /// closed), distinguishing a real spend-cap stop from a transient probe quirk.
    /// </summary>
    private readonly record struct BudgetAdjustedQuota(
        EffectiveQuota Quota, bool BudgetExhausted, DateTimeOffset? BudgetReset);
}

public sealed record EffectiveQuota(
    double AvailablePct,
    DateTimeOffset? ResetAt,
    string? Window,
    IReadOnlyList<WindowQuota>? Windows = null);

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
    /// True when no class member is eligible — either the legacy
    /// <see cref="WorkItem.MinModelScore"/> floor or the new
    /// <see cref="WorkItem.RequiredCapabilities"/> gate (or both) excludes
    /// every member. The caller must fail the item immediately rather than
    /// waiting or routing.
    /// </summary>
    public bool NoEligibleMembers { get; init; }

    /// <summary>
    /// True when the router invoked the caller's per-agent slot reservation
    /// callback for the chosen member and it succeeded — the caller does NOT
    /// need to (and must not) re-reserve, and must release the slot on every
    /// exit path. Always false when <see cref="Chosen"/> is null or when no
    /// reservation callback was supplied.
    /// </summary>
    public bool SlotReserved { get; init; }

    /// <summary>
    /// True when at least one quota-passing member was blocked by its
    /// per-agent concurrency cap during this dispatch attempt (and the
    /// router could not spill to a free-and-eligible member). The caller
    /// should defer with a short cap-retry rather than the full quota
    /// recheck interval, since operator-configured caps free up much
    /// faster than a quota window resets. The router already applies that
    /// shorter delay via <see cref="SuggestedRecheckIn"/>; callers that
    /// honour <see cref="SuggestedRecheckIn"/> directly do not need to
    /// branch on this flag.
    /// <para>
    /// Any-at-cap (not all-at-cap): even one cap-blocked member means a
    /// worker finishing on that agent will free up a routable slot within
    /// the cap-retry window. Use <see cref="AtCapAgents"/> for the precise
    /// per-agent breakdown when emitting audit events.
    /// </para>
    /// </summary>
    public bool AnyMemberAtCap { get; init; }

    /// <summary>
    /// Agents whose per-agent concurrency cap blocked them this dispatch.
    /// Populated when the router spilled past or deferred due to a cap;
    /// empty otherwise. Used by the caller to emit per-agent audit events
    /// without re-deriving which members were blocked.
    /// </summary>
    public IReadOnlyList<AgentKind> AtCapAgents { get; init; } = [];
}

/// <summary>
/// Configuration for the quota-aware agent class router.
/// Bound from <c>CodeyBox:QuotaRouter</c>.
/// </summary>
public sealed class QuotaRouterOptions
{
    /// <summary>
    /// Fallback floor used when the time-based ramp can't be computed — the
    /// probe didn't surface a <c>ResetAt</c>, or no <see cref="RampWindow"/>
    /// is configured for the member's agent. Members below this threshold
    /// are skipped in favour of the next class member. Default 10.
    /// <para>
    /// When the ramp IS computable, <see cref="StartFloorPct"/> /
    /// <see cref="EndFloorPct"/> drive the effective floor instead — see
    /// <see cref="AgentClassRouter.ComputeEffectiveFloorPct(AgentKind, DateTimeOffset?, DateTimeOffset)"/>.
    /// </para>
    /// </summary>
    public double MinQuotaPct { get; set; } = 10.0;

    /// <summary>
    /// Per-window absolute floors, keyed by <see cref="WindowQuota.Name"/>
    /// (e.g. <c>five_hour</c>, <c>seven_day</c>). When a snapshot surfaces
    /// per-window readings, dispatch requires EVERY window's
    /// <see cref="WindowQuota.AvailablePct"/> to be at or above its window's
    /// floor; an unlisted window falls back to <see cref="MinQuotaPct"/>.
    /// Sits alongside (not under) the time-ramped floor computed in
    /// <see cref="AgentClassRouter.ComputeEffectiveFloorPct(AgentKind, DateTimeOffset?, DateTimeOffset)"/>:
    /// the ramp governs the aggregated min-across-windows reading, while this
    /// map governs each window independently so a small window like
    /// <c>five_hour</c> can hold more headroom than its share of the aggregate
    /// would imply. The 5h window has a far smaller absolute budget than 7d,
    /// so 10% of 5h is thin headroom under bursty dispatch (MaxConcurrent + the
    /// 60 s cache TTL of in-flight overshoot) where 10% of 7d is large; the
    /// 5h floor should cover that overshoot — default ~25% for MaxConcurrent=4
    /// (see <c>QuotaRouterConfig.MinQuotaPctByWindow</c> for the bound defaults).
    /// Hot-reloadable.
    /// </summary>
    public Dictionary<string, double> MinQuotaPctByWindow { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Early-window floor for the time-based ramp: the effective minimum
    /// available-quota percentage just after the quota window resets. Higher
    /// than <see cref="EndFloorPct"/> so the operator's own interactive
    /// session and monitoring keep headroom on a freshly-reset week. Default 25.
    /// </summary>
    public double StartFloorPct { get; set; } = 25.0;

    /// <summary>
    /// Late-window floor for the time-based ramp: the effective minimum
    /// available-quota percentage as the quota window approaches reset. Low
    /// enough to drain otherwise-stranded quota right before the use-it-or-
    /// lose-it reset. Default 3.
    /// </summary>
    public double EndFloorPct { get; set; } = 3.0;

    /// <summary>
    /// Default length of the quota window used to compute the time-based
    /// floor ramp when the probe surfaces a <c>ResetAt</c>. The fraction-
    /// elapsed is <c>1 - timeUntilReset / RampWindow</c>. Default 7 days
    /// (claude/codex weekly cap). Override per agent via
    /// <see cref="RampWindowByAgent"/> when an agent's binding window differs.
    /// </summary>
    public TimeSpan RampWindow { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Per-agent override for the ramp window length, keyed by
    /// <see cref="AgentKind.Value"/>. When an agent is not present here the
    /// global <see cref="RampWindow"/> is used. Lets the operator wire a
    /// 7-day window for one agent and a 24h window for another without
    /// touching code.
    /// </summary>
    public Dictionary<string, TimeSpan> RampWindowByAgent { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Suggested recheck delay surfaced by <see cref="AgentClassRouter.ResolveAsync"/>
    /// when every eligible candidate was blocked by its per-agent concurrency
    /// cap rather than quota exhaustion. Short enough that the deferred item is
    /// reconsidered as soon as another worker on any of those agents finishes;
    /// long enough not to busy-loop. Default 15s.
    /// </summary>
    public TimeSpan CapRetryRecheckInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Default "how many concurrent burns fit in the remaining quota window"
    /// used when the estimator has no historical samples yet. Keeps the
    /// dispatch queue from stalling on cold start. Default 2.0.
    /// </summary>
    public double ColdStartFitInWindow { get; set; } = 2.0;
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

using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Thread-safe escrow ledger that closes the quota-gate TOCTOU: probe snapshots
/// (<see cref="IAgentQuotaProbe.GetAvailabilityAsync"/>) are periodic and may be
/// served from cache, so N workers dispatching between two refreshes would each
/// evaluate against the same reading and each pass. The ledger tracks the
/// estimated cost of authorised-but-not-yet-observed dispatches per quota
/// account and gates on <c>last_reading - sum(outstanding)</c> instead of the
/// raw reading.
///
/// <para>
/// Lifecycle: <see cref="TryReserve"/> atomically re-checks the floor and records
/// an estimate when a dispatch is authorised; <see cref="Complete"/> swaps the
/// estimate for observed usage when the phase ends (<see cref="Release"/> drops
/// it outright); <see cref="NoteProbeReading"/> retires completed entries once a
/// newer probe reading has observed the actual consumption (no double-counting);
/// <see cref="SweepExpired"/> reaps orphans whose worker died without releasing.
/// All release paths are idempotent and safe to call with anything.
/// </para>
///
/// <para>
/// Keying is pool-aware: members of a configured quota pool share one ledger
/// account (<see cref="QuotaReservationLedger.PoolReservationKey"/>) so
/// concurrent workers drawing on one subscription cannot jointly overshoot
/// its floor; members with no pool keep the legacy per-agent key
/// (<see cref="QuotaReservationLedger.DefaultReservationKey"/> returns the
/// member's <see cref="AgentMembership.RouteKey"/>). The constructor's
/// <c>keyProvider</c> only applies to the legacy path.
/// </para>
/// </summary>
public sealed class QuotaReservationLedger
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ReservationEntry> _entries = new();
    private readonly Dictionary<string, ProbeMark> _lastReading = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SettlementCounters> _settlements = new(StringComparer.Ordinal);
    private readonly Func<AgentMembership, string> _keyProvider;
    private readonly QuotaRouterOptions _options;
    private readonly TimeProvider _time;

    /// <summary>
    /// Raised after reservation entries change (reserve, settle, release,
    /// probe-driven retirement, expiry sweep). Raised outside the ledger lock,
    /// so handlers must not call back into the ledger while holding their own
    /// locks that ledger callbacks could also need. Intended for
    /// observability and for tests to wait on ledger state without polling
    /// wall-clock time.
    /// </summary>
    public event Action? ReservationsChanged;

    private void RaiseReservationsChanged() => ReservationsChanged?.Invoke();

    /// <summary>
    /// Creates a ledger keyed by <see cref="AgentMembership.RouteKey"/> with
    /// default reservation settings.
    /// </summary>
    public QuotaReservationLedger(TimeProvider? time = null)
        : this(new QuotaRouterOptions(), time, DefaultReservationKey)
    {
    }

    /// <summary>
    /// Creates a ledger reading live reservation knobs from
    /// <paramref name="options"/> (the shared hot-reload instance, so operator
    /// edits apply without a restart), keyed by <paramref name="keyProvider"/>.
    /// </summary>
    public QuotaReservationLedger(
        QuotaRouterOptions options,
        TimeProvider? time = null,
        Func<AgentMembership, string>? keyProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _time = time ?? TimeProvider.System;
        _keyProvider = keyProvider ?? DefaultReservationKey;
    }

    /// <summary>
    /// Default account key: the member's stable route key (bare agent kind for
    /// legacy members, <c>agent/instance</c> for named instances). Keeps the
    /// existing per-agent keying.
    /// </summary>
    public static string DefaultReservationKey(AgentMembership member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.RouteKey;
    }

    /// <summary>
    /// Canonical ledger key for a quota pool. All members of the pool share
    /// this key, so one member's authorised dispatch is visible to the gate
    /// evaluating another. Pure.
    /// </summary>
    public static string PoolReservationKey(string poolName)
    {
        ArgumentNullException.ThrowIfNull(poolName);
        return "pool:" + poolName.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Resolves the ledger account key for <paramref name="member"/>: the
    /// pool key when the member belongs to a configured pool, otherwise the
    /// constructor's <c>keyProvider</c> (legacy per-agent keying). Reads the
    /// live shared options so hot-reloaded pool membership applies without a
    /// restart. Pure apart from the options read.
    /// </summary>
    private string ResolveKey(AgentMembership member)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (QuotaPoolResolver.TryResolvePool(_options, member, out var poolName, out _, out _)
            && poolName is not null)
            return PoolReservationKey(poolName);
        return _keyProvider(member);
    }

    /// <summary>
    /// Resolves the reservation estimate for one dispatch, in quota-percentage
    /// points. A missing, zero, negative, or non-finite
    /// <paramref name="rawEstimatePct"/> falls back to the configured default
    /// (never silently reserves nothing); the result is clamped to
    /// [<paramref name="minPct"/>, <paramref name="maxPct"/>]. Pure.
    /// </summary>
    public static double ResolveEstimatePct(
        double? rawEstimatePct, double fallbackPct, double minPct, double maxPct)
    {
        var lo = Math.Min(minPct, maxPct);
        var hi = Math.Max(minPct, maxPct);
        var candidate = rawEstimatePct is { } raw
            && double.IsFinite(raw)
            && raw > 0
            ? raw
            : fallbackPct;
        if (!double.IsFinite(candidate) || candidate <= 0)
            candidate = lo > 0 ? lo : hi;
        if (!double.IsFinite(candidate) || candidate <= 0)
            return 0;
        return Math.Clamp(candidate, lo, hi);
    }

    /// <summary>Effective availability after escrow: reading minus outstanding. Pure.</summary>
    public static double EffectiveAvailablePct(double readingPct, double outstandingPct) =>
        readingPct - Math.Max(0, outstandingPct);

    /// <summary>Floor check on the escrow-adjusted availability. Pure.</summary>
    public static bool MeetsFloor(double effectiveAvailablePct, double floorPct) =>
        effectiveAvailablePct >= floorPct;

    /// <summary>
    /// Which configuration tier produced a reservation estimate. Surfaced so
    /// deny reasons and operators can tell a measured burn apart from a
    /// hand-tuned constant.
    /// </summary>
    public enum QuotaReservationEstimateSource
    {
        /// <summary>Pool <c>ReservationEstimate</c> percentage.</summary>
        PoolEstimate,
        /// <summary>Pool <c>ReservationEstimateItems</c> converted via measured burn.</summary>
        PoolItemsEstimate,
        /// <summary>Explicit per-call override.</summary>
        ExplicitOverride,
        /// <summary>Per-agent <c>DispatchReservationEstimatePctByAgent</c> entry.</summary>
        AgentEstimate,
        /// <summary>Per-agent <c>DispatchReservationEstimateItemsByAgent</c> entry via measured burn.</summary>
        AgentItemsEstimate,
        /// <summary>Global <c>DispatchReservationEstimateItems</c> via measured burn.</summary>
        GlobalItemsEstimate,
        /// <summary>Measured per-item burn × <c>DispatchReservationBurnMultiplier</c>.</summary>
        MeasuredBurn,
        /// <summary>Global <c>DispatchReservationEstimatePct</c> fallback.</summary>
        GlobalEstimate,
    }

    /// <summary>
    /// A resolved reservation estimate with its source tier. The estimate is
    /// already clamped to the configured min/max bounds.
    /// </summary>
    public sealed record QuotaReservationResolution(
        double EstimatePct,
        QuotaReservationEstimateSource Source,
        double? MeasuredBurnPctPerItem = null,
        int MeasuredSamples = 0);

    /// <summary>
    /// Resolves the reservation estimate for one dispatch, in quota-percentage
    /// points, honouring the same tier order as the legacy percentage chain —
    /// pool override &gt; explicit override &gt; per-agent &gt; global — with
    /// two work-denominated additions: within the pool, agent, and global
    /// tiers an items quantity (converted through measured burn) applies when
    /// no percentage is configured at that tier, and the measured burn (with
    /// the safety multiplier) displaces only the unconfigured global default.
    /// Any explicitly configured tier therefore still wins over the derived
    /// value, and with no measured history the result is identical to the
    /// legacy chain. The winning estimate is clamped to
    /// [<c>DispatchReservationMinPct</c>, <c>DispatchReservationMaxPct</c>].
    /// Depleting-balance pools resolve exactly as before (their estimate is
    /// already absolute; items quantities never apply to them). Pure.
    /// </summary>
    public static QuotaReservationResolution ResolveEstimate(
        QuotaRouterOptions options,
        AgentMembership member,
        double? estimateOverride = null,
        AgentBurnEstimate? measuredBurn = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(member);
        var minSamples = options.DispatchReservationBurnMinSamples;

        if (QuotaPoolResolver.TryResolvePool(options, member, out _, out var pool, out _)
            && pool is not null)
        {
            if (pool.Kind == QuotaPoolKind.DepletingBalance)
            {
                var poolRaw = estimateOverride is { } poolOverride
                    && double.IsFinite(poolOverride)
                    && poolOverride > 0
                    ? poolOverride
                    : pool.ReservationEstimate;
                var candidate = poolRaw is { } r && double.IsFinite(r) && r > 0
                    ? r
                    : options.DispatchReservationEstimatePct;
                var clamped = double.IsFinite(candidate) && candidate > 0 ? candidate : 0;
                return new QuotaReservationResolution(clamped, QuotaReservationEstimateSource.PoolEstimate);
            }

            if (pool.ReservationEstimate is { } poolPct
                && double.IsFinite(poolPct)
                && poolPct > 0)
                return ClampResolution(poolPct, QuotaReservationEstimateSource.PoolEstimate, options, measuredBurn);
            if (measuredBurn?.ToItemsPct(pool.ReservationEstimateItems, minSamples) is { } poolItemsPct)
                return ClampResolution(
                    poolItemsPct, QuotaReservationEstimateSource.PoolItemsEstimate, options, measuredBurn);
        }

        if (estimateOverride is { } raw
            && double.IsFinite(raw)
            && raw > 0)
            return ClampResolution(raw, QuotaReservationEstimateSource.ExplicitOverride, options, measuredBurn);

        if (!string.IsNullOrEmpty(member.Agent.Value))
        {
            if (options.DispatchReservationEstimatePctByAgent.TryGetValue(member.Agent.Value, out var agentRaw)
                && double.IsFinite(agentRaw)
                && agentRaw > 0)
                return ClampResolution(agentRaw, QuotaReservationEstimateSource.AgentEstimate, options, measuredBurn);
            if (options.DispatchReservationEstimateItemsByAgent.TryGetValue(member.Agent.Value, out var agentItems)
                && measuredBurn?.ToItemsPct(agentItems, minSamples) is { } agentItemsPct)
                return ClampResolution(
                    agentItemsPct, QuotaReservationEstimateSource.AgentItemsEstimate, options, measuredBurn);
        }

        if (measuredBurn?.ToItemsPct(options.DispatchReservationEstimateItems, minSamples) is { } globalItemsPct)
            return ClampResolution(
                globalItemsPct, QuotaReservationEstimateSource.GlobalItemsEstimate, options, measuredBurn);

        if (measuredBurn is { } burn
            && burn.HasMeasuredBurn(minSamples)
            && double.IsFinite(options.DispatchReservationBurnMultiplier)
            && options.DispatchReservationBurnMultiplier > 0)
        {
            var derived = burn.AvgBurnPctPerItem * options.DispatchReservationBurnMultiplier;
            if (double.IsFinite(derived) && derived > 0)
                return ClampResolution(
                    derived, QuotaReservationEstimateSource.MeasuredBurn, options, measuredBurn);
        }

        return ClampResolution(
            options.DispatchReservationEstimatePct,
            QuotaReservationEstimateSource.GlobalEstimate,
            options,
            measuredBurn,
            fallbackToBounds: true);
    }

    private static QuotaReservationResolution ClampResolution(
        double candidate,
        QuotaReservationEstimateSource source,
        QuotaRouterOptions options,
        AgentBurnEstimate? measuredBurn,
        bool fallbackToBounds = false)
    {
        var lo = Math.Min(options.DispatchReservationMinPct, options.DispatchReservationMaxPct);
        var hi = Math.Max(options.DispatchReservationMinPct, options.DispatchReservationMaxPct);
        var value = candidate;
        if (!double.IsFinite(value) || value <= 0)
        {
            if (!fallbackToBounds)
                value = options.DispatchReservationEstimatePct;
            if (!double.IsFinite(value) || value <= 0)
                value = lo > 0 ? lo : hi;
        }
        if (!double.IsFinite(value) || value <= 0)
            return new QuotaReservationResolution(0, source, measuredBurn?.AvgBurnPctPerItem, measuredBurn?.SampleCount ?? 0);
        return new QuotaReservationResolution(
            Math.Clamp(value, lo, hi), source, measuredBurn?.AvgBurnPctPerItem, measuredBurn?.SampleCount ?? 0);
    }

    /// <summary>
    /// Converts persisted per-item usage into quota-percentage points against
    /// an operator-configured window token budget. Returns null when there is
    /// no positive budget to divide by — the caller must then keep the
    /// estimate (or release it) rather than reconcile against a guess. Pure.
    /// </summary>
    public static double? ToObservedPct(WorkItemUsageTotal? total, long tokenBudget)
    {
        if (total is null || tokenBudget <= 0) return null;
        var tokens = Math.Max(0L, total.TokensInput)
            + Math.Max(0L, total.TokensOutput)
            + Math.Max(0L, total.TokensReasoning);
        if (tokens <= 0) return 0;
        var pct = (double)tokens / tokenBudget * 100.0;
        return double.IsFinite(pct) && pct >= 0 ? pct : null;
    }

    /// <summary>
    /// Atomically re-checks the floor and, when headroom covers a full
    /// estimate, records an estimated-cost reservation. The floor is a reserve
    /// that must REMAIN after escrow (<c>available - outstanding - estimate
    /// &gt;= floor</c>); merely requiring the pre-escrow headroom to sit above
    /// the floor would still let one estimate carry the pool below it. The
    /// check and the escrow happen under one lock, so N racers sharing a cached
    /// probe reading authorise only as many dispatches as the headroom covers.
    ///
    /// <para>
    /// The estimate resolves through <see cref="ResolveEstimate"/>: pass the
    /// member's measured burn in <paramref name="measuredBurn"/> to let history
    /// displace the configured default (explicit overrides still win), and the
    /// binding window name in <paramref name="bindingWindow"/> so the refusal
    /// names the constraint instead of reading as generic exhaustion.
    /// </para>
    ///
    /// <para>
    /// Unknown readings (<paramref name="availablePct"/> negative) carry no
    /// baseline to escrow against: the attempt allows without recording, and the
    /// caller's unknown-policy decision stands on its own.
    /// </para>
    /// </summary>
    public QuotaReservationAttempt TryReserve(
        AgentMembership member,
        double availablePct,
        double floorPct,
        double? estimatePctOverride = null,
        AgentBurnEstimate? measuredBurn = null,
        string? bindingWindow = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        if (availablePct < 0)
            return new QuotaReservationAttempt(true, null, 0, availablePct, null);

        var key = ResolveKey(member);
        var resolution = ResolveEstimate(_options, member, estimatePctOverride, measuredBurn);
        var estimate = resolution.EstimatePct;
        var bindingSuffix = FormatBindingSuffix(bindingWindow);
        var estimateDetail = FormatEstimateDetail(resolution, _options);
        QuotaReservationAttempt attempt;
        var reserved = false;
        lock (_sync)
        {
            var outstanding = SumOutstandingLocked(key);
            var effective = EffectiveAvailablePct(availablePct, outstanding);
            if (!MeetsFloor(effective - estimate, floorPct))
            {
                attempt = new QuotaReservationAttempt(
                    false, null, outstanding, effective,
                    $"quota reservation of {estimate:F1}%{estimateDetail} would breach floor ({effective - estimate:F1}% < {floorPct:F1}%; {outstanding:F1}% already escrowed{bindingSuffix})");
            }
            else
            {
                var now = _time.GetUtcNow();
                var entry = new ReservationEntry
                {
                    Id = Guid.NewGuid(),
                    Key = key,
                    ReservedPct = estimate,
                    CreatedAt = now,
                };
                _entries[entry.Id] = entry;
                var lease = new QuotaReservationLease(
                    entry.Id, key, member.Agent, member.ModelId, estimate, now);
                attempt = new QuotaReservationAttempt(true, lease, outstanding + estimate, effective, null);
                reserved = true;
            }
        }
        if (reserved) RaiseReservationsChanged();
        return attempt;
    }

    private static string FormatEstimateDetail(
        QuotaReservationResolution resolution, QuotaRouterOptions options)
    {
        if (resolution.Source == QuotaReservationEstimateSource.MeasuredBurn
            && resolution.MeasuredBurnPctPerItem is { } burn
            && double.IsFinite(burn)
            && burn > 0)
            return $" (measured burn {burn:F1}% x{options.DispatchReservationBurnMultiplier:F1}, {resolution.MeasuredSamples} samples)";
        return string.Empty;
    }

    private static string FormatBindingSuffix(string? bindingWindow)
    {
        if (string.IsNullOrWhiteSpace(bindingWindow)) return string.Empty;
        var name = bindingWindow.Trim();
        if (name.Length > QuotaWindowBinding.MaxWindowNameLength)
            name = name[..QuotaWindowBinding.MaxWindowNameLength];
        return $"; binding window '{name}'";
    }

    /// <summary>
    /// Current escrowed total for <paramref name="member"/>'s account, in
    /// quota-percentage points. Includes reconciled (completed but not yet
    /// superseded) entries — those are still-unobserved consumption.
    /// </summary>
    public double GetOutstandingPct(AgentMembership member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return GetOutstandingPct(ResolveKey(member));
    }

    /// <summary>Current escrowed total for a raw account <paramref name="key"/>.</summary>
    public double GetOutstandingPct(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_sync)
        {
            return SumOutstandingLocked(key);
        }
    }

    /// <summary>Number of live escrow entries for <paramref name="member"/>'s account.</summary>
    public int GetReservationCount(AgentMembership member)
    {
        ArgumentNullException.ThrowIfNull(member);
        var key = ResolveKey(member);
        lock (_sync)
        {
            var count = 0;
            foreach (var entry in _entries.Values)
            {
                if (string.Equals(entry.Key, key, StringComparison.Ordinal)) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Drops the reservation outright. Idempotent and safe to call with null or
    /// an unknown id (returns false) — release sites must never throw.
    /// </summary>
    public bool Release(QuotaReservationLease? lease) =>
        lease is not null && Release(lease.Id);

    /// <summary>
    /// Drops the reservation with <paramref name="reservationId"/>. Idempotent;
    /// unknown ids return false.
    /// </summary>
    public bool Release(Guid reservationId)
    {
        bool removed;
        lock (_sync)
        {
            removed = _entries.Remove(reservationId);
        }
        if (removed) RaiseReservationsChanged();
        return removed;
    }

    /// <summary>
    /// Reconciles the estimate against observed usage when the phase ends: a
    /// positive <paramref name="observedPct"/> replaces the estimate (kept
    /// escrowed until a newer probe reading supersedes it); a missing, zero,
    /// negative, or non-finite observation releases the entry — there is no
    /// measured consumption left to protect. Idempotent; unknown ids return false.
    /// This overload is for callers that cannot say whether the observation
    /// came from extracted token usage; prefer
    /// <see cref="Complete(QuotaReservationLease?, double?, bool)"/> and pass
    /// <c>false</c> for <c>hasObservedUsage</c> when the run produced cost rows
    /// without extracted token usage so the estimate is retained instead of
    /// released.
    /// </summary>
    public bool Complete(QuotaReservationLease? lease, double? observedPct) =>
        lease is not null && Complete(lease.Id, observedPct);

    /// <summary>
    /// Reconciles the estimate for <paramref name="reservationId"/>; see
    /// <see cref="Complete(QuotaReservationLease?, double?)"/>.
    /// </summary>
    public bool Complete(Guid reservationId, double? observedPct) =>
        Complete(reservationId, observedPct, hasObservedUsage: true);

    /// <summary>
    /// Settles the reservation for <paramref name="lease"/> against observed
    /// usage only when <paramref name="hasObservedUsage"/> is set — i.e. at
    /// least one cost row for the run carries extracted token usage
    /// (<c>has_extracted_token_usage</c>). When it is not set the run really
    /// consumed quota but nothing measured how much, so the original estimate
    /// is retained as the settled cost (marked completed so a newer probe
    /// reading can still supersede it) instead of releasing the whole
    /// reservation as if the run were free. Never releases more quota than was
    /// reserved. Idempotent; unknown ids return false.
    /// </summary>
    public bool Complete(QuotaReservationLease? lease, double? observedPct, bool hasObservedUsage) =>
        lease is not null && Complete(lease.Id, observedPct, hasObservedUsage);

    /// <summary>
    /// Settles the reservation for <paramref name="reservationId"/>; see
    /// <see cref="Complete(QuotaReservationLease?, double?, bool)"/>.
    /// </summary>
    public bool Complete(Guid reservationId, double? observedPct, bool hasObservedUsage)
    {
        bool settled;
        lock (_sync)
        {
            if (!_entries.TryGetValue(reservationId, out var entry)) return false;
            if (!hasObservedUsage)
            {
                entry.CompletedAt = _time.GetUtcNow();
                RecordSettlementLocked(entry, fromActuals: false);
                settled = true;
            }
            else if (observedPct is not { } observed
                || !double.IsFinite(observed)
                || observed <= 0)
            {
                _entries.Remove(reservationId);
                settled = true;
            }
            else
            {
                entry.ReservedPct = observed;
                entry.CompletedAt = _time.GetUtcNow();
                RecordSettlementLocked(entry, fromActuals: true, settledPct: observed);
                settled = true;
            }
        }
        RaiseReservationsChanged();
        return settled;
    }

    /// <summary>
    /// Records a fresh probe reading for <paramref name="member"/>'s account and
    /// retires reconciled entries it supersedes: once a reading observed at
    /// <paramref name="observedAt"/> exists, completed entries from before that
    /// moment are already reflected in the provider number and must not be
    /// subtracted again. Unknown readings (negative) supersede nothing. Still-
    /// running (uncompleted) entries are never retired by a reading.
    /// </summary>
    public void NoteProbeReading(AgentMembership member, double availablePct, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(member);
        NoteProbeReading(ResolveKey(member), availablePct, observedAt);
    }

    /// <summary>
    /// Records a fresh probe reading for a raw account <paramref name="key"/>; see
    /// <see cref="NoteProbeReading(AgentMembership, double, DateTimeOffset)"/>.
    /// </summary>
    public void NoteProbeReading(string key, double availablePct, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (availablePct < 0) return;
        bool retiredAny;
        lock (_sync)
        {
            _lastReading[key] = new ProbeMark(availablePct, observedAt);
            List<Guid>? retire = null;
            foreach (var (id, entry) in _entries)
            {
                if (!string.Equals(entry.Key, key, StringComparison.Ordinal)) continue;
                if (entry.CompletedAt is { } completed && completed <= observedAt)
                    (retire ??= []).Add(id);
            }
            if (retire is null) return;
            foreach (var id in retire) _entries.Remove(id);
            retiredAny = retire.Count > 0;
        }
        if (retiredAny) RaiseReservationsChanged();
    }

    /// <summary>
    /// Removes entries older than the configured <c>QuotaReservationMaxAge</c>.
    /// Backstop for workers that died without releasing; prompt release still
    /// flows through the worker-slot lifecycle. Returns the number removed.
    /// </summary>
    public int SweepExpired() => SweepExpired(_time.GetUtcNow());

    /// <summary>Removes entries older than <c>QuotaReservationMaxAge</c> as of <paramref name="now"/>.</summary>
    public int SweepExpired(DateTimeOffset now)
    {
        var maxAge = _options.QuotaReservationMaxAge;
        if (maxAge <= TimeSpan.Zero) return 0;
        int removed;
        lock (_sync)
        {
            List<Guid>? expired = null;
            foreach (var (id, entry) in _entries)
            {
                if (now - entry.CreatedAt >= maxAge)
                    (expired ??= []).Add(id);
            }
            if (expired is null) return 0;
            foreach (var id in expired) _entries.Remove(id);
            removed = expired.Count;
        }
        if (removed > 0) RaiseReservationsChanged();
        return removed;
    }

    private double SumOutstandingLocked(string key)
    {
        var total = 0.0;
        foreach (var entry in _entries.Values)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
                total += entry.ReservedPct;
        }
        return total;
    }

    /// <summary>
    /// Settlement outcome for one pool account: how many reservations settled
    /// from measured usage versus how many were retained at the reserved
    /// estimate because no extracted token usage existed, plus the settled
    /// quota-percentage totals behind each count so the proportion of
    /// unmeasured spend is visible rather than silent. Pure snapshot.
    /// </summary>
    public sealed record QuotaReservationSettlementStats(
        long SettledFromActuals,
        long RetainedAtEstimate,
        double SettledFromActualsPct,
        double RetainedAtEstimatePct);

    /// <summary>
    /// Settlement counters for <paramref name="member"/>'s pool account.
    /// </summary>
    public QuotaReservationSettlementStats GetSettlementStats(AgentMembership member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return GetSettlementStats(ResolveKey(member));
    }

    /// <summary>Settlement counters for a raw pool account <paramref name="key"/>.</summary>
    public QuotaReservationSettlementStats GetSettlementStats(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_sync)
        {
            return SnapshotStatsLocked(key);
        }
    }

    /// <summary>
    /// Settlement counters for every pool account that has settled at least
    /// one reservation. Read-only snapshot.
    /// </summary>
    public IReadOnlyDictionary<string, QuotaReservationSettlementStats> GetAllSettlementStats()
    {
        lock (_sync)
        {
            var snapshot = new Dictionary<string, QuotaReservationSettlementStats>(
                _settlements.Count, StringComparer.Ordinal);
            foreach (var key in _settlements.Keys)
                snapshot[key] = SnapshotStatsLocked(key);
            return snapshot;
        }
    }

    private QuotaReservationSettlementStats SnapshotStatsLocked(string key) =>
        _settlements.TryGetValue(key, out var counters)
            ? new QuotaReservationSettlementStats(
                counters.SettledFromActuals,
                counters.RetainedAtEstimate,
                counters.SettledFromActualsPct,
                counters.RetainedAtEstimatePct)
            : new QuotaReservationSettlementStats(0, 0, 0, 0);

    private void RecordSettlementLocked(ReservationEntry entry, bool fromActuals, double settledPct = 0)
    {
        if (entry.SettlementRecorded) return;
        entry.SettlementRecorded = true;
        if (!_settlements.TryGetValue(entry.Key, out var counters))
        {
            counters = new SettlementCounters();
            _settlements[entry.Key] = counters;
        }

        if (fromActuals)
        {
            counters.SettledFromActuals++;
            counters.SettledFromActualsPct += settledPct;
        }
        else
        {
            counters.RetainedAtEstimate++;
            counters.RetainedAtEstimatePct += entry.ReservedPct;
        }
    }

    private sealed class SettlementCounters
    {
        public long SettledFromActuals { get; set; }
        public long RetainedAtEstimate { get; set; }
        public double SettledFromActualsPct { get; set; }
        public double RetainedAtEstimatePct { get; set; }
    }

    private sealed class ReservationEntry
    {
        public Guid Id { get; set; }
        public string Key { get; set; } = "";
        public double ReservedPct { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public bool SettlementRecorded { get; set; }
    }

    private readonly record struct ProbeMark(double AvailablePct, DateTimeOffset ObservedAt);
}

/// <summary>
/// Opaque handle for one escrowed dispatch. The snapshot
/// <see cref="ReservedPct"/> is informational — the ledger entry is mutated by
/// <see cref="QuotaReservationLedger.Complete(Guid, double?)"/> — so callers
/// must treat the id as the identity and never branch on the snapshot.
/// </summary>
public sealed record QuotaReservationLease(
    Guid Id,
    string Key,
    AgentKind Agent,
    string? ModelId,
    double ReservedPct,
    DateTimeOffset CreatedAt);

/// <summary>Outcome of one atomic gate-and-escrow attempt.</summary>
public sealed record QuotaReservationAttempt(
    bool Allowed,
    QuotaReservationLease? Lease,
    double OutstandingPct,
    double EffectiveAvailablePct,
    string? DenyReason);

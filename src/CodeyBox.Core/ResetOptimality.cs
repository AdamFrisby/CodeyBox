namespace CodeyBox.Core;

/// <summary>
/// Pure evaluator for the "should I spend a banked quota-reset credit now?"
/// decision. It composes the two upstream readings — the live quota snapshot
/// (surfaced by the quota probe, 1/5) and the derived banked-credit expiry
/// (<see cref="ResetCreditExpiryReport"/>, 2/5) — and encodes the operator's
/// reset-optimality algorithm. It is <b>report-only</b>: it emits a structured
/// <see cref="ResetSpendAdvice"/> and never notifies, triggers a reset, or
/// mutates state.
///
/// <para>The algorithm, with the corrections established from real Codex data:</para>
/// <list type="number">
/// <item><b>Burn-first.</b> Never advise spending while usable quota is above a
/// dust threshold. Applying a reset re-anchors the current window, so any quota
/// left in it at the moment of the reset is forfeited — you must burn the
/// current window down to dust before a reset stops wasting quota.</item>
/// <item><b>Re-anchor model.</b> Applying a banked reset sets the window to
/// <c>now + period</c> and <em>destroys</em> the upcoming natural reset (you'd
/// have gotten that fresh window for free). So only advise spending when the
/// natural reset would land <em>too late</em> to help — otherwise waiting for
/// the free natural reset both saves the credit and avoids forfeiting the
/// free window.</item>
/// <item><b>Predicted natural reset.</b> Codex's real reset is a fixed weekly
/// boundary (~Monday 06:00 UTC). The provider's <c>reset_at</c> field
/// OVER-PREDICTS, so the natural reset is predicted from a configured cadence
/// anchor + period (see <see cref="NaturalResetCadence"/>), never from the API
/// field.</item>
/// <item><b>Decision deadline.</b> <c>min(planEndsAt, nextCreditExpiresAt)</c> —
/// the latest moment at which spending the credit still has value AND is still
/// possible. Past the plan end more quota is worthless; past the credit's
/// advised spend-by the credit is gone.</item>
/// </list>
/// </summary>
public static class ResetOptimalityEvaluator
{
    /// <summary>
    /// Evaluates the reset-spend decision for <paramref name="agent"/> at
    /// <paramref name="now"/> from the current quota snapshot and the derived
    /// banked-credit expiry report. Pure and deterministic — the same inputs
    /// always yield the same advice.
    /// </summary>
    /// <param name="agent">Agent kind the readings belong to (e.g. <c>codex</c>).</param>
    /// <param name="quota">Live quota snapshot (1/5). An unknown snapshot yields
    /// <see cref="ResetAdviceReason.QuotaReadingUnavailable"/> — burn-first cannot
    /// be evaluated without a real usable-quota reading.</param>
    /// <param name="credits">Derived banked-credit expiry report (2/5). Drives
    /// whether there is a credit to spend and its advised spend-by moment.</param>
    /// <param name="config">Operator config: plan end, cadence anchor + period,
    /// dust threshold, time tolerance, and which agents the advisor covers.</param>
    /// <param name="now">Evaluation instant.</param>
    public static ResetSpendAdvice Evaluate(
        string agent,
        AgentQuotaSnapshot quota,
        ResetCreditExpiryReport credits,
        ResetOptimalityConfig config,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(quota);
        ArgumentNullException.ThrowIfNull(credits);
        ArgumentNullException.ThrowIfNull(config);

        var nextCreditExpiresAt = credits.NextCreditExpiresAt;
        var nextCreditIsEstimated = credits.NextCreditIsEstimated;
        double? usableQuotaPct = quota.IsKnown ? ResolveResetTargetQuota(quota, config) : null;

        // Base advice carrying the transparency fields common to every branch.
        // Individual branches override ShouldSpend / Reason / Rationale / window.
        ResetSpendAdvice Advice(bool shouldSpend, ResetAdviceReason reason, string rationale,
            DateTimeOffset? predictedNaturalReset = null,
            DateTimeOffset? decisionDeadline = null,
            ResetSpendWindow? window = null) => new()
            {
                Agent = agent,
                EvaluatedAt = now,
                ShouldSpend = shouldSpend,
                Reason = reason,
                Rationale = rationale,
                UsableQuotaPct = usableQuotaPct,
                DustThresholdPct = config.DustThresholdPct,
                PlanEndsAt = config.PlanEndsAt,
                NextCreditExpiresAt = nextCreditExpiresAt,
                NextCreditIsEstimated = nextCreditIsEstimated,
                PredictedNaturalReset = predictedNaturalReset,
                DecisionDeadline = decisionDeadline,
                OptimalWindow = window,
            };

        // 1. Agent scoping. The advisor only covers configured agents (codex
        //    today; claude later). An out-of-scope agent is never advised.
        if (!config.CoversAgent(agent))
            return Advice(false, ResetAdviceReason.NotApplicableAgent,
                $"'{agent}' is not in the configured reset-advisor agent set — no advice.");

        // 2. Cadence must be usable to predict the natural reset. A missing
        //    anchor or non-positive period is a configuration error; without a
        //    natural-reset prediction the re-anchor branch cannot be reasoned
        //    about, so conservatively advise against spending.
        if (config.CadenceAnchor is not { } anchor || config.CadencePeriod <= TimeSpan.Zero)
            return Advice(false, ResetAdviceReason.ConfigurationInvalid,
                "Cadence anchor/period is not configured — cannot predict the natural reset; no advice.");

        // 3. Burn-first needs a real usable-quota reading. An unknown snapshot
        //    cannot tell us whether quota is still burnable, so we hold.
        if (usableQuotaPct is not { } usable)
            return Advice(false, ResetAdviceReason.QuotaReadingUnavailable,
                "Quota reading is unavailable — cannot evaluate burn-first; no advice.");

        // 4. Nothing to spend. NextCreditExpiresAt is null exactly when no
        //    banked credit is tracked.
        if (nextCreditExpiresAt is not { } creditSpendBy)
            return Advice(false, ResetAdviceReason.NoBankedCredit,
                "No banked reset credit is available to spend.");

        // 5. BURN-FIRST. While usable quota is above dust, resetting now would
        //    forfeit the remaining window — spend the quota first.
        if (usable > config.DustThresholdPct)
            return Advice(false, ResetAdviceReason.BurnFirst,
                $"Usable quota is {usable:0.##}% (> {config.DustThresholdPct:0.##}% dust) — burn it before resetting; a reset now forfeits the current window.");

        // Quota is at/below dust: the current window is spent. Decide between a
        // banked reset now vs. waiting for the free natural reset.
        var decisionDeadline = config.PlanEndsAt is { } planEnd && planEnd < creditSpendBy
            ? planEnd
            : creditSpendBy;

        // 6. Deadline already passed: the plan is over or the credit's advised
        //    spend-by is behind us — spending has no remaining value.
        if (decisionDeadline <= now)
            return Advice(false, ResetAdviceReason.DeadlinePassed,
                $"Decision deadline {decisionDeadline:u} is in the past — the credit's spend-by or the plan end has passed; no advice.",
                decisionDeadline: decisionDeadline);

        var predictedNaturalReset = NaturalResetCadence.PredictNextReset(now, anchor, config.CadencePeriod);

        // 7. RE-ANCHOR MODEL. If the free natural reset lands at/before the
        //    deadline (within tolerance), wait for it: spending now would
        //    destroy that free reset AND consume a credit for nothing.
        if (predictedNaturalReset <= decisionDeadline + config.TimeTolerance)
            return Advice(false, ResetAdviceReason.NaturalResetArrivesInTime,
                $"Quota is exhausted but the natural reset at {predictedNaturalReset:u} lands by the deadline {decisionDeadline:u} — wait for the free reset; spending now would destroy it and burn a credit.",
                predictedNaturalReset: predictedNaturalReset,
                decisionDeadline: decisionDeadline);

        // 8. DEADLINE branch. The natural reset lands too late (after the
        //    deadline, beyond tolerance): waiting is useless, so spend a banked
        //    credit while it still has value. The optimal window opens now (the
        //    window is already spent) and closes at the decision deadline.
        return Advice(true, ResetAdviceReason.SpendBeforeDeadline,
            $"Quota is exhausted and the natural reset at {predictedNaturalReset:u} lands after the deadline {decisionDeadline:u} — spend a banked credit before then, else the plan ends or the credit expires unused.",
            predictedNaturalReset: predictedNaturalReset,
            decisionDeadline: decisionDeadline,
            window: new ResetSpendWindow(now, decisionDeadline));
    }

    /// <summary>
    /// Reads the usable-quota percentage of the window a banked reset actually
    /// re-anchors (the weekly window for Codex), NOT the cross-window minimum
    /// <see cref="AgentQuotaSnapshot.AvailablePct"/> exposes.
    ///
    /// <para>Burn-first / re-anchor reasoning is about that one window: applying a
    /// reset re-anchors it and forfeits whatever it still holds. During a heavy
    /// burst a short 5h window routinely sits near 0% while the weekly window is
    /// still substantial; keying on the min would read ~0%, falsely satisfy
    /// burn-first, and advise spending a credit that wastes the live weekly
    /// quota — exactly the loss the algorithm exists to prevent.</para>
    ///
    /// <para>When the snapshot carries no per-window breakdown, or the configured
    /// target window is absent from it, this falls back to the aggregated
    /// <see cref="AgentQuotaSnapshot.AvailablePct"/> — best-effort, since a probe
    /// without window detail can only offer the overall number.</para>
    /// </summary>
    private static double ResolveResetTargetQuota(AgentQuotaSnapshot quota, ResetOptimalityConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ResetTargetWindow) && quota.Windows.Count > 0)
        {
            foreach (var window in quota.Windows)
            {
                if (string.Equals(window.Name, config.ResetTargetWindow, StringComparison.OrdinalIgnoreCase)
                    && window.AvailablePct >= 0)
                    return window.AvailablePct;
            }
        }

        return quota.AvailablePct;
    }
}

/// <summary>
/// Predicts and calibrates the fixed-cadence natural quota reset. Codex resets
/// on a weekly wall-clock boundary (~Monday 06:00 UTC); the provider's
/// <c>reset_at</c> field over-predicts and must not be used, so the boundary is
/// derived from a configured anchor + period and optionally phase-refined from
/// observed reset instants in the quota logger.
/// </summary>
public static class NaturalResetCadence
{
    /// <summary>
    /// Returns the next natural reset strictly after <paramref name="now"/> on the
    /// schedule <c>anchor + k·period</c>. The anchor may be any instant on the
    /// schedule (past or future); the result is the first schedule point after
    /// <paramref name="now"/>.
    /// </summary>
    public static DateTimeOffset PredictNextReset(DateTimeOffset now, DateTimeOffset anchor, TimeSpan period)
    {
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), period, "Cadence period must be positive.");

        // Jump to the schedule point at or just before `now`, then step forward
        // to the first point strictly after it. The floor avoids O(elapsed/period)
        // iteration when the anchor is far in the past.
        var periodsElapsed = Math.Floor((now - anchor) / period);
        var candidate = anchor + period * periodsElapsed;
        while (candidate <= now)
            candidate += period;
        return candidate;
    }

    /// <summary>
    /// Phase-refines a configured cadence anchor from observed natural-reset
    /// instants (self-calibration from the quota logger). Each observation's
    /// signed distance to its nearest scheduled boundary is computed in
    /// <c>[-period/2, +period/2]</c>; the median of those residuals is the phase
    /// drift between the configured schedule and reality. The anchor is shifted
    /// by that drift, but only when it exceeds <paramref name="tolerance"/> — a
    /// smaller drift is treated as noise and the configured anchor is kept so
    /// the schedule does not churn on every sample.
    /// </summary>
    /// <param name="configuredAnchor">Operator-seeded anchor.</param>
    /// <param name="period">Cadence period (weekly for Codex).</param>
    /// <param name="observedResets">Instants at which a natural reset was
    /// observed in the logger. Empty = no refinement.</param>
    /// <param name="tolerance">Drift below which the configured anchor is kept.</param>
    public static DateTimeOffset RefineAnchor(
        DateTimeOffset configuredAnchor,
        TimeSpan period,
        IReadOnlyList<DateTimeOffset> observedResets,
        TimeSpan tolerance)
    {
        ArgumentNullException.ThrowIfNull(observedResets);
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), period, "Cadence period must be positive.");
        if (observedResets.Count == 0)
            return configuredAnchor;

        var residuals = new List<TimeSpan>(observedResets.Count);
        foreach (var reset in observedResets)
        {
            var offset = (reset - configuredAnchor) / period;
            var residual = (reset - configuredAnchor) - period * Math.Round(offset);
            residuals.Add(residual);
        }

        residuals.Sort();
        var medianResidual = Median(residuals);

        return medianResidual.Duration() <= tolerance
            ? configuredAnchor
            : configuredAnchor + medianResidual;
    }

    private static TimeSpan Median(IReadOnlyList<TimeSpan> sorted)
    {
        var mid = sorted.Count / 2;
        if (sorted.Count % 2 == 1)
            return sorted[mid];
        // Even count: average the two central residuals (in ticks to stay exact).
        return TimeSpan.FromTicks((sorted[mid - 1].Ticks + sorted[mid].Ticks) / 2);
    }
}

/// <summary>
/// Operator config for <see cref="ResetOptimalityEvaluator"/>. Hosts bind these
/// from operator config and hot-reload them; every field is read per evaluation.
/// </summary>
public sealed record ResetOptimalityConfig
{
    /// <summary>
    /// When the subscription plan ends. Past this instant additional quota is
    /// worthless, so it caps the decision deadline. Null = no plan-end pressure.
    /// </summary>
    public DateTimeOffset? PlanEndsAt { get; init; }

    /// <summary>
    /// A known instant on the natural-reset schedule (e.g. a recent Monday
    /// 06:00 UTC boundary). Combined with <see cref="CadencePeriod"/> to predict
    /// the next natural reset. Null disables spend advice (cannot reason about
    /// the re-anchor trade-off without it).
    /// </summary>
    public DateTimeOffset? CadenceAnchor { get; init; }

    /// <summary>Natural-reset period. Codex resets weekly; default 7 days.</summary>
    public TimeSpan CadencePeriod { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Name of the cap window a banked reset re-anchors — the window burn-first
    /// must reason over. For Codex this is the <c>weekly</c> window; a reset
    /// re-anchors it, so the decision hinges on its remaining quota, not the
    /// cross-window minimum <see cref="AgentQuotaSnapshot.AvailablePct"/> reports.
    /// When null/empty, or when the snapshot has no matching window, the evaluator
    /// falls back to the aggregated <see cref="AgentQuotaSnapshot.AvailablePct"/>.
    /// Matched case-insensitively against <see cref="WindowQuota.Name"/>.
    /// </summary>
    public string? ResetTargetWindow { get; init; } = "weekly";

    /// <summary>
    /// Usable-quota percentage at or below which the current window counts as
    /// spent (burn-first is satisfied). Default 1% — a sliver of quota is not
    /// worth delaying a needed reset for.
    /// </summary>
    public double DustThresholdPct { get; init; } = 1.0;

    /// <summary>
    /// Slack around the deadline vs. natural-reset comparison. The natural reset
    /// must land later than the deadline by MORE than this before a spend is
    /// advised, so a near-tie never burns a credit. Default 6 hours.
    /// </summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Agents the advisor covers (case-insensitive). Codex today; claude later.
    /// An empty set advises for no one.
    /// </summary>
    public IReadOnlyCollection<string> Agents { get; init; } = new[] { "codex" };

    /// <summary>True when <paramref name="agent"/> is in <see cref="Agents"/>.</summary>
    public bool CoversAgent(string agent)
        => Agents.Any(a => string.Equals(a, agent, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Why the evaluator reached its spend/hold verdict.
/// Serialized as a string so the wire contract is human-readable and stable
/// independent of the API's JSON-converter list — operator scripts and UIs can
/// switch on the reason name directly.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum ResetAdviceReason
{
    /// <summary>The agent is outside the configured advisor scope.</summary>
    NotApplicableAgent,

    /// <summary>No cadence anchor/period configured — the natural reset cannot be predicted.</summary>
    ConfigurationInvalid,

    /// <summary>The quota snapshot is unknown — burn-first cannot be evaluated.</summary>
    QuotaReadingUnavailable,

    /// <summary>No banked reset credit is available to spend.</summary>
    NoBankedCredit,

    /// <summary>Usable quota is still above dust — burn it before resetting.</summary>
    BurnFirst,

    /// <summary>The decision deadline is already in the past.</summary>
    DeadlinePassed,

    /// <summary>The free natural reset arrives by the deadline — wait for it (a reset now would destroy it).</summary>
    NaturalResetArrivesInTime,

    /// <summary>The natural reset lands after the deadline — spend a banked credit before then.</summary>
    SpendBeforeDeadline,
}

/// <summary>The window during which a banked reset should be spent, when advised.</summary>
/// <param name="OpensAt">Earliest sensible spend instant (quota already spent).</param>
/// <param name="ClosesAt">Latest spend instant that still captures value (the decision deadline).</param>
public readonly record struct ResetSpendWindow(DateTimeOffset OpensAt, DateTimeOffset ClosesAt);

/// <summary>
/// Structured, report-only advice emitted per evaluation. Carries the verdict
/// (<see cref="ShouldSpend"/>) plus every input that drove it, so an operator
/// (or a UI) can see the reasoning without re-deriving it.
/// </summary>
public sealed record ResetSpendAdvice
{
    /// <summary>Agent the advice is for.</summary>
    public required string Agent { get; init; }

    /// <summary>Evaluation instant.</summary>
    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>Whether to spend a banked reset credit now. The single actionable bit.</summary>
    public required bool ShouldSpend { get; init; }

    /// <summary>Machine-readable reason code for the verdict.</summary>
    public required ResetAdviceReason Reason { get; init; }

    /// <summary>Human-readable explanation of the verdict.</summary>
    public required string Rationale { get; init; }

    /// <summary>Predicted next natural reset used in the re-anchor comparison. Null when not reached.</summary>
    public DateTimeOffset? PredictedNaturalReset { get; init; }

    /// <summary>
    /// <c>min(planEndsAt, nextCreditExpiresAt)</c> — the latest moment spending
    /// still has value. Null when the decision did not reach the deadline stage.
    /// </summary>
    public DateTimeOffset? DecisionDeadline { get; init; }

    /// <summary>Configured plan end, echoed for transparency. Null when unset.</summary>
    public DateTimeOffset? PlanEndsAt { get; init; }

    /// <summary>Advised spend-by of the soonest banked credit (from 2/5). Null when none.</summary>
    public DateTimeOffset? NextCreditExpiresAt { get; init; }

    /// <summary>
    /// True when <see cref="NextCreditExpiresAt"/> is driven by a seeded operator
    /// estimate rather than an observed grant — a consumer must not render the
    /// deadline as precise when this is set.
    /// </summary>
    public bool NextCreditIsEstimated { get; init; }

    /// <summary>Usable quota percentage read from the snapshot. Null when unknown.</summary>
    public double? UsableQuotaPct { get; init; }

    /// <summary>Dust threshold used for the burn-first test, echoed for transparency.</summary>
    public double DustThresholdPct { get; init; }

    /// <summary>The window to spend in, present only when <see cref="ShouldSpend"/> is true.</summary>
    public ResetSpendWindow? OptimalWindow { get; init; }
}

/// <summary>
/// Composes the current quota snapshot and derived credit expiry into a
/// <see cref="ResetSpendAdvice"/>. Lives in <c>CodeyBox.Core</c> so the API
/// layer can resolve it from DI and degrade to 503 when no implementation is
/// registered (the statistics plugin is not loaded). Implementations MUST be
/// thread-safe.
/// </summary>
public interface IResetOptimalityAdvisor
{
    /// <summary>Produces reset-spend advice for the requested agent.</summary>
    Task<ResetSpendAdvice> AdviseAsync(ResetAdviceRequest request, CancellationToken ct = default);
}

/// <summary>Query accepted by <see cref="IResetOptimalityAdvisor.AdviseAsync"/>.</summary>
public sealed record ResetAdviceRequest
{
    /// <summary>Agent to advise on. Null falls back to the advisor's configured default agent.</summary>
    public string? Agent { get; init; }

    /// <summary>Lower bound on the credit-count series used to derive expiries. Null = default look-back.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Upper bound on the credit-count series. Null = now.</summary>
    public DateTimeOffset? ToUtc { get; init; }
}

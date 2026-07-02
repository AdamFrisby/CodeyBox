namespace CodeyBox.Core;

/// <summary>
/// Derives the expiry of each banked quota <em>reset credit</em> from the
/// observed <c>rate_limit_reset_credits.available_count</c> time-series
/// (surfaced on <see cref="AgentQuotaSnapshot.ResetCreditsAvailable"/> by the
/// quota probe and sampled by the statistics plugin). No manual per-credit
/// expiry input is required — the grant instant of each credit is inferred
/// from when the count stepped up, and the provider's fixed expiry period
/// (Codex publishes a 30-day credit lifetime) is added to it.
///
/// <para><b>Grant-time inference (the robustness property).</b> When the
/// count increments from a lower value to a higher one across two samples,
/// the grant happened somewhere in the gap between them. The tracker pins the
/// grant instant to the <em>last sample at the previous (lower) count</em> —
/// the earliest-possible grant instant — rather than the first higher reading.
/// Taking the earlier bound yields the earliest-possible expiry (the safe
/// direction: it warns to spend a credit sooner, never later) and is immune to
/// the orchestrator being down across the grant: a measurement gap can only
/// push the inferred grant earlier, never later, so it cannot make the tracker
/// under-estimate a credit's age.</para>
///
/// <para><b>FIFO retirement.</b> On a decrement the tracker retires the oldest
/// tracked grant first — the credit closest to expiry — mirroring how a
/// provider spends the soonest-expiring credit.</para>
///
/// <para><b>Pre-observation credits.</b> Credits already banked before
/// tracking began have no observed increment, so their age cannot be inferred.
/// Operators seed these via <see cref="ResetCreditExpiryConfig.SeededCredits"/>
/// with an <em>estimated</em> expiry; the tracker flags them
/// <see cref="BankedResetCredit.IsEstimated"/> so they are never presented as
/// precise. Seeds are treated as the oldest credits (retired before any
/// observed grant on a decrement).</para>
/// </summary>
public static class ResetCreditExpiryTracker
{
    /// <summary>
    /// Replays an ordered reset-credit-count time-series and returns the
    /// derived set of banked credits with their inferred expiries.
    /// </summary>
    /// <param name="observations">
    /// Samples of the banked reset-credit count. Only samples whose count is
    /// known belong here — a missing reading (probe failure / older provider)
    /// is a <em>gap</em>, not a decrement to zero, and must be omitted so the
    /// downtime-robustness property holds. Order is normalised internally by
    /// <see cref="ResetCreditObservation.SampledAt"/>.
    /// </param>
    /// <param name="config">Expiry period, safety buffer, and seeded pre-observation credits.</param>
    public static ResetCreditExpiryReport Track(
        IReadOnlyList<ResetCreditObservation> observations,
        ResetCreditExpiryConfig config)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(config);

        // The FIFO queue, oldest (closest-to-expiry) at the front. Seeds are
        // pre-observation and therefore the oldest — enqueue them first,
        // ordered by their estimated expiry so the soonest-expiring seed is
        // retired first on a decrement.
        var queue = new Queue<Grant>();
        foreach (var seed in config.SeededCredits.OrderBy(s => s.EstimatedExpiresAt))
        {
            queue.Enqueue(new Grant(
                GrantedAt: seed.EstimatedExpiresAt - config.ExpiryPeriod,
                ExpiresAt: seed.EstimatedExpiresAt,
                IsEstimated: true,
                Label: seed.Label));
        }

        var ordered = observations.OrderBy(o => o.SampledAt).ToList();

        int? previousCount = null;
        DateTimeOffset previousSampledAt = default;

        foreach (var obs in ordered)
        {
            if (previousCount is null)
            {
                // First reading establishes the baseline only. Any credits it
                // reports are pre-observation (no observed grant), so they are
                // not inferable here — the operator seeds those.
                previousCount = obs.AvailableCount;
                previousSampledAt = obs.SampledAt;
                continue;
            }

            var delta = obs.AvailableCount - previousCount.Value;
            if (delta > 0)
            {
                // Grant(s): pin each to the last sample at the previous lower
                // count (the earliest-possible grant instant). A multi-step
                // jump across a measurement gap pins every new credit to that
                // same earliest bound.
                for (var i = 0; i < delta; i++)
                {
                    queue.Enqueue(new Grant(
                        GrantedAt: previousSampledAt,
                        ExpiresAt: previousSampledAt + config.ExpiryPeriod,
                        IsEstimated: false,
                        Label: null));
                }
            }
            else if (delta < 0)
            {
                // Spend(s): retire the oldest grant(s) first. Clamp to the
                // queue size — a decrement below what we are tracking (e.g.
                // baseline credits the operator did not seed) is a safe no-op.
                var toRetire = -delta;
                for (var i = 0; i < toRetire && queue.Count > 0; i++)
                    queue.Dequeue();
            }

            previousCount = obs.AvailableCount;
            previousSampledAt = obs.SampledAt;
        }

        var credits = queue
            .Select(g => new BankedResetCredit
            {
                GrantedAt = g.GrantedAt,
                ExpiresAt = g.ExpiresAt,
                AdvisedSpendByAt = g.ExpiresAt - config.SafetyBuffer,
                IsEstimated = g.IsEstimated,
                Label = g.Label,
            })
            .ToList();

        // nextCreditExpiresAt = min over queued grants of
        // (grant_time + expiryPeriod - safetyBuffer). Because the safety buffer
        // is a constant, the minimum spend-by moment belongs to the credit with
        // the earliest raw expiry (the oldest grant when all share one period).
        DateTimeOffset? next = credits.Count == 0
            ? null
            : credits.Min(c => c.AdvisedSpendByAt);

        return new ResetCreditExpiryReport
        {
            Credits = credits,
            NextCreditExpiresAt = next,
            LatestObservedCount = previousCount,
            ExpiryPeriod = config.ExpiryPeriod,
            SafetyBuffer = config.SafetyBuffer,
        };
    }

    /// <summary>Internal working entry for the FIFO queue.</summary>
    private readonly record struct Grant(
        DateTimeOffset GrantedAt,
        DateTimeOffset ExpiresAt,
        bool IsEstimated,
        string? Label);
}

/// <summary>
/// Configuration for <see cref="ResetCreditExpiryTracker.Track"/>. Both spans
/// have provider-published / operator-safe defaults so an empty config is
/// usable; hosts hot-reload these from operator config.
/// </summary>
public sealed record ResetCreditExpiryConfig
{
    /// <summary>
    /// How long a granted reset credit remains spendable before the provider
    /// expires it. Codex publishes a fixed 30-day credit lifetime, the default.
    /// </summary>
    public TimeSpan ExpiryPeriod { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Margin subtracted from a credit's raw expiry to produce its advised
    /// spend-by moment, so the advisor prompts before the true deadline rather
    /// than at it. Default 24 hours.
    /// </summary>
    public TimeSpan SafetyBuffer { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Operator-seeded pre-observation credits — credits already banked before
    /// the count time-series began, whose age cannot be inferred. Each carries
    /// an <em>estimated</em> expiry and is flagged as such in the output.
    /// </summary>
    public IReadOnlyList<SeededResetCredit> SeededCredits { get; init; } = Array.Empty<SeededResetCredit>();
}

/// <summary>
/// An operator-supplied estimate for a reset credit that was already banked
/// before tracking began. Surfaced as an estimate, never as a precise value.
/// </summary>
public sealed record SeededResetCredit
{
    /// <summary>Operator's best estimate of when this pre-observation credit expires.</summary>
    public required DateTimeOffset EstimatedExpiresAt { get; init; }

    /// <summary>Optional operator label (e.g. "credit A — burn within 2 weeks").</summary>
    public string? Label { get; init; }
}

/// <summary>One sample of the banked reset-credit count at a point in time.</summary>
/// <param name="SampledAt">When the count was observed.</param>
/// <param name="AvailableCount">The observed <c>available_count</c> (must be known — omit gaps entirely).</param>
public readonly record struct ResetCreditObservation(DateTimeOffset SampledAt, int AvailableCount);

/// <summary>One banked reset credit with its inferred (or seeded-estimated) expiry.</summary>
public sealed record BankedResetCredit
{
    /// <summary>
    /// Inferred grant instant. For an observed credit this is the
    /// earliest-possible grant (the last sample at the previous lower count).
    /// For a seeded credit it is back-derived from the operator's estimated
    /// expiry and is only as good as that estimate.
    /// </summary>
    public required DateTimeOffset GrantedAt { get; init; }

    /// <summary>
    /// Raw provider expiry: <see cref="GrantedAt"/> + expiry period for an
    /// observed credit, or the operator's estimate for a seeded credit.
    /// </summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Advised spend-by moment: <see cref="ExpiresAt"/> minus the safety buffer.</summary>
    public required DateTimeOffset AdvisedSpendByAt { get; init; }

    /// <summary>
    /// True when this credit's timing is an operator estimate (a seeded
    /// pre-observation credit) rather than inferred from an observed grant.
    /// </summary>
    public required bool IsEstimated { get; init; }

    /// <summary>Optional operator label carried through from a seed. Null for observed credits.</summary>
    public string? Label { get; init; }
}

/// <summary>The derived state of an account's banked reset credits.</summary>
public sealed record ResetCreditExpiryReport
{
    /// <summary>Banked credits in FIFO order — oldest (closest to expiry) first. Empty when none are tracked.</summary>
    public IReadOnlyList<BankedResetCredit> Credits { get; init; } = Array.Empty<BankedResetCredit>();

    /// <summary>
    /// Earliest advised spend-by moment across all tracked credits
    /// (min of <see cref="BankedResetCredit.AdvisedSpendByAt"/>). Null when no
    /// credits are tracked. This is the value a reset advisor watches.
    /// </summary>
    public DateTimeOffset? NextCreditExpiresAt { get; init; }

    /// <summary>
    /// The most recent observed <c>available_count</c>, or null when the
    /// series was empty. May differ from <see cref="Credits"/> count when the
    /// operator's seeds do not exactly cover the pre-observation baseline — a
    /// signal that the seed list needs adjusting.
    /// </summary>
    public int? LatestObservedCount { get; init; }

    /// <summary>Expiry period used for the derivation (echoed for transparency).</summary>
    public TimeSpan ExpiryPeriod { get; init; }

    /// <summary>Safety buffer used for the derivation (echoed for transparency).</summary>
    public TimeSpan SafetyBuffer { get; init; }
}

/// <summary>
/// Reads the sampled reset-credit-count time-series and returns the derived
/// <see cref="ResetCreditExpiryReport"/>. Lives in <c>CodeyBox.Core</c> so the
/// API layer can resolve it from DI and gracefully degrade to 503 when no
/// implementation is registered (the statistics plugin is not loaded).
/// Implementations MUST be thread-safe — callers may query concurrently with
/// sampler writes against the underlying time-series.
/// </summary>
public interface IResetCreditExpiryEstimator
{
    /// <summary>Derives banked-credit expiries for the requested agent and time range.</summary>
    Task<ResetCreditExpiryReport> EstimateAsync(ResetCreditExpiryQuery query, CancellationToken ct = default);
}

/// <summary>Query accepted by <see cref="IResetCreditExpiryEstimator.EstimateAsync"/>.</summary>
public sealed record ResetCreditExpiryQuery
{
    /// <summary>Agent kind whose reset credits to derive. Null falls back to the estimator's configured default agent (Codex).</summary>
    public string? Agent { get; init; }

    /// <summary>Lower bound on the count series (inclusive). Null = the estimator's default look-back horizon.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Upper bound on the count series (exclusive). Null = now.</summary>
    public DateTimeOffset? ToUtc { get; init; }
}

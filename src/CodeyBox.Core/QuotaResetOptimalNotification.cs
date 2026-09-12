namespace CodeyBox.Core;

/// <summary>
/// Stable event names for quota-reset advisory notifications. The single
/// source of truth for the <c>quota.reset_optimal</c> webhook event name —
/// the notifier plugin, <see cref="CodeyBox.Webhooks.EventSchema"/>, and
/// docs all reference this constant rather than re-typing the string.
/// </summary>
public static class QuotaResetOptimalEvents
{
    /// <summary>
    /// Fired when the reset-optimality advisor flips to
    /// <c>shouldSpend=true</c> for a watched agent: it is a good time to
    /// spend a banked quota-reset credit. Report-only — no reset is triggered.
    /// </summary>
    public const string ResetOptimal = "quota.reset_optimal";
}

/// <summary>
/// Details payload for the <c>quota.reset_optimal</c> webhook event, carried
/// in <see cref="WebhookEvent.Details"/>. Tells the operator which agent hit
/// its optimal spend window, why, how many banked credits were observed, and
/// how long the window stays open.
/// </summary>
public sealed record QuotaResetOptimalDetails
{
    /// <summary>Agent the advice was evaluated for (e.g. <c>codex</c>).</summary>
    public required string Agent { get; init; }

    /// <summary>
    /// Machine-readable reason for the verdict — the
    /// <see cref="ResetAdviceReason"/> name (always
    /// <c>SpendBeforeDeadline</c> on this event; rendered as a string so
    /// receivers can switch on it without knowing the enum ordinal).
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Banked reset credits observed at evaluation time. Null when the count
    /// could not be resolved (the credit estimator was unavailable).
    /// </summary>
    public int? BankedCredits { get; init; }

    /// <summary>
    /// Latest instant spending still captures value — the advice's optimal
    /// window close (the decision deadline). The operator should spend before
    /// this moment.
    /// </summary>
    public required DateTimeOffset OptimalUntil { get; init; }

    /// <summary>Earliest sensible spend instant (the advice's optimal window open).</summary>
    public DateTimeOffset? OptimalFrom { get; init; }

    /// <summary>Predicted next natural reset used in the re-anchor comparison, if reached.</summary>
    public DateTimeOffset? PredictedNaturalReset { get; init; }

    /// <summary>Decision deadline echoed for transparency, if reached.</summary>
    public DateTimeOffset? DecisionDeadline { get; init; }

    /// <summary>Advised spend-by of the soonest banked credit, if any.</summary>
    public DateTimeOffset? NextCreditExpiresAt { get; init; }

    /// <summary>
    /// True when <see cref="NextCreditExpiresAt"/> is an operator estimate
    /// rather than an observed grant — receivers must not render the deadline
    /// as precise when this is set.
    /// </summary>
    public bool NextCreditIsEstimated { get; init; }

    /// <summary>Usable quota percentage read from the snapshot, if known.</summary>
    public double? UsableQuotaPct { get; init; }

    /// <summary>
    /// Builds the webhook payload from a spend-positive
    /// <see cref="ResetSpendAdvice"/>. Pure — the same advice and credit
    /// count always yield the same payload.
    /// </summary>
    /// <param name="advice">Advice that flipped to <c>ShouldSpend</c>.</param>
    /// <param name="bankedCredits">Observed banked-credit count, if resolvable.</param>
    public static QuotaResetOptimalDetails FromAdvice(ResetSpendAdvice advice, int? bankedCredits)
    {
        ArgumentNullException.ThrowIfNull(advice);
        if (!advice.ShouldSpend)
            throw new ArgumentOutOfRangeException(nameof(advice), "Cannot build a reset-optimal payload from hold advice.");
        if (advice.OptimalWindow is not { } window)
            throw new ArgumentOutOfRangeException(nameof(advice), "Cannot build a reset-optimal payload without an optimal window.");

        return new QuotaResetOptimalDetails
        {
            Agent = advice.Agent,
            Reason = advice.Reason.ToString(),
            BankedCredits = bankedCredits,
            OptimalUntil = window.ClosesAt,
            OptimalFrom = window.OpensAt,
            PredictedNaturalReset = advice.PredictedNaturalReset,
            DecisionDeadline = advice.DecisionDeadline,
            NextCreditExpiresAt = advice.NextCreditExpiresAt,
            NextCreditIsEstimated = advice.NextCreditIsEstimated,
            UsableQuotaPct = advice.UsableQuotaPct,
        };
    }
}

/// <summary>
/// Per-agent de-duplication state for <c>quota.reset_optimal</c> delivery.
/// Records what was last pinged and when, so the notifier sends once per
/// optimal window instead of once per evaluation tick.
/// </summary>
public sealed record ResetOptimalNotifiedState
{
    /// <summary>Agent that was notified.</summary>
    public required string Agent { get; init; }

    /// <summary>
    /// The optimal window close (<see cref="QuotaResetOptimalDetails.OptimalUntil"/>)
    /// that was notified. A spend verdict with the same close is the same
    /// window — already pinged, never re-pinged.
    /// </summary>
    public required DateTimeOffset OptimalUntil { get; init; }

    /// <summary>Instant the notification was sent. Compared against the configured cooldown.</summary>
    public required DateTimeOffset NotifiedAt { get; init; }
}

/// <summary>
/// Pure de-duplication policy for <c>quota.reset_optimal</c> delivery.
/// The notifier pings once per optimal window: a spend verdict for an
/// already-pinged window is suppressed, and verdicts (even for a new window)
/// inside the configured cooldown after the last ping are suppressed so a
/// flapping deadline cannot page the operator every tick.
/// </summary>
public static class ResetOptimalNotifyPolicy
{
    /// <summary>
    /// Returns true when <paramref name="advice"/> should produce a
    /// notification given the last delivery and the current time. Pure and
    /// deterministic.
    /// </summary>
    /// <param name="advice">Latest advice for the agent.</param>
    /// <param name="last">Last delivery for the agent, if any.</param>
    /// <param name="now">Evaluation instant.</param>
    /// <param name="cooldown">Minimum interval between pings. Non-positive disables the time check (same-window suppression still applies).</param>
    public static bool ShouldNotify(
        ResetSpendAdvice advice,
        ResetOptimalNotifiedState? last,
        DateTimeOffset now,
        TimeSpan cooldown)
    {
        ArgumentNullException.ThrowIfNull(advice);
        if (!advice.ShouldSpend)
            return false;
        if (advice.OptimalWindow is not { } window)
            return false;
        if (last is null)
            return true;
        if (!string.Equals(last.Agent, advice.Agent, StringComparison.OrdinalIgnoreCase))
            return true;
        if (last.OptimalUntil == window.ClosesAt)
            return false;
        if (cooldown > TimeSpan.Zero && now - last.NotifiedAt < cooldown)
            return false;
        return true;
    }
}

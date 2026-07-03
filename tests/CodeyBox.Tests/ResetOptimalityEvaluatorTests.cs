using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Drives <see cref="ResetOptimalityEvaluator.Evaluate"/> — the pure
/// reset-optimality decision. Covers the acceptance contract: BURN-FIRST
/// (never spend while usable quota &gt; dust), the RE-ANCHOR "natural reset
/// would be destroyed" branch (wait for the free reset), and the DEADLINE
/// branch (spend before the plan ends / the credit expires), plus the guard
/// branches (agent scope, unknown quota, no credit, bad cadence, past deadline).
/// </summary>
public sealed class ResetOptimalityEvaluatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-03T12:00:00Z");

    // A cadence anchor two days before "now" on a weekly schedule → the next
    // natural reset is 5 days out (Now + 5d), a convenient reference point.
    private static readonly DateTimeOffset Anchor = Now - TimeSpan.FromDays(2);

    private static ResetOptimalityConfig Config(
        DateTimeOffset? planEndsAt = null,
        DateTimeOffset? anchor = null,
        double dust = 1.0,
        TimeSpan? tolerance = null,
        IReadOnlyList<string>? agents = null)
        => new()
        {
            PlanEndsAt = planEndsAt,
            CadenceAnchor = anchor ?? Anchor,
            CadencePeriod = TimeSpan.FromDays(7),
            DustThresholdPct = dust,
            TimeTolerance = tolerance ?? TimeSpan.FromHours(6),
            Agents = agents ?? new[] { "codex" },
        };

    private static AgentQuotaSnapshot Quota(double availablePct)
        => new() { AvailablePct = availablePct };

    private static AgentQuotaSnapshot UnknownQuota()
        => AgentQuotaSnapshot.UnknownSnapshot(QuotaUnknownReason.Transient);

    /// <summary>A credit report whose soonest credit's advised spend-by is <paramref name="spendBy"/>.</summary>
    private static ResetCreditExpiryReport Credit(DateTimeOffset spendBy, bool estimated = false)
        => new()
        {
            Credits = new[]
            {
                new BankedResetCredit
                {
                    GrantedAt = spendBy - TimeSpan.FromDays(30),
                    ExpiresAt = spendBy + TimeSpan.FromHours(24),
                    AdvisedSpendByAt = spendBy,
                    IsEstimated = estimated,
                },
            },
            NextCreditExpiresAt = spendBy,
            NextCreditIsEstimated = estimated,
        };

    private static ResetCreditExpiryReport NoCredit()
        => new() { Credits = Array.Empty<BankedResetCredit>(), NextCreditExpiresAt = null };

    // ---- BURN-FIRST -------------------------------------------------------

    [Fact]
    public void BurnFirst_UsableQuotaAboveDust_DoesNotAdviseSpend()
    {
        // 40% quota still usable. Even with a credit expiring soon and the
        // natural reset far off, resetting now would forfeit that 40% — hold.
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex",
            Quota(40.0),
            Credit(Now + TimeSpan.FromDays(1)),
            Config(),
            Now);

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.BurnFirst, advice.Reason);
        Assert.Null(advice.OptimalWindow);
        Assert.Equal(40.0, advice.UsableQuotaPct);
    }

    [Fact]
    public void BurnFirst_ExactlyAtDust_IsNotAboveDust_ProceedsPastBurnFirst()
    {
        // Usable == dust is NOT "above dust" — the window counts as spent, so
        // burn-first is satisfied and the decision proceeds. With the natural
        // reset arriving in time, the verdict is hold-for-natural-reset.
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex",
            Quota(1.0),
            Credit(Now + TimeSpan.FromDays(20)),
            Config(dust: 1.0),
            Now);

        Assert.NotEqual(ResetAdviceReason.BurnFirst, advice.Reason);
        Assert.Equal(ResetAdviceReason.NaturalResetArrivesInTime, advice.Reason);
    }

    // ---- RE-ANCHOR: natural reset would be destroyed ----------------------

    [Fact]
    public void ReAnchor_NaturalResetArrivesBeforeDeadline_DoesNotSpend()
    {
        // Quota exhausted. Credit is comfortable (spend-by 20d out), no plan
        // end. The natural reset lands in 5 days — before the deadline — so
        // spending now would DESTROY that free reset and burn a credit. Hold.
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex",
            Quota(0.0),
            Credit(Now + TimeSpan.FromDays(20)),
            Config(),
            Now);

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.NaturalResetArrivesInTime, advice.Reason);
        Assert.Equal(Now + TimeSpan.FromDays(5), advice.PredictedNaturalReset);
        Assert.Equal(Now + TimeSpan.FromDays(20), advice.DecisionDeadline);
        Assert.Null(advice.OptimalWindow);
    }

    [Fact]
    public void ReAnchor_NaturalResetWithinToleranceOfDeadline_DoesNotSpend()
    {
        // Deadline is 5 days minus 3 hours; the natural reset is at 5 days —
        // i.e. 3 hours AFTER the deadline, inside the 6-hour tolerance. A
        // near-tie must not burn a credit: treat the free reset as in-time.
        var deadline = Now + TimeSpan.FromDays(5) - TimeSpan.FromHours(3);
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex",
            Quota(0.0),
            Credit(deadline),
            Config(tolerance: TimeSpan.FromHours(6)),
            Now);

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.NaturalResetArrivesInTime, advice.Reason);
    }

    // ---- DEADLINE: spend before plan-end / credit-expiry ------------------

    [Fact]
    public void Deadline_CreditExpiresBeforeNaturalReset_AdvisesSpend()
    {
        // Quota exhausted. The credit's advised spend-by is 1 day out, but the
        // natural reset is 5 days out — the free reset lands after the credit
        // is gone. Spend it now (before it's lost for nothing).
        var deadline = Now + TimeSpan.FromDays(1);
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex",
            Quota(0.0),
            Credit(deadline),
            Config(),
            Now);

        Assert.True(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.SpendBeforeDeadline, advice.Reason);
        Assert.Equal(deadline, advice.DecisionDeadline);
        Assert.Equal(Now + TimeSpan.FromDays(5), advice.PredictedNaturalReset);
        Assert.NotNull(advice.OptimalWindow);
        Assert.Equal(Now, advice.OptimalWindow!.Value.OpensAt);
        Assert.Equal(deadline, advice.OptimalWindow.Value.ClosesAt);
    }

    [Fact]
    public void Deadline_PlanEndsBeforeNaturalReset_AdvisesSpend()
    {
        // Quota exhausted, credit healthy (20d), but the PLAN ends in 2 days —
        // before the natural reset at 5 days. Quota after the plan end is
        // worthless, so spend the credit while it still has value.
        var planEnd = Now + TimeSpan.FromDays(2);
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex",
            Quota(0.0),
            Credit(Now + TimeSpan.FromDays(20)),
            Config(planEndsAt: planEnd),
            Now);

        Assert.True(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.SpendBeforeDeadline, advice.Reason);
        // Deadline is the tighter plan-end, not the credit expiry.
        Assert.Equal(planEnd, advice.DecisionDeadline);
        Assert.Equal(planEnd, advice.OptimalWindow!.Value.ClosesAt);
    }

    [Fact]
    public void Deadline_UsesMinOfPlanEndAndCreditExpiry()
    {
        // Both a plan end (3d) and a credit expiry (1d) are set; the decision
        // deadline is the earlier of the two (the credit expiry at 1d).
        var creditSpendBy = Now + TimeSpan.FromDays(1);
        var planEnd = Now + TimeSpan.FromDays(3);
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex",
            Quota(0.0),
            Credit(creditSpendBy),
            Config(planEndsAt: planEnd),
            Now);

        Assert.True(advice.ShouldSpend);
        Assert.Equal(creditSpendBy, advice.DecisionDeadline);
    }

    // ---- Guard branches ---------------------------------------------------

    [Fact]
    public void AgentOutsideConfiguredSet_IsNotApplicable()
    {
        var advice = ResetOptimalityEvaluator.Evaluate(
            "claude",
            Quota(0.0),
            Credit(Now + TimeSpan.FromDays(1)),
            Config(agents: new[] { "codex" }),
            Now);

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.NotApplicableAgent, advice.Reason);
    }

    [Fact]
    public void AgentMatchIsCaseInsensitive()
    {
        var advice = ResetOptimalityEvaluator.Evaluate(
            "CODEX",
            Quota(0.0),
            Credit(Now + TimeSpan.FromDays(1)),
            Config(agents: new[] { "codex" }),
            Now);

        Assert.Equal(ResetAdviceReason.SpendBeforeDeadline, advice.Reason);
    }

    [Fact]
    public void NoCadenceAnchor_IsConfigurationInvalid()
    {
        var config = Config() with { CadenceAnchor = null };
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex", Quota(0.0), Credit(Now + TimeSpan.FromDays(1)), config, Now);

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.ConfigurationInvalid, advice.Reason);
    }

    [Fact]
    public void NonPositiveCadencePeriod_IsConfigurationInvalid()
    {
        var config = Config() with { CadencePeriod = TimeSpan.Zero };
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex", Quota(0.0), Credit(Now + TimeSpan.FromDays(1)), config, Now);

        Assert.Equal(ResetAdviceReason.ConfigurationInvalid, advice.Reason);
    }

    [Fact]
    public void UnknownQuota_CannotEvaluateBurnFirst()
    {
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex", UnknownQuota(), Credit(Now + TimeSpan.FromDays(1)), Config(), Now);

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.QuotaReadingUnavailable, advice.Reason);
        Assert.Null(advice.UsableQuotaPct);
    }

    [Fact]
    public void NoBankedCredit_HasNothingToSpend()
    {
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex", Quota(0.0), NoCredit(), Config(), Now);

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.NoBankedCredit, advice.Reason);
    }

    [Fact]
    public void DecisionDeadlineInThePast_IsDeadlinePassed()
    {
        // The credit's advised spend-by is already behind us — no value left.
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex", Quota(0.0), Credit(Now - TimeSpan.FromHours(1)), Config(), Now);

        Assert.False(advice.ShouldSpend);
        Assert.Equal(ResetAdviceReason.DeadlinePassed, advice.Reason);
    }

    [Fact]
    public void EstimatedCreditFlag_IsPropagatedToAdvice()
    {
        var advice = ResetOptimalityEvaluator.Evaluate(
            "codex",
            Quota(0.0),
            Credit(Now + TimeSpan.FromDays(1), estimated: true),
            Config(),
            Now);

        Assert.True(advice.ShouldSpend);
        Assert.True(advice.NextCreditIsEstimated);
    }
}

using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Drives <see cref="ResetCreditExpiryTracker.Track"/> — the pure derivation of
/// banked reset-credit expiries from an observed <c>available_count</c> series.
/// Covers the acceptance contract: grant-times pinned to the last-lower-value
/// sample (including across a downtime gap that spans a grant), FIFO retirement
/// on a decrement, <c>nextCreditExpiresAt = oldest_grant + 30d - 24h</c>, and
/// seeded pre-observation credits flagged estimated.
/// </summary>
public sealed class ResetCreditExpiryTrackerTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
    private static readonly TimeSpan ThirtyDays = TimeSpan.FromDays(30);
    private static readonly TimeSpan TwentyFourHours = TimeSpan.FromHours(24);

    private static ResetCreditObservation Obs(int minutes, int count)
        => new(Base + TimeSpan.FromMinutes(minutes), count);

    private static ResetCreditExpiryConfig DefaultConfig(params SeededResetCredit[] seeds)
        => new() { SeededCredits = seeds };

    [Fact]
    public void IncrementAcrossDowntimeGap_PinsGrantToLastSampleAtLowerCount()
    {
        // Count sits at 0 through three samples, the last at t+30m. The
        // orchestrator is then DOWN for hours; when it returns the count is 1.
        // The grant happened somewhere in the gap — the tracker pins it to the
        // last sample at the previous lower count (t+30m), the earliest-possible
        // grant instant. A measurement gap can only push the inferred grant
        // EARLIER, never later, so it can never under-estimate a credit's age.
        var observations = new[]
        {
            Obs(0, 0),
            Obs(15, 0),
            Obs(30, 0),   // last sample at count 0
            // ---- downtime gap spanning the grant ----
            Obs(240, 1),  // count is now 1
        };

        var report = ResetCreditExpiryTracker.Track(observations, DefaultConfig());

        var credit = Assert.Single(report.Credits);
        Assert.Equal(Base + TimeSpan.FromMinutes(30), credit.GrantedAt);
        Assert.False(credit.IsEstimated);
        Assert.Equal(credit.GrantedAt + ThirtyDays, credit.ExpiresAt);
        Assert.Equal(credit.ExpiresAt - TwentyFourHours, credit.AdvisedSpendByAt);

        // nextCreditExpiresAt = oldest_grant + 30d - 24h.
        Assert.Equal(
            Base + TimeSpan.FromMinutes(30) + ThirtyDays - TwentyFourHours,
            report.NextCreditExpiresAt);
        Assert.Equal(1, report.LatestObservedCount);
    }

    [Fact]
    public void MultiStepJumpAcrossGap_PinsEveryNewCreditToTheSameEarliestBound()
    {
        // A jump of +2 across a gap: both new credits are pinned to the last
        // sample at the lower count (the earliest-possible instant for each).
        var observations = new[]
        {
            Obs(0, 0),
            Obs(15, 0),   // last sample at count 0
            Obs(180, 2),  // jumped straight to 2 across the gap
        };

        var report = ResetCreditExpiryTracker.Track(observations, DefaultConfig());

        Assert.Equal(2, report.Credits.Count);
        Assert.All(report.Credits, c => Assert.Equal(Base + TimeSpan.FromMinutes(15), c.GrantedAt));
        Assert.All(report.Credits, c => Assert.False(c.IsEstimated));
    }

    [Fact]
    public void Decrement_RetiresOldestGrantFirst_Fifo()
    {
        // Two grants, then one spend. FIFO retires the OLDEST (closest to
        // expiry); the newer grant survives.
        var observations = new[]
        {
            Obs(0, 0),
            Obs(15, 1),   // grant #1 pinned to t+0
            Obs(30, 2),   // grant #2 pinned to t+15
            Obs(45, 1),   // spend one — retire grant #1 (oldest)
        };

        var report = ResetCreditExpiryTracker.Track(observations, DefaultConfig());

        var survivor = Assert.Single(report.Credits);
        Assert.Equal(Base + TimeSpan.FromMinutes(15), survivor.GrantedAt);
    }

    [Fact]
    public void DecrementBelowTrackedCount_IsSafeNoOp()
    {
        // Baseline of 3 credits exists before tracking began but was never
        // seeded, so none are tracked. A drop to 0 must not throw or produce
        // phantom negative state — it is a clamped no-op.
        var observations = new[]
        {
            Obs(0, 3),   // baseline (pre-observation, untracked)
            Obs(15, 0),  // all spent
        };

        var report = ResetCreditExpiryTracker.Track(observations, DefaultConfig());

        Assert.Empty(report.Credits);
        Assert.Null(report.NextCreditExpiresAt);
        Assert.Equal(0, report.LatestObservedCount);
    }

    [Fact]
    public void SeededCredits_AreFlaggedEstimated_AndOrderedBeforeObservedGrants()
    {
        // A pre-observation credit the operator seeded with an estimate that
        // expires BEFORE any observed grant would. It must be flagged estimated
        // and ordered first (oldest) in the FIFO queue.
        var seedExpiry = Base + TimeSpan.FromDays(10);
        var config = DefaultConfig(new SeededResetCredit
        {
            EstimatedExpiresAt = seedExpiry,
            Label = "credit A — burn within 2 weeks",
        });

        var observations = new[]
        {
            Obs(0, 1),
            Obs(15, 2),  // observed grant pinned to t+0
        };

        var report = ResetCreditExpiryTracker.Track(observations, config);

        Assert.Equal(2, report.Credits.Count);

        var seed = report.Credits[0];
        Assert.True(seed.IsEstimated);
        Assert.Equal(seedExpiry, seed.ExpiresAt);
        Assert.Equal("credit A — burn within 2 weeks", seed.Label);
        Assert.Equal(seedExpiry - TwentyFourHours, seed.AdvisedSpendByAt);

        var observed = report.Credits[1];
        Assert.False(observed.IsEstimated);
        Assert.Null(observed.Label);

        // The seed expires first, so it drives nextCreditExpiresAt.
        Assert.Equal(seedExpiry - TwentyFourHours, report.NextCreditExpiresAt);
    }

    [Fact]
    public void SeededCredits_SortedByEstimatedExpiryAscending_RetiredClosestFirst()
    {
        var earlier = Base + TimeSpan.FromDays(5);
        var later = Base + TimeSpan.FromDays(20);
        // Supplied out of order to prove the tracker sorts them.
        var config = DefaultConfig(
            new SeededResetCredit { EstimatedExpiresAt = later, Label = "B" },
            new SeededResetCredit { EstimatedExpiresAt = earlier, Label = "A" });

        var observations = new[]
        {
            Obs(0, 2),   // baseline matching the two seeds
            Obs(15, 1),  // spend one — must retire the earlier-expiring seed (A)
        };

        var report = ResetCreditExpiryTracker.Track(observations, config);

        var survivor = Assert.Single(report.Credits);
        Assert.Equal("B", survivor.Label);
        Assert.Equal(later, survivor.ExpiresAt);
    }

    [Fact]
    public void NoObservationsNoSeeds_ProducesEmptyReport()
    {
        var report = ResetCreditExpiryTracker.Track(Array.Empty<ResetCreditObservation>(), DefaultConfig());

        Assert.Empty(report.Credits);
        Assert.Null(report.NextCreditExpiresAt);
        Assert.Null(report.LatestObservedCount);
        Assert.Equal(ThirtyDays, report.ExpiryPeriod);
        Assert.Equal(TwentyFourHours, report.SafetyBuffer);
    }

    [Fact]
    public void SingleObservation_EstablishesBaselineOnly_TracksNoCredits()
    {
        // One reading cannot difference into a grant; the credits it reports are
        // pre-observation and would need seeding to be tracked.
        var report = ResetCreditExpiryTracker.Track(new[] { Obs(0, 4) }, DefaultConfig());

        Assert.Empty(report.Credits);
        Assert.Equal(4, report.LatestObservedCount);
        Assert.Null(report.NextCreditExpiresAt);
    }

    [Fact]
    public void CustomConfig_UsesConfiguredExpiryPeriodAndSafetyBuffer()
    {
        var config = new ResetCreditExpiryConfig
        {
            ExpiryPeriod = TimeSpan.FromDays(7),
            SafetyBuffer = TimeSpan.FromHours(12),
        };
        var observations = new[] { Obs(0, 0), Obs(15, 1) };

        var report = ResetCreditExpiryTracker.Track(observations, config);

        var credit = Assert.Single(report.Credits);
        Assert.Equal(Base + TimeSpan.FromDays(7), credit.ExpiresAt);
        Assert.Equal(Base + TimeSpan.FromDays(7) - TimeSpan.FromHours(12), credit.AdvisedSpendByAt);
        Assert.Equal(credit.AdvisedSpendByAt, report.NextCreditExpiresAt);
        Assert.Equal(TimeSpan.FromDays(7), report.ExpiryPeriod);
        Assert.Equal(TimeSpan.FromHours(12), report.SafetyBuffer);
    }

    [Fact]
    public void OutOfOrderObservations_AreNormalisedByTimestamp()
    {
        // Same series as the FIFO test but shuffled — the result must be
        // identical because the tracker sorts by SampledAt.
        var shuffled = new[]
        {
            Obs(45, 1),
            Obs(0, 0),
            Obs(30, 2),
            Obs(15, 1),
        };

        var report = ResetCreditExpiryTracker.Track(shuffled, DefaultConfig());

        var survivor = Assert.Single(report.Credits);
        Assert.Equal(Base + TimeSpan.FromMinutes(15), survivor.GrantedAt);
    }

    [Fact]
    public void SawtoothWithGrantAfterFullDrain_PinsToLastSampleAtDrainedCount()
    {
        // 1 → 0 (spend) → stays 0 → 1 (new grant). The new grant must pin to the
        // last sample at count 0, not to the earlier count-1 samples.
        var observations = new[]
        {
            Obs(0, 1),
            Obs(15, 0),   // drained
            Obs(30, 0),   // still 0 — last sample at the drained count
            Obs(45, 1),   // new grant
        };

        var report = ResetCreditExpiryTracker.Track(observations, DefaultConfig());

        var credit = Assert.Single(report.Credits);
        Assert.Equal(Base + TimeSpan.FromMinutes(30), credit.GrantedAt);
    }
}

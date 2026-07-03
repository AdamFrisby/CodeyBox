using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Drives <see cref="NaturalResetCadence"/>: predicting the next weekly natural
/// reset from an anchor + period (never from the over-predicting API field), and
/// phase-refining the anchor from observed resets (self-calibration).
/// </summary>
public sealed class NaturalResetCadenceTests
{
    private static readonly DateTimeOffset Monday0600 = DateTimeOffset.Parse("2026-06-01T06:00:00Z");
    private static readonly TimeSpan Weekly = TimeSpan.FromDays(7);

    [Fact]
    public void PredictNextReset_AnchorInThePast_ReturnsNextBoundaryAfterNow()
    {
        // Anchor is a Monday; now is the following Wednesday. Next reset is the
        // Monday after (anchor + 7d).
        var now = Monday0600 + TimeSpan.FromDays(2);
        var next = NaturalResetCadence.PredictNextReset(now, Monday0600, Weekly);
        Assert.Equal(Monday0600 + Weekly, next);
    }

    [Fact]
    public void PredictNextReset_AnchorInTheFuture_StillReturnsFirstBoundaryAfterNow()
    {
        // The anchor may be a future schedule point; the prediction still walks
        // back to the first boundary strictly after now.
        var futureAnchor = Monday0600 + TimeSpan.FromDays(70); // 10 weeks out
        var now = Monday0600 + TimeSpan.FromDays(3);
        var next = NaturalResetCadence.PredictNextReset(now, futureAnchor, Weekly);
        Assert.Equal(Monday0600 + Weekly, next); // day 7, first boundary after day 3
    }

    [Fact]
    public void PredictNextReset_NowExactlyOnBoundary_ReturnsFollowingBoundary()
    {
        // On the boundary, that reset has already happened — the NEXT one is a
        // period later. "Strictly after now" avoids re-reporting the current one.
        var next = NaturalResetCadence.PredictNextReset(Monday0600, Monday0600, Weekly);
        Assert.Equal(Monday0600 + Weekly, next);
    }

    [Fact]
    public void PredictNextReset_NonPositivePeriod_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => NaturalResetCadence.PredictNextReset(Monday0600, Monday0600, TimeSpan.Zero));
    }

    [Fact]
    public void RefineAnchor_NoObservations_ReturnsConfiguredAnchor()
    {
        var refined = NaturalResetCadence.RefineAnchor(
            Monday0600, Weekly, Array.Empty<DateTimeOffset>(), TimeSpan.FromHours(6));
        Assert.Equal(Monday0600, refined);
    }

    [Fact]
    public void RefineAnchor_ObservationsWithinTolerance_KeepsConfiguredAnchor()
    {
        // Observed resets land ~2h off the configured phase — inside the 6h
        // tolerance, so the anchor is kept (no churn on noise).
        var observed = new[]
        {
            Monday0600 + TimeSpan.FromHours(2),
            Monday0600 + Weekly + TimeSpan.FromHours(2),
            Monday0600 + 2 * Weekly + TimeSpan.FromHours(1),
        };
        var refined = NaturalResetCadence.RefineAnchor(
            Monday0600, Weekly, observed, TimeSpan.FromHours(6));
        Assert.Equal(Monday0600, refined);
    }

    [Fact]
    public void RefineAnchor_ConsistentDriftBeyondTolerance_ShiftsAnchorByMedianResidual()
    {
        // Every observed reset is a consistent +10h off the configured phase —
        // beyond the 6h tolerance — so the anchor snaps forward by the median
        // residual (10h).
        var drift = TimeSpan.FromHours(10);
        var observed = new[]
        {
            Monday0600 + drift,
            Monday0600 + Weekly + drift,
            Monday0600 + 2 * Weekly + drift,
        };
        var refined = NaturalResetCadence.RefineAnchor(
            Monday0600, Weekly, observed, TimeSpan.FromHours(6));
        Assert.Equal(Monday0600 + drift, refined);
    }

    [Fact]
    public void RefineAnchor_NegativeDrift_ShiftsAnchorEarlier()
    {
        // Resets land BEFORE the configured phase — the residual is negative and
        // the anchor snaps earlier.
        var drift = TimeSpan.FromHours(-9);
        var observed = new[]
        {
            Monday0600 + drift,
            Monday0600 + Weekly + drift,
        };
        var refined = NaturalResetCadence.RefineAnchor(
            Monday0600, Weekly, observed, TimeSpan.FromHours(6));
        Assert.Equal(Monday0600 + drift, refined);
    }

    [Fact]
    public void RefineAnchor_UsesMedian_SoOneOutlierDoesNotDominate()
    {
        // Two clustered observations at +8h and one wild outlier; the median
        // residual (+8h) drives the shift, not the outlier.
        var observed = new[]
        {
            Monday0600 + TimeSpan.FromHours(8),
            Monday0600 + Weekly + TimeSpan.FromHours(8),
            Monday0600 + 2 * Weekly + TimeSpan.FromHours(8),
            Monday0600 + 3 * Weekly - TimeSpan.FromHours(80), // outlier (wraps toward the previous boundary)
        };
        var refined = NaturalResetCadence.RefineAnchor(
            Monday0600, Weekly, observed, TimeSpan.FromHours(6));
        Assert.Equal(Monday0600 + TimeSpan.FromHours(8), refined);
    }
}

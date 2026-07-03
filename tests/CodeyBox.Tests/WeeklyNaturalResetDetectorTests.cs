using CodeyBox.Core;
using CodeyBox.StatisticsPlugin;

namespace CodeyBox.Tests;

/// <summary>
/// Drives <see cref="WeeklyNaturalResetDetector.Detect"/> — inferring natural
/// weekly-reset instants from the logged weekly-window series (a spent window
/// refilling to a fresh one), used to self-calibrate the cadence anchor.
/// </summary>
public sealed class WeeklyNaturalResetDetectorTests
{
    private static readonly DateTimeOffset Base = DateTimeOffset.Parse("2026-06-01T00:00:00Z");

    private static QuotaSampleRow Row(int hours, double? windowPct, bool isKnown = true)
        => new(
            SampledAt: Base + TimeSpan.FromHours(hours),
            Agent: "codex",
            ModelId: null,
            OverallPct: windowPct ?? 0,
            WouldAllow: (windowPct ?? 0) > 0,
            Notes: null,
            WindowName: "weekly",
            WindowPct: windowPct,
            WindowResetAt: null,
            IsKnown: isKnown,
            UnknownReason: isKnown ? null : "Transient");

    [Fact]
    public void Detect_SpentThenFresh_RecordsThePostJumpInstant()
    {
        var rows = new[]
        {
            Row(0, 100),
            Row(24, 10),   // spent down
            Row(48, 5),    // still spent
            Row(72, 98),   // refilled → reset detected here
        };

        var resets = WeeklyNaturalResetDetector.Detect(rows);

        var instant = Assert.Single(resets);
        Assert.Equal(Base + TimeSpan.FromHours(72), instant);
    }

    [Fact]
    public void Detect_GradualDrainWithoutRefill_FindsNoReset()
    {
        var rows = new[]
        {
            Row(0, 100),
            Row(24, 70),
            Row(48, 40),
            Row(72, 12),
        };

        Assert.Empty(WeeklyNaturalResetDetector.Detect(rows));
    }

    [Fact]
    public void Detect_TwoResetsOverTwoWeeks_RecordsBoth()
    {
        var rows = new[]
        {
            Row(0, 20),
            Row(24, 95),   // reset #1
            Row(48, 30),
            Row(72, 8),
            Row(96, 90),   // reset #2
        };

        var resets = WeeklyNaturalResetDetector.Detect(rows);

        Assert.Equal(2, resets.Count);
        Assert.Equal(Base + TimeSpan.FromHours(24), resets[0]);
        Assert.Equal(Base + TimeSpan.FromHours(96), resets[1]);
    }

    [Fact]
    public void Detect_UnknownReadingBreaksTheChain_NoPhantomReset()
    {
        // A gap (unknown sample) between a spent and a fresh reading must not be
        // differenced across — a probe outage cannot fabricate a boundary.
        var rows = new[]
        {
            Row(0, 8),
            Row(24, null, isKnown: false), // gap
            Row(48, 96),                   // fresh, but preceded by a gap
        };

        Assert.Empty(WeeklyNaturalResetDetector.Detect(rows));
    }

    [Fact]
    public void Detect_SmallWiggleWithinBand_IsNotAReset()
    {
        // Movement that doesn't cross from spent (<25%) to fresh (>75%) is noise.
        var rows = new[]
        {
            Row(0, 40),
            Row(24, 60),
            Row(48, 55),
        };

        Assert.Empty(WeeklyNaturalResetDetector.Detect(rows));
    }
}

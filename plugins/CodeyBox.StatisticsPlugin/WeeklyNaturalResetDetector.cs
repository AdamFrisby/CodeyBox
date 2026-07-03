using CodeyBox.Core;

namespace CodeyBox.StatisticsPlugin;

/// <summary>
/// Detects natural weekly-reset instants from the logged <c>weekly</c>-window
/// quota series. A natural reset shows up as a sharp upward jump in the window's
/// usable percentage — a spent window (near 0%) refilling to a fresh one
/// (near 100%). The instant recorded is the first post-jump sample, an upper
/// bound on the true boundary; the phase-refinement (<see
/// cref="NaturalResetCadence.RefineAnchor"/>) tolerates this bounded error via
/// its median-of-residuals-with-tolerance rule.
///
/// <para>This is the self-calibration signal for the cadence anchor: the
/// provider's <c>reset_at</c> field over-predicts and must not be used, so the
/// natural boundary is learned from observed refills instead.</para>
/// </summary>
public static class WeeklyNaturalResetDetector
{
    /// <summary>
    /// Usable % below which the weekly window counts as spent. A reset must
    /// start from below this.
    /// </summary>
    private const double SpentBelowPct = 25.0;

    /// <summary>
    /// Usable % above which the weekly window counts as freshly reset. A reset
    /// must land above this.
    /// </summary>
    private const double FreshAbovePct = 75.0;

    /// <summary>
    /// Scans <paramref name="weeklyRows"/> (ordered ascending by sample time)
    /// and returns the instant of each detected weekly reset. Only transitions
    /// between two <em>known</em> readings count — an unknown sample is a gap,
    /// not a reset, so a probe outage cannot fabricate a boundary.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Detect(IReadOnlyList<QuotaSampleRow> weeklyRows)
    {
        ArgumentNullException.ThrowIfNull(weeklyRows);

        var resets = new List<DateTimeOffset>();
        double? previousPct = null;

        foreach (var row in weeklyRows)
        {
            // A gap (unknown reading or no window percentage) breaks the chain:
            // we cannot difference across it, so reset the baseline and skip.
            if (!row.IsKnown || row.WindowPct is not { } pct)
            {
                previousPct = null;
                continue;
            }

            if (previousPct is { } prev && prev < SpentBelowPct && pct > FreshAbovePct)
                resets.Add(row.SampledAt);

            previousPct = pct;
        }

        return resets;
    }
}

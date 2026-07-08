using CodeyBox.Core;

namespace CodeyBox.StatisticsPlugin;

/// <summary>
/// Detects natural reset instants from the logged reset-target quota series. A
/// natural reset shows up as a sharp upward jump in usable percentage — a spent
/// target window (near 0%) refilling to a fresh one
/// (near 100%). The instant recorded is the first post-jump sample, an upper
/// bound on the true boundary; the phase-refinement (<see
/// cref="NaturalResetCadence.RefineAnchor"/>) tolerates this bounded error via
/// its median-of-residuals-with-tolerance rule.
///
/// <para>This is the self-calibration signal for the cadence anchor: the
/// provider's <c>reset_at</c> field over-predicts and must not be used, so the
/// natural boundary is learned from observed refills instead.</para>
/// </summary>
internal static class WeeklyNaturalResetDetector
{
    /// <summary>
    /// Usable % below which the target window counts as spent. A reset must
    /// start from below this.
    /// </summary>
    private const double SpentBelowPct = 25.0;

    /// <summary>
    /// Usable % above which the target window counts as freshly reset. A reset
    /// must land above this.
    /// </summary>
    private const double FreshAbovePct = 75.0;

    /// <summary>
    /// Scans <paramref name="rows"/> (ordered ascending by sample time)
    /// and returns the instant of each detected natural reset. Only transitions
    /// between two <em>known</em> readings count — an unknown sample is a gap,
    /// not a reset, so a probe outage cannot fabricate a boundary.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Detect(IReadOnlyList<QuotaSampleRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var resets = new List<DateTimeOffset>();
        double? previousPct = null;

        foreach (var row in rows)
        {
            // A gap (unknown reading or no window percentage) breaks the chain:
            // we cannot difference across it, so reset the baseline and skip.
            var pct = row.WindowPct ?? (row.WindowName is null ? row.OverallPct : null);
            if (!row.IsKnown || pct is not { } usablePct)
            {
                previousPct = null;
                continue;
            }

            if (previousPct is { } prev && prev < SpentBelowPct && usablePct > FreshAbovePct)
                resets.Add(row.SampledAt);

            previousPct = usablePct;
        }

        return resets;
    }
}

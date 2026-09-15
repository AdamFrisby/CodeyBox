namespace CodeyBox.Admin.Model;

/// <summary>
/// Hot-reloadable knobs for the fleet map (layout, camera, node detail).
/// Operational values live here — never as literals in the layout, camera,
/// or styling logic — so the admin can bind them straight from configuration
/// (<c>CodeyBoxAdmin:FleetMap</c>) and pick them up without a restart.
/// </summary>
public sealed record FleetMapOptions
{
    /// <summary>Horizontal gap between dependency-depth columns, in map units.</summary>
    public double ColumnGap { get; init; } = 170.0;

    /// <summary>Vertical gap between nodes in the same column, in map units.</summary>
    public double RowGap { get; init; } = 96.0;

    /// <summary>Vertical gap between chain lanes, in map units.</summary>
    public double ChainLaneGap { get; init; } = 72.0;

    /// <summary>Deepest dependency column laid out; deeper nodes share the last column.</summary>
    public int MaxDepthColumns { get; init; } = 16;

    /// <summary>Node box width at working zoom, in map units.</summary>
    public double NodeWidth { get; init; } = 132.0;

    /// <summary>Node box height at working zoom, in map units.</summary>
    public double NodeHeight { get; init; } = 64.0;

    /// <summary>Seconds the idle camera dwells on one chain before moving on.</summary>
    public double DwellSeconds { get; init; } = 12.0;

    /// <summary>Attention score at or above which an item seizes the camera.</summary>
    public double UrgentAttentionThreshold { get; init; } = 60.0;

    /// <summary>How many top chains the idle camera cycles through.</summary>
    public int CycleTopChains { get; init; } = 8;

    /// <summary>Zoom applied when focusing a single item (1.0 is working zoom).</summary>
    public double ItemFocusZoom { get; init; } = 1.6;

    /// <summary>Zoom applied when nothing needs attention (whole-fleet overview).</summary>
    public double OverviewZoom { get; init; } = 1.0;

    /// <summary>Smallest zoom that keeps the overview legible; fit-zoom never goes below this.</summary>
    public double MinFitZoom { get; init; } = 0.15;

    /// <summary>Largest zoom the camera ever applies when fitting a region.</summary>
    public double MaxFitZoom { get; init; } = 2.0;

    /// <summary>Zoom at or above which nodes render full detail (title, agent, age, attempts).</summary>
    public double FullDetailZoom { get; init; } = 1.0;

    /// <summary>Zoom at or above which nodes render compact detail (short label); below is a bare shape.</summary>
    public double CompactDetailZoom { get; init; } = 0.55;

    /// <summary>Base title font size in screen pixels; text is drawn in screen space, never smaller than this.</summary>
    public double BaseTitleTextPx { get; init; } = 13.0;

    /// <summary>Base sub-label font size in screen pixels.</summary>
    public double BaseSubTextPx { get; init; } = 11.0;

    /// <summary>Minimum readable text size in screen pixels; nothing renders text below this.</summary>
    public double MinReadableTextPx { get; init; } = 10.0;

    /// <summary>Title characters kept in a full-detail label before ellipsizing.</summary>
    public int MaxLabelChars { get; init; } = 42;

    /// <summary>Upper bound on transition events emitted per snapshot pair.</summary>
    public int MaxTransitionsPerDiff { get; init; } = 256;
}

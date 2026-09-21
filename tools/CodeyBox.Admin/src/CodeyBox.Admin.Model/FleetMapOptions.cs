namespace CodeyBox.Admin.Model;

/// <summary>
/// Hot-reloadable knobs for the fleet map (layout, camera, semantic zoom,
/// node detail). Operational values live here — never as literals in the
/// layout, camera, or styling logic — so the admin can bind them straight
/// from configuration (<c>CodeyBoxAdmin:FleetMap</c>) and pick them up
/// without a restart.
/// </summary>
public sealed record FleetMapOptions
{
    // ── Layout: a horizontal dependency tree per lane ────────────────────

    /// <summary>Horizontal pitch between dependency-depth columns (and between
    /// the wrapped sub-columns of a wide fan-out), in map units.</summary>
    public double ColumnGap { get; init; } = 250.0;

    /// <summary>Vertical pitch between nodes in the same column, in map units.</summary>
    public double RowGap { get; init; } = 144.0;

    /// <summary>Vertical gap between chain lanes, in map units.</summary>
    public double ChainLaneGap { get; init; } = 64.0;

    /// <summary>Kept for configuration compatibility; the time layout packs rows by horizontal overlap instead.</summary>
    public int MaxRowsPerColumn { get; init; } = 8;

    // ── Time axis: past left, now centre, future right ──────────────────

    /// <summary>
    /// Minutes of faithful time one column gap stands for in the past. Inside
    /// a burst, spacing is elapsed time at this rate; quiet stretches are cut.
    /// </summary>
    public double PastMinutesPerColumn { get; init; } = 60.0;

    /// <summary>"Now" advances in steps of this many minutes so a settled view does not creep every second.</summary>
    public int NowBucketMinutes { get; init; } = 5;

    /// <summary>
    /// A stretch with no landing longer than this is cut from the axis and
    /// replaced by a labelled break. Fixed: the warp is computed once per
    /// snapshot and never depends on the camera.
    /// </summary>
    public double QuietGapMinutes { get; init; } = 120.0;

    /// <summary>Width of a break marker, in map units (never less than half a node).</summary>
    public double BreakWidth { get; init; } = 200.0;

    /// <summary>
    /// Two landings in one lane closer than this are spaced out to it (older
    /// pushed left), so boxes never overlap in the world; never less than a
    /// node's width.
    /// </summary>
    public double PastMinSpacing { get; init; } = 200.0;

    /// <summary>Concurrency assumed for the forecast when the fleet surface does not say.</summary>
    public int DefaultCapacity { get; init; } = 3;

    /// <summary>Predicted batches drawn at a full column each before the future compresses.</summary>
    public int FutureNearBatches { get; init; } = 6;

    /// <summary>Columns per natural-log unit of batches beyond the near future.</summary>
    public double FutureCompression { get; init; } = 3.0;

    /// <summary>Deepest dependency column laid out; deeper nodes share the last column.</summary>
    public int MaxDepthColumns { get; init; } = 16;

    /// <summary>Node card width at working zoom, in map units.</summary>
    public double NodeWidth { get; init; } = 180.0;

    /// <summary>Node card height at working zoom, in map units.</summary>
    public double NodeHeight { get; init; } = 120.0;

    // ── Camera: dwell, hold, travel ──────────────────────────────────────

    /// <summary>Base seconds the idle camera dwells on one chain before moving on.</summary>
    public double DwellSeconds { get; init; } = 12.0;

    /// <summary>Extra dwell per member of the framed chain — long enough to read.</summary>
    public double DwellSecondsPerItem { get; init; } = 1.0;

    /// <summary>Ceiling on a chain dwell, whatever its size.</summary>
    public double MaxDwellSeconds { get; init; } = 30.0;

    /// <summary>Seconds the idle camera rests on the whole-fleet overview at the end of each lap.</summary>
    public double OverviewDwellSeconds { get; init; } = 20.0;

    /// <summary>Attention score at or above which an item seizes the camera.</summary>
    public double UrgentAttentionThreshold { get; init; } = 60.0;

    /// <summary>
    /// Once an urgent item has taken the camera it is held at least this long,
    /// even if the urgency clears — a camera that flaps is worse than none.
    /// </summary>
    public double ItemHoldMinSeconds { get; init; } = 20.0;

    /// <summary>
    /// An urgent item older than this (by last update) no longer seizes the
    /// camera; it stays on the attention rail instead. Keeps a stale failure
    /// from parking the camera for a week.
    /// </summary>
    public double UrgentMaxAgeHours { get; init; } = 24.0;

    /// <summary>Most cards the attention rail shows at once (most urgent first when trimming).</summary>
    public int RailMaxCards { get; init; } = 12;

    /// <summary>How many active chains the idle camera cycles through per lap.</summary>
    public int CycleTopChains { get; init; } = 8;

    /// <summary>Shortest eased travel between two camera targets, in milliseconds.</summary>
    public int TravelMinMs { get; init; } = 500;

    /// <summary>Longest eased travel, in milliseconds.</summary>
    public int TravelMaxMs { get; init; } = 2200;

    /// <summary>Travel time added per screen pixel the framed centre moves.</summary>
    public double TravelMsPerScreenPx { get; init; } = 1.1;

    /// <summary>Travel time added per natural-log unit of zoom change.</summary>
    public double TravelMsPerZoomLog { get; init; } = 650.0;

    // ── Zoom levels: what the picture is about ───────────────────────────

    /// <summary>Zoom applied when focusing a single item (1.0 is working zoom). At or
    /// above <see cref="OpenZoomEnd"/> so a focused item is fully open.</summary>
    public double ItemFocusZoom { get; init; } = 3.0;

    /// <summary>Zoom applied when nothing needs attention (whole-fleet overview cap).</summary>
    public double OverviewZoom { get; init; } = 1.0;

    /// <summary>Smallest zoom that keeps the overview legible; fit-zoom never goes below this.</summary>
    public double MinFitZoom { get; init; } = 0.08;

    /// <summary>Fraction of the view kept as empty margin beyond the outermost node when panning is bounded.</summary>
    public double PanMarginFraction { get; init; } = 0.25;

    /// <summary>Largest zoom the camera ever applies when fitting a region.</summary>
    public double MaxFitZoom { get; init; } = 3.2;

    /// <summary>
    /// Zoom at which a node card has grown large enough to render its stage
    /// pipeline inside itself. Between this and <see cref="OpenZoomEnd"/> the
    /// renderer cross-fades the card's text into the pipeline — the same box,
    /// one continuous level-of-detail progression, applied to every node.
    /// </summary>
    public double OpenZoomStart { get; init; } = 2.0;

    /// <summary>Zoom at which every node's stage pipeline is fully materialised.</summary>
    public double OpenZoomEnd { get; init; } = 2.8;

    /// <summary>
    /// Upper bound on nodes whose history is fetched for one viewport. At
    /// pipeline zoom a screen holds a handful; this caps a huge display.
    /// </summary>
    public int MaxDetailFetch { get; init; } = 24;

    /// <summary>
    /// Under manual control, the node nearest the view centre within this
    /// many screen pixels opens once the zoom crosses the band.
    /// </summary>
    public double OpenPickRadiusPx { get; init; } = 220.0;

    // ── Node detail ──────────────────────────────────────────────────────

    /// <summary>Zoom at or above which nodes render full detail (title, agent, age, attempts).</summary>
    public double FullDetailZoom { get; init; } = 0.95;

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

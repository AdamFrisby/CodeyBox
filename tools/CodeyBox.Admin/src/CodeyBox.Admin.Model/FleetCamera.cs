namespace CodeyBox.Admin.Model;

/// <summary>What the camera is looking at.</summary>
public enum CameraFocusKind
{
    /// <summary>One work item fills the frame and opens into its stage pipeline.</summary>
    Item,
    /// <summary>A busy chain is framed as a whole.</summary>
    Chain,
    /// <summary>The idle fleet pulls back to show everything.</summary>
    All,
}

/// <summary>Size of the visible canvas, in screen pixels. Supplied by the renderer.</summary>
public sealed record CameraViewSize(double Width, double Height)
{
    public static CameraViewSize Default { get; } = new(1600, 900);
}

/// <summary>A camera position: what it frames and how close.</summary>
public sealed record CameraViewport
{
    public required double CenterX { get; init; }

    public required double CenterY { get; init; }

    /// <summary>1.0 is working zoom; larger zooms in.</summary>
    public required double Zoom { get; init; }
}

/// <summary>Full camera state. The renderer eases towards <see cref="Viewport"/>.</summary>
public sealed record CameraState
{
    public bool Manual { get; init; }

    public CameraFocusKind FocusKind { get; init; } = CameraFocusKind.All;

    public string FocusId { get; init; } = string.Empty;

    public required CameraViewport Viewport { get; init; }

    /// <summary>When the camera last changed target.</summary>
    public DateTimeOffset LastChangeAt { get; init; }

    /// <summary>Index of the current stop inside the idle cycle (-1 = not cycling).</summary>
    public int CycleIndex { get; init; }

    /// <summary>
    /// Milliseconds the renderer should take to ease from wherever it is to
    /// <see cref="Viewport"/>. Zero means a cut (reduced motion, manual drag).
    /// A pure function of the distance travelled, so a quiet fleet re-emits
    /// the same value and the frame stays byte-identical.
    /// </summary>
    public int TravelMs { get; init; }

    /// <summary>
    /// Item whose stage pipeline is open — the zoomed-in level of meaning.
    /// Set when the camera focuses one item, or when manual zoom crosses the
    /// open band over a node. Null at chain and overview levels.
    /// </summary>
    public string? OpenItemId { get; init; }

    /// <summary>Why the camera is holding (an urgent item's top reason); null when cycling.</summary>
    public string? HoldReason { get; init; }

    /// <summary>Seconds this stop dwells before the idle cycle moves on (0 when holding or manual).</summary>
    public double DwellSeconds { get; init; }
}

/// <summary>
/// Pure camera policy — the editorial layer. Urgent items (failures, parks,
/// conflicts: anything at or above the attention threshold) seize the camera
/// and hold it while unresolved; otherwise the camera dwells through the
/// active chains, long enough to read each, and rests on the whole-fleet
/// overview at the end of every lap; manual input suspends automation until
/// the operator explicitly resumes it. Every move carries an eased travel
/// time — never a cut. When <paramref name="reducedMotion"/> is set the
/// director never moves the camera on its own. No I/O, no clock; the caller
/// supplies <c>now</c>.
/// </summary>
public static class CameraDirector
{
    /// <summary>First frame: the whole fleet, pulled back.</summary>
    public static CameraState Initial(
        FleetMapLayout layout,
        DateTimeOffset now,
        CameraViewSize? viewSize = null,
        FleetMapOptions? options = null)
    {
        options ??= new FleetMapOptions();
        return new CameraState
        {
            Viewport = FitAll(layout, Validated(viewSize), options),
            LastChangeAt = now,
            CycleIndex = -1,
            TravelMs = 0,
            // The fleet is established first: auto-follow may not move until
            // the landing view has been seen.
            DwellSeconds = Math.Max(0, options.LandingDwellSeconds),
        };
    }

    /// <summary>Advances the camera one tick. Never throws on odd input.</summary>
    public static CameraState Next(
        CameraState state,
        FleetProjection projection,
        FleetMapLayout layout,
        DateTimeOffset now,
        CameraViewSize? viewSize = null,
        FleetMapOptions? options = null,
        bool reducedMotion = false,
        FleetSnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new FleetMapOptions();
        var view = Validated(viewSize);

        if (reducedMotion || state.Manual)
        {
            return state;
        }

        var sinceChange = (now - state.LastChangeAt).TotalSeconds;
        // The landing view: the whole fleet, held long enough to see it is a
        // fleet, before anything — urgent items included — takes the camera.
        if (state.FocusKind == CameraFocusKind.All && state.CycleIndex == -1 && sinceChange < state.DwellSeconds)
        {
            return state;
        }
        // Anti-flap: an urgency hold lasts at least the minimum even once it
        // clears. A *new* urgent item always preempts — it is more urgent.
        var holding = state.FocusKind == CameraFocusKind.Item
            && state.HoldReason is not null
            && sinceChange < options.ItemHoldMinSeconds;

        var urgent = FindUrgent(projection, options, snapshot, now);
        if (urgent is not null)
        {
            if (state.FocusKind == CameraFocusKind.Item
                && string.Equals(state.FocusId, urgent.ItemId, StringComparison.Ordinal))
            {
                return state;
            }
            return Move(state, FocusItem(layout, state.Viewport, urgent.ItemId, options), options) with
            {
                FocusKind = CameraFocusKind.Item,
                FocusId = urgent.ItemId,
                LastChangeAt = now,
                CycleIndex = -1,
                OpenItemId = urgent.ItemId,
                HoldReason = urgent.Reasons.Count > 0 ? urgent.Reasons[0] : "Needs attention.",
                DwellSeconds = 0,
            };
        }
        if (holding)
        {
            return state;
        }

        var stops = CycleStops(projection, layout, view, options);
        var currentIndex = stops.FindIndex(s => s.Kind == state.FocusKind
            && string.Equals(s.Id, state.FocusId, StringComparison.Ordinal));
        if (currentIndex >= 0 && state.CycleIndex >= 0 && sinceChange < state.DwellSeconds)
        {
            return state;
        }
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % stops.Count;
        var stop = stops[nextIndex];
        if (stop.Kind == state.FocusKind
            && string.Equals(stop.Id, state.FocusId, StringComparison.Ordinal)
            && state.CycleIndex >= 0)
        {
            // Single stop: the picture is already right; just restart the dwell clock.
            return state with { LastChangeAt = now, TravelMs = 0 };
        }
        return Move(state, stop.Viewport, options) with
        {
            FocusKind = stop.Kind,
            FocusId = stop.Id,
            LastChangeAt = now,
            CycleIndex = nextIndex,
            OpenItemId = stop.OpenItemId,
            HoldReason = null,
            DwellSeconds = stop.DwellSeconds,
        };
    }

    /// <summary>
    /// Manual pan/zoom takes control immediately and suspends automation.
    /// The camera stays here until <see cref="ResumeAuto"/> — a camera that
    /// yanks the view away while someone is reading is worse than no camera.
    /// Crossing the open band over a node opens it (semantic zoom).
    /// </summary>
    public static CameraState ApplyManual(
        CameraState state,
        CameraViewport viewport,
        DateTimeOffset now,
        FleetMapLayout? layout = null,
        FleetMapOptions? options = null,
        CameraViewSize? viewSize = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(viewport);
        options ??= new FleetMapOptions();
        var resolved = layout is not null;
        // With a view size known, the manual viewport is kept inside the
        // content bounds (the renderer applies the same clamp; this is the
        // idempotent server-side echo of it).
        if (resolved && viewSize is not null && viewSize.Width > 0 && viewSize.Height > 0)
        {
            viewport = CameraBounds.Clamp(viewport, layout!, viewSize, options);
        }
        var open = resolved ? SemanticZoom.Resolve(layout!, viewport, options) : state.OpenItemId;
        return state with
        {
            Manual = true,
            Viewport = viewport,
            LastChangeAt = now,
            TravelMs = 0,
            OpenItemId = open,
            HoldReason = null,
            DwellSeconds = 0,
            FocusKind = !resolved ? state.FocusKind : open is null ? CameraFocusKind.Chain : CameraFocusKind.Item,
            FocusId = !resolved ? state.FocusId : open ?? string.Empty,
        };
    }

    /// <summary>
    /// The operator picked a node: travel to it and open it. Automation stays
    /// suspended (this is manual interaction) until <see cref="ResumeAuto"/>.
    /// </summary>
    public static CameraState OpenItem(
        CameraState state,
        string itemId,
        FleetMapLayout layout,
        DateTimeOffset now,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new FleetMapOptions();
        if (string.IsNullOrEmpty(itemId) || !layout.Nodes.ContainsKey(itemId))
        {
            return state;
        }
        return Move(state, FocusItem(layout, state.Viewport, itemId, options), options) with
        {
            Manual = true,
            FocusKind = CameraFocusKind.Item,
            FocusId = itemId,
            LastChangeAt = now,
            CycleIndex = -1,
            OpenItemId = itemId,
            HoldReason = null,
            DwellSeconds = 0,
        };
    }

    /// <summary>Closes the open item: pull back just below the open band, same centre.</summary>
    public static CameraState CloseItem(CameraState state, DateTimeOffset now, FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        options ??= new FleetMapOptions();
        if (state.OpenItemId is null)
        {
            return state;
        }
        var target = state.Viewport with { Zoom = Math.Min(state.Viewport.Zoom, options.OpenZoomStart * 0.8) };
        return Move(state, target, options) with
        {
            Manual = true,
            FocusKind = CameraFocusKind.Chain,
            FocusId = string.Empty,
            LastChangeAt = now,
            CycleIndex = -1,
            OpenItemId = null,
            HoldReason = null,
            DwellSeconds = 0,
        };
    }

    /// <summary>The operator asked for the whole fleet: frame everything, automation stays suspended.</summary>
    public static CameraState Overview(
        CameraState state,
        FleetMapLayout layout,
        DateTimeOffset now,
        CameraViewSize? viewSize = null,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new FleetMapOptions();
        return Move(state, FitAll(layout, Validated(viewSize), options), options) with
        {
            Manual = true,
            FocusKind = CameraFocusKind.All,
            FocusId = string.Empty,
            LastChangeAt = now,
            CycleIndex = -1,
            OpenItemId = null,
            HoldReason = null,
            DwellSeconds = 0,
        };
    }

    /// <summary>Explicit way back to auto: keeps the current view, restarts the dwell clock.</summary>
    public static CameraState ResumeAuto(CameraState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Manual = false, LastChangeAt = now, TravelMs = 0, CycleIndex = -1 };
    }

    /// <summary>
    /// Fits a bounding box into the view. Zoom is a pure function of what is
    /// framed: a single node fills the frame at item zoom, a region fits its
    /// bounds, clamped to the configured fit range.
    /// </summary>
    public static CameraViewport FitViewport(
        double minX, double minY, double maxX, double maxY,
        CameraViewSize view, FleetMapOptions options)
    {
        var width = Math.Max(options.NodeWidth, maxX - minX + options.NodeWidth * 1.5);
        var height = Math.Max(options.NodeHeight, maxY - minY + options.NodeHeight * 3);
        var zoom = Math.Min(view.Width / width, view.Height / height);
        zoom = Math.Clamp(zoom, options.MinFitZoom, options.MaxFitZoom);
        return new CameraViewport
        {
            CenterX = (minX + maxX) / 2,
            CenterY = (minY + maxY) / 2,
            Zoom = zoom,
        };
    }

    /// <summary>
    /// Eased travel time between two viewports: a function of how far the
    /// framed centre moves on screen and how much the zoom changes, clamped
    /// to the configured range. Deterministic — identical moves ease alike.
    /// </summary>
    public static int TravelTime(CameraViewport from, CameraViewport to, FleetMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var zoomScale = Math.Max(Math.Max(from.Zoom, to.Zoom), 1e-6);
        var dx = (to.CenterX - from.CenterX) * zoomScale;
        var dy = (to.CenterY - from.CenterY) * zoomScale;
        var screenDistance = Math.Sqrt(dx * dx + dy * dy);
        var zoomLog = Math.Abs(Math.Log(Math.Max(to.Zoom, 1e-6) / Math.Max(from.Zoom, 1e-6)));
        if (screenDistance < 0.5 && zoomLog < 1e-4)
        {
            return 0;
        }
        var ms = options.TravelMinMs
            + screenDistance * options.TravelMsPerScreenPx
            + zoomLog * options.TravelMsPerZoomLog;
        if (!double.IsFinite(ms))
        {
            return options.TravelMaxMs;
        }
        return (int)Math.Clamp(Math.Round(ms), options.TravelMinMs, Math.Max(options.TravelMinMs, options.TravelMaxMs));
    }

    private static CameraState Move(CameraState state, CameraViewport target, FleetMapOptions options) =>
        state with
        {
            Viewport = target,
            TravelMs = TravelTime(state.Viewport, target, options),
        };

    private static CameraViewSize Validated(CameraViewSize? view) =>
        view is not null && view.Width > 0 && view.Height > 0
            ? view
            : CameraViewSize.Default;

    /// <summary>
    /// The most urgent item, if any is urgent enough — and, when the snapshot
    /// is supplied, recent enough: a failure from last week is on the rail,
    /// not something the camera should hold on for hours.
    /// </summary>
    private static AttentionScore? FindUrgent(
        FleetProjection projection, FleetMapOptions options, FleetSnapshot? snapshot, DateTimeOffset now)
    {
        Dictionary<string, DateTimeOffset>? updatedAt = null;
        if (snapshot is not null)
        {
            updatedAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var item in snapshot.Items ?? [])
            {
                if (item is not null && !string.IsNullOrEmpty(item.Id))
                {
                    updatedAt.TryAdd(item.Id, item.UpdatedAt);
                }
            }
        }
        var maxAge = TimeSpan.FromHours(Math.Max(0, options.UrgentMaxAgeHours));
        foreach (var score in projection.Attention ?? [])
        {
            if (score is null || score.Score < options.UrgentAttentionThreshold)
            {
                continue;
            }
            if (updatedAt is not null && updatedAt.TryGetValue(score.ItemId, out var at) && now - at > maxAge)
            {
                continue;
            }
            return score;
        }
        return null;
    }

    private sealed record CycleStop(
        CameraFocusKind Kind, string Id, CameraViewport Viewport, string? OpenItemId, double DwellSeconds);

    /// <summary>
    /// The idle lap: the most attention-worthy *active* chains — a chain
    /// where something runs, waits for a slot, or is parked — then the
    /// overview. Chains blocked end-to-end by dependencies are dormant; they
    /// are the shape of the backlog, read from the overview, not a stop.
    /// </summary>
    private static List<CycleStop> CycleStops(
        FleetProjection projection, FleetMapLayout layout, CameraViewSize view, FleetMapOptions options)
    {
        var stops = new List<CycleStop>();
        var members = layout.Nodes?.Values
            .GroupBy(n => n.ChainId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal)
            ?? new Dictionary<string, List<MapNodeLayout>>(StringComparer.Ordinal);
        foreach (var chain in projection.ChainAttention ?? [])
        {
            if (chain is null || chain.Score <= 0 || !IsActive(chain, projection))
            {
                continue;
            }
            if (!members.TryGetValue(chain.ChainId, out var nodes) || nodes.Count == 0)
            {
                continue;
            }
            var dwell = Math.Clamp(
                options.DwellSeconds + nodes.Count * options.DwellSecondsPerItem,
                options.DwellSeconds,
                Math.Max(options.DwellSeconds, options.MaxDwellSeconds));
            if (nodes.Count == 1)
            {
                stops.Add(new CycleStop(
                    CameraFocusKind.Chain, chain.ChainId,
                    FocusItem(layout, null, nodes[0].ItemId, options),
                    nodes[0].ItemId, dwell));
            }
            else
            {
                var fit = FitViewport(
                    nodes.Min(n => n.X), nodes.Min(n => n.Y),
                    nodes.Max(n => n.X), nodes.Max(n => n.Y),
                    view, options);
                // A chain stop never opens a member by accident: cap under the open band.
                fit = fit with { Zoom = Math.Min(fit.Zoom, Math.Max(options.MinFitZoom, options.OpenZoomStart * 0.95)) };
                stops.Add(new CycleStop(CameraFocusKind.Chain, chain.ChainId, fit, null, dwell));
            }
            if (stops.Count >= Math.Max(1, options.CycleTopChains))
            {
                break;
            }
        }
        stops.Add(new CycleStop(
            CameraFocusKind.All, string.Empty,
            FitAll(layout, view, options), null,
            Math.Max(1, options.OverviewDwellSeconds)));
        return stops;
    }

    /// <summary>
    /// A chain is a region worth visiting when something in it is happening
    /// or stuck on its own account: running, parked, failed, or in an
    /// unrecognised state. Queued-and-waiting — on a dependency, a slot, or a
    /// paused agent — is the backlog's shape, read from the overview and the
    /// counts; touring it one item at a time would be motion without meaning.
    /// </summary>
    private static bool IsActive(ChainAttention chain, FleetProjection projection)
    {
        foreach (var id in chain.ItemIds ?? [])
        {
            if (projection.Activities is not null
                && projection.Activities.TryGetValue(id, out var activity)
                && activity.Kind is ActivityKind.Running or ActivityKind.Parked or ActivityKind.Failed or ActivityKind.Unknown)
            {
                return true;
            }
        }
        return false;
    }

    private static CameraViewport FocusItem(
        FleetMapLayout layout, CameraViewport? fallback, string itemId, FleetMapOptions options)
    {
        if (layout.Nodes is not null && layout.Nodes.TryGetValue(itemId, out var node))
        {
            return new CameraViewport
            {
                CenterX = node.X,
                CenterY = node.Y,
                Zoom = Math.Min(options.ItemFocusZoom, options.MaxFitZoom),
            };
        }
        return fallback ?? new CameraViewport { CenterX = 0, CenterY = 0, Zoom = options.OverviewZoom };
    }

    private static CameraViewport FitAll(
        FleetMapLayout layout, CameraViewSize view, FleetMapOptions options)
    {
        var nodes = layout.Nodes?.Values.ToList() ?? [];
        if (nodes.Count == 0)
        {
            return new CameraViewport { CenterX = 0, CenterY = 0, Zoom = options.OverviewZoom };
        }
        var fit = FitViewport(
            nodes.Min(n => n.X), nodes.Min(n => n.Y),
            nodes.Max(n => n.X), nodes.Max(n => n.Y),
            view, options);
        if (fit.Zoom > options.MinFitZoom + 1e-9)
        {
            return fit with { Zoom = Math.Min(fit.Zoom, options.OverviewZoom) };
        }
        // Months of history do not fit one screen at a legible zoom. The
        // overview is then the live work — now and the forecast — with the
        // history running off to the left, where panning finds it.
        var live = nodes.Where(n => n.Zone != AxisZone.Past).ToList();
        if (live.Count == 0)
        {
            return fit;
        }
        var liveFit = FitViewport(
            live.Min(n => n.X), live.Min(n => n.Y),
            live.Max(n => n.X), live.Max(n => n.Y),
            view, options);
        return liveFit with { Zoom = Math.Min(liveFit.Zoom, options.OverviewZoom) };
    }
}

/// <summary>
/// Semantic zoom: which item, if any, is open at a given viewport. Pure
/// geometry over the layout — used for manual viewports, where the operator
/// zooms in over a node and it opens into its stage pipeline.
/// </summary>
public static class SemanticZoom
{
    /// <summary>
    /// The node nearest the view centre, within <see cref="FleetMapOptions.OpenPickRadiusPx"/>
    /// screen pixels, once the zoom is inside or past the open band. Null otherwise.
    /// </summary>
    public static string? Resolve(FleetMapLayout layout, CameraViewport viewport, FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(viewport);
        options ??= new FleetMapOptions();
        if (!double.IsFinite(viewport.Zoom) || viewport.Zoom < options.OpenZoomStart || layout.Nodes is null)
        {
            return null;
        }
        string? best = null;
        var bestDistance = double.MaxValue;
        foreach (var node in layout.Nodes.Values.OrderBy(n => n.ItemId, StringComparer.Ordinal))
        {
            var dx = (node.X - viewport.CenterX) * viewport.Zoom;
            var dy = (node.Y - viewport.CenterY) * viewport.Zoom;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= options.OpenPickRadiusPx && distance < bestDistance)
            {
                best = node.ItemId;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>
    /// Nodes whose box intersects the view once the zoom is inside or past the
    /// open band — the set whose stage history the page must have on hand,
    /// because at that zoom every one of them renders its pipeline. Sorted by
    /// id, capped at <see cref="FleetMapOptions.MaxDetailFetch"/>; empty below
    /// the band so a quiet overview fetches nothing.
    /// </summary>
    public static IReadOnlyList<string> VisibleItems(
        FleetMapLayout layout, CameraViewport viewport, CameraViewSize? viewSize = null, FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(viewport);
        options ??= new FleetMapOptions();
        if (!double.IsFinite(viewport.Zoom) || viewport.Zoom < options.OpenZoomStart || layout.Nodes is null)
        {
            return [];
        }
        var view = viewSize is { Width: > 0, Height: > 0 } ? viewSize : CameraViewSize.Default;
        var halfW = view.Width / 2 / viewport.Zoom + options.NodeWidth / 2;
        var halfH = view.Height / 2 / viewport.Zoom + options.NodeHeight / 2;
        var result = new List<string>();
        foreach (var node in layout.Nodes.Values.OrderBy(n => n.ItemId, StringComparer.Ordinal))
        {
            if (Math.Abs(node.X - viewport.CenterX) <= halfW && Math.Abs(node.Y - viewport.CenterY) <= halfH)
            {
                result.Add(node.ItemId);
                if (result.Count >= Math.Max(1, options.MaxDetailFetch))
                {
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 0 → card text, 1 → pipeline fully rendered; linear inside the band.
    /// The renderer uses this to cross-fade, so zooming changes what the
    /// picture is about continuously rather than by a cut.
    /// </summary>
    public static double OpenFraction(double zoom, FleetMapOptions? options = null)
    {
        options ??= new FleetMapOptions();
        var start = options.OpenZoomStart;
        var end = Math.Max(options.OpenZoomEnd, start + 1e-6);
        if (!double.IsFinite(zoom))
        {
            return 0;
        }
        return Math.Clamp((zoom - start) / (end - start), 0, 1);
    }
}

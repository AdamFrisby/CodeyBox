namespace CodeyBox.Admin.Model;

/// <summary>What the camera is looking at.</summary>
public enum CameraFocusKind
{
    /// <summary>One work item fills the frame (failures, parks, conflicts).</summary>
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

    /// <summary>Index of the focused chain inside the current cycle list.</summary>
    public int CycleIndex { get; init; }
}

/// <summary>
/// Pure camera policy over the attention ranking: urgent items (failures,
/// parks, conflicts — anything at or above the attention threshold) seize the
/// camera and hold it while unresolved; otherwise the camera dwells through
/// the most active chains; manual input suspends automation until the operator
/// explicitly resumes it. When <paramref name="reducedMotion"/> is set the
/// director never moves the camera on its own — the screen stays fully usable
/// as a static map. No I/O, no clock; the caller supplies <c>now</c>.
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
            Viewport = FitAll(layout, viewSize ?? CameraViewSize.Default, options),
            LastChangeAt = now,
            CycleIndex = -1,
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
        bool reducedMotion = false)
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

        var urgent = FindUrgent(projection, options);
        if (urgent is not null)
        {
            if (state.FocusKind == CameraFocusKind.Item
                && string.Equals(state.FocusId, urgent, StringComparison.Ordinal))
            {
                return state;
            }
            return new CameraState
            {
                Viewport = FocusItem(layout, state.Viewport, urgent, options),
                FocusKind = CameraFocusKind.Item,
                FocusId = urgent,
                LastChangeAt = now,
                CycleIndex = -1,
            };
        }

        var cycle = CycleCandidates(projection, options);
        if (cycle.Count == 0)
        {
            if (state.FocusKind == CameraFocusKind.All && state.FocusId.Length == 0)
            {
                return state;
            }
            return new CameraState
            {
                Viewport = FitAll(layout, view, options),
                FocusKind = CameraFocusKind.All,
                FocusId = string.Empty,
                LastChangeAt = now,
                CycleIndex = -1,
            };
        }

        var currentIndex = cycle.FindIndex(c =>
            string.Equals(c, state.FocusId, StringComparison.Ordinal));
        if (state.FocusKind == CameraFocusKind.Chain
            && currentIndex >= 0
            && (now - state.LastChangeAt).TotalSeconds < options.DwellSeconds)
        {
            return state;
        }
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % cycle.Count;
        var chainId = cycle[nextIndex];
        if (state.FocusKind == CameraFocusKind.Chain
            && string.Equals(state.FocusId, chainId, StringComparison.Ordinal))
        {
            return state;
        }
        return new CameraState
        {
            Viewport = FocusChain(layout, state.Viewport, chainId, view, options),
            FocusKind = CameraFocusKind.Chain,
            FocusId = chainId,
            LastChangeAt = now,
            CycleIndex = nextIndex,
        };
    }

    /// <summary>
    /// Manual pan/zoom takes control immediately and suspends automation.
    /// The camera stays here until <see cref="ResumeAuto"/> — a camera that
    /// yanks the view away while someone is reading is worse than no camera.
    /// </summary>
    public static CameraState ApplyManual(CameraState state, CameraViewport viewport, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(viewport);
        return state with { Manual = true, Viewport = viewport, LastChangeAt = now };
    }

    /// <summary>Explicit way back to auto: keeps the current view, restarts the dwell clock.</summary>
    public static CameraState ResumeAuto(CameraState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state with { Manual = false, LastChangeAt = now };
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
        var width = Math.Max(options.NodeWidth, maxX - minX + options.NodeWidth);
        var height = Math.Max(options.NodeHeight, maxY - minY + options.NodeHeight);
        var zoom = Math.Min(view.Width / width, view.Height / height);
        zoom = Math.Clamp(zoom, options.MinFitZoom, options.MaxFitZoom);
        return new CameraViewport
        {
            CenterX = (minX + maxX) / 2,
            CenterY = (minY + maxY) / 2,
            Zoom = zoom,
        };
    }

    private static CameraViewSize Validated(CameraViewSize? view) =>
        view is not null && view.Width > 0 && view.Height > 0
            ? view
            : CameraViewSize.Default;

    private static string? FindUrgent(FleetProjection projection, FleetMapOptions options)
    {
        foreach (var score in projection.Attention ?? [])
        {
            if (score is not null && score.Score >= options.UrgentAttentionThreshold)
            {
                return score.ItemId;
            }
        }
        return null;
    }

    private static List<string> CycleCandidates(FleetProjection projection, FleetMapOptions options)
    {
        var result = new List<string>();
        foreach (var chain in projection.ChainAttention ?? [])
        {
            if (chain is null || chain.Score <= 0)
            {
                continue;
            }
            result.Add(chain.ChainId);
            if (result.Count >= Math.Max(1, options.CycleTopChains))
            {
                break;
            }
        }
        return result;
    }

    private static CameraViewport FocusItem(
        FleetMapLayout layout, CameraViewport fallback, string itemId, FleetMapOptions options)
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
        return fallback;
    }

    private static CameraViewport FocusChain(
        FleetMapLayout layout, CameraViewport fallback, string chainId,
        CameraViewSize view, FleetMapOptions options)
    {
        var nodes = layout.Nodes is null
            ? []
            : layout.Nodes.Values
                .Where(n => string.Equals(n.ChainId, chainId, StringComparison.Ordinal))
                .ToList();
        if (nodes.Count == 0)
        {
            return fallback;
        }
        if (nodes.Count == 1)
        {
            return new CameraViewport
            {
                CenterX = nodes[0].X,
                CenterY = nodes[0].Y,
                Zoom = Math.Min(options.ItemFocusZoom, options.MaxFitZoom),
            };
        }
        return FitViewport(
            nodes.Min(n => n.X), nodes.Min(n => n.Y),
            nodes.Max(n => n.X), nodes.Max(n => n.Y),
            view, options);
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
        return fit with { Zoom = Math.Min(fit.Zoom, options.OverviewZoom) };
    }
}

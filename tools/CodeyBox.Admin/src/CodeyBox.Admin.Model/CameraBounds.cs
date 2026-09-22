namespace CodeyBox.Admin.Model;

/// <summary>The extent of what is on the map, in map units.</summary>
public sealed record MapBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public double Width => MaxX - MinX;

    public double Height => MaxY - MinY;
}

/// <summary>
/// Where the camera may go: the content's extent plus a margin. Panning past
/// the outermost node into nothing leaves an operator wondering whether
/// there is more; bounding the view makes the edge of the world
/// discoverable. Pure geometry over the layout — the layout never reads the
/// camera, so bounding the camera can never move a node.
/// </summary>
public static class CameraBounds
{
    /// <summary>The content extent: every node plus the margin (a screen-fraction of the view, at least a node).</summary>
    public static MapBounds Of(FleetMapLayout layout, CameraViewSize view, double zoom, FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new FleetMapOptions();
        var nodes = layout.Nodes?.Values ?? [];
        if (!nodes.Any())
        {
            return new MapBounds(-options.ColumnGap, -options.RowGap, options.ColumnGap, options.RowGap);
        }
        var z = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        var marginX = Math.Max(options.NodeWidth, view.Width * options.PanMarginFraction / z);
        var marginY = Math.Max(options.NodeHeight, view.Height * options.PanMarginFraction / z);
        return new MapBounds(
            nodes.Min(n => n.X) - options.NodeWidth / 2 - marginX,
            nodes.Min(n => n.Y) - options.NodeHeight / 2 - marginY,
            nodes.Max(n => n.X) + options.NodeWidth / 2 + marginX,
            nodes.Max(n => n.Y) + options.NodeHeight / 2 + marginY);
    }

    /// <summary>
    /// The smallest zoom worth having: the whole board fits the view (with its
    /// margin), however large the board — the zoom-out-to-orient loop needs
    /// the entire extent reachable. Never above <see cref="FleetMapOptions.MinFitZoom"/>
    /// (a small board still zooms out to dots) nor below the hard floor.
    /// </summary>
    public static double MinZoom(FleetMapLayout layout, CameraViewSize view, FleetMapOptions? options = null)
    {
        options ??= new FleetMapOptions();
        var nodes = layout.Nodes?.Values ?? [];
        var rawW = nodes.Any() ? nodes.Max(n => n.X) - nodes.Min(n => n.X) + options.NodeWidth : options.ColumnGap * 2;
        var rawH = nodes.Any() ? nodes.Max(n => n.Y) - nodes.Min(n => n.Y) + options.NodeHeight : options.RowGap * 2;
        // The margin is a fraction of the view at every zoom, so the zoom at
        // which content plus margins exactly fills the view is
        // view·(1 − 2f) / content — and at that zoom Clamp centres the board.
        var usable = Math.Max(0.1, 1 - 2 * Math.Clamp(options.PanMarginFraction, 0, 0.45));
        var fit = Math.Min(view.Width * usable / Math.Max(1, rawW), view.Height * usable / Math.Max(1, rawH));
        return Math.Max(Math.Max(1e-4, options.WholeBoardMinZoom), Math.Min(fit, options.MinFitZoom));
    }

    /// <summary>
    /// The viewport, kept inside the bounds: the visible rectangle never
    /// leaves the content extent (when the content is narrower than the view
    /// at this zoom, it is centred on that axis). Zoom is raised to the
    /// minimum when below it.
    /// </summary>
    public static CameraViewport Clamp(CameraViewport viewport, FleetMapLayout layout, CameraViewSize view, FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new FleetMapOptions();
        var zoom = Math.Max(viewport.Zoom, MinZoom(layout, view, options));
        var b = Of(layout, view, zoom, options);
        var halfW = view.Width / 2 / zoom;
        var halfH = view.Height / 2 / zoom;
        var cx = halfW * 2 >= b.Width ? (b.MinX + b.MaxX) / 2 : Math.Clamp(viewport.CenterX, b.MinX + halfW, b.MaxX - halfW);
        var cy = halfH * 2 >= b.Height ? (b.MinY + b.MaxY) / 2 : Math.Clamp(viewport.CenterY, b.MinY + halfH, b.MaxY - halfH);
        return new CameraViewport { CenterX = cx, CenterY = cy, Zoom = zoom };
    }
}

namespace CodeyBox.Admin.Model;

/// <summary>How one dependency edge should be drawn between its two nodes.</summary>
public sealed record EdgeRoute
{
    public required string From { get; init; }

    public required string To { get; init; }

    /// <summary>
    /// Vertical offset of the curve's apex from the higher endpoint, in map
    /// units; negative lifts the edge into the gap above the row. Zero is a
    /// plain horizontal-tangent curve — the default when nothing is in the way.
    /// </summary>
    public double Bend { get; init; }

    /// <summary>Ids of the nodes a straight run would have passed through — why it bends.</summary>
    public IReadOnlyList<string> Obstacles { get; init; } = [];
}

/// <summary>
/// Routes dependency edges around what stands between their endpoints. A
/// plain curve from a blocker to its dependent runs straight through any
/// node that sits between them on the same row — and then looks as if it
/// ended there. On a hand-drawn diagram the line would arc over the box in
/// the way; this does the same: when the run is obstructed, the edge lifts
/// into the gap above the row (or over the whole lane when the obstacle is
/// on a different row of the same lane), so a crossing is a deliberate arc,
/// never a graze. Pure geometry over the layout.
/// </summary>
public static class EdgeRouter
{
    public static IReadOnlyList<EdgeRoute> Route(
        FleetMapLayout layout,
        IEnumerable<(string From, string To)> edges,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new FleetMapOptions();
        var nodes = layout.Nodes ?? new Dictionary<string, MapNodeLayout>(StringComparer.Ordinal);
        var byLane = nodes.Values.GroupBy(n => n.LaneId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var routes = new List<EdgeRoute>();
        var halfW = options.NodeWidth / 2;
        var halfH = options.NodeHeight / 2;
        foreach (var (from, to) in edges ?? [])
        {
            if (from is null || to is null || !nodes.TryGetValue(from, out var a) || !nodes.TryGetValue(to, out var b))
            {
                continue;
            }
            var left = Math.Min(a.X, b.X) + halfW;
            var right = Math.Max(a.X, b.X) - halfW;
            var obstacles = new List<string>();
            var otherRow = false;
            if (right > left && byLane.TryGetValue(a.LaneId, out var lane))
            {
                foreach (var n in lane)
                {
                    if (ReferenceEquals(n, a) || ReferenceEquals(n, b) || n.X - halfW >= right || n.X + halfW <= left)
                    {
                        continue;
                    }
                    // The straight run interpolates y between the endpoints across the x span.
                    var t = Math.Clamp((n.X - a.X) / (b.X - a.X), 0, 1);
                    var yAt = a.Y + (b.Y - a.Y) * t;
                    if (Math.Abs(n.Y - yAt) < halfH * 2 * 0.8)
                    {
                        obstacles.Add(n.ItemId);
                        if (Math.Abs(n.Y - a.Y) > 1e-6 || Math.Abs(n.Y - b.Y) > 1e-6)
                        {
                            otherRow = true;
                        }
                    }
                }
            }
            double bend = 0;
            if (obstacles.Count > 0)
            {
                // Same row: lift into the gap just above it. Anything more
                // tangled: clear the lane by arcing over its top edge.
                bend = otherRow
                    ? (LaneTop(layout, a.LaneId) - halfH - options.ChainLaneGap * 0.4) - Math.Min(a.Y, b.Y)
                    : -(options.RowGap - options.NodeHeight) * 0.5 - halfH;
            }
            routes.Add(new EdgeRoute
            {
                From = from,
                To = to,
                Bend = Math.Round(bend, 2),
                Obstacles = obstacles.OrderBy(o => o, StringComparer.Ordinal).ToList(),
            });
        }
        return routes;
    }

    private static double LaneTop(FleetMapLayout layout, string laneId)
    {
        foreach (var lane in layout.Lanes ?? [])
        {
            if (string.Equals(lane.ChainId, laneId, StringComparison.Ordinal))
            {
                return lane.Y;
            }
        }
        return 0;
    }
}

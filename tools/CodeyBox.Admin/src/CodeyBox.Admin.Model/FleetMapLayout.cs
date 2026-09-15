namespace CodeyBox.Admin.Model;

/// <summary>A point on the fleet map, in map units (1 unit is one pixel at working zoom).</summary>
public sealed record MapPoint(double X, double Y);

/// <summary>Laid-out position of one work item.</summary>
public sealed record MapNodeLayout
{
    public required string ItemId { get; init; }

    public required string ChainId { get; init; }

    public required double X { get; init; }

    public required double Y { get; init; }

    /// <summary>Dependency depth: longest in-snapshot dep path from a root, clamped.</summary>
    public required int Depth { get; init; }
}

/// <summary>One chain's horizontal lane: origin Y plus the member set it was laid out for.</summary>
public sealed record MapChainLane
{
    public required string ChainId { get; init; }

    public required double Y { get; init; }

    public required double Height { get; init; }

    public IReadOnlyList<string> MemberIds { get; init; } = [];
}

/// <summary>
/// Positions for every mapped item, plus the lane table that keeps them
/// stable across refreshes.
/// </summary>
public sealed record FleetMapLayout
{
    public IReadOnlyDictionary<string, MapNodeLayout> Nodes { get; init; } =
        new Dictionary<string, MapNodeLayout>(StringComparer.Ordinal);

    public IReadOnlyList<MapChainLane> Lanes { get; init; } = [];
}

/// <summary>
/// Lays chains out so dependency direction reads left-to-right and edges
/// rarely cross: each chain gets a horizontal lane, each dependency depth a
/// column, rows within a column sort by id. Positions are sticky across
/// refreshes — <see cref="Update"/> keeps every surviving node where it was
/// and only places new or re-homed nodes, because a map that reshuffles on
/// every poll cannot be read. Pure: no I/O, no clock.
/// </summary>
public static class FleetMapBuilder
{
    private const int MaxDepsPerItem = 64;

    /// <summary>
    /// Deterministic first layout: chains stack in the order given (the
    /// caller passes <see cref="ChainGrouping"/> order), members flow by
    /// (depth, id). The same inputs always produce the same layout.
    /// </summary>
    public static FleetMapLayout DeriveInitial(
        IReadOnlyList<AdminWorkItem> items,
        IReadOnlyList<WorkChain> chains,
        FleetMapOptions? options = null)
    {
        options ??= new FleetMapOptions();
        var byId = IndexItems(items);
        var depths = ComputeDepths(byId, null, options);
        var lanes = new List<MapChainLane>();
        var nodes = new Dictionary<string, MapNodeLayout>(StringComparer.Ordinal);
        var laneY = 0.0;
        foreach (var chain in OrderedChains(chains, byId))
        {
            var height = LayoutChain(chain, byId, depths, laneY, null, nodes, options);
            lanes.Add(new MapChainLane
            {
                ChainId = chain.Id,
                Y = laneY,
                Height = height,
                MemberIds = chain.ItemIds,
            });
            laneY += height + options.ChainLaneGap;
        }
        return new FleetMapLayout { Nodes = nodes, Lanes = lanes };
    }

    /// <summary>
    /// Refreshes <paramref name="previous"/> for a new snapshot. Surviving
    /// members keep their exact positions when their chain lane and depth are
    /// unchanged; chains are re-matched to lanes by member overlap so a chain
    /// that gained or lost one item stays in its lane; brand-new chains append
    /// lanes at the bottom. Lanes never move up (no compaction on removal) and
    /// shift down only when a lane's own growth would otherwise overlap the
    /// lane below.
    /// </summary>
    public static FleetMapLayout Update(
        FleetMapLayout previous,
        IReadOnlyList<AdminWorkItem> items,
        IReadOnlyList<WorkChain> chains,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(previous);
        options ??= new FleetMapOptions();
        var byId = IndexItems(items);
        var previousDepths = previous.Nodes.ToDictionary(
            kv => kv.Key, kv => kv.Value.Depth, StringComparer.Ordinal);
        var depths = ComputeDepths(byId, previousDepths, options);

        var ordered = OrderedChains(chains, byId);
        var laneForChain = MatchLanes(ordered, previous.Lanes);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, MapNodeLayout>(StringComparer.Ordinal);
        var lanes = new List<MapChainLane>(ordered.Count);

        // Lanes stack in previous-Y order (stable), new lanes append at the end.
        var laneOrder = ordered
            .Select((chain, index) => (chain, index))
            .OrderBy(t => LaneSortKey(t.chain, laneForChain, previous.Lanes))
            .ThenBy(t => t.index)
            .ToList();

        var cursorY = 0.0;
        foreach (var (chain, _) in laneOrder)
        {
            double laneY;
            if (laneForChain.TryGetValue(chain.Id, out var prevLane)
                && claimed.Add(prevLane.ChainId))
            {
                laneY = Math.Max(prevLane.Y, cursorY);
            }
            else
            {
                laneY = cursorY;
            }
            var height = LayoutChain(chain, byId, depths, laneY, previous.Nodes, nodes, options);
            lanes.Add(new MapChainLane
            {
                ChainId = chain.Id,
                Y = laneY,
                Height = height,
                MemberIds = chain.ItemIds,
            });
            cursorY = laneY + height + options.ChainLaneGap;
        }

        // Keep lane list in chain order for a stable frame payload.
        lanes.Sort((a, b) => string.Compare(a.ChainId, b.ChainId, StringComparison.Ordinal));
        return new FleetMapLayout { Nodes = nodes, Lanes = lanes };
    }

    private static double LaneSortKey(
        WorkChain chain,
        Dictionary<string, MapChainLane> laneForChain,
        IReadOnlyList<MapChainLane> previousLanes)
    {
        if (laneForChain.TryGetValue(chain.Id, out var prev))
        {
            return prev.Y;
        }
        var maxY = double.MinValue;
        foreach (var lane in previousLanes)
        {
            maxY = Math.Max(maxY, lane.Y);
        }
        // New lanes sort after every previous lane. Ties between new lanes
        // break by chain order at the call site (ThenBy index), which is
        // deterministic — no hash codes, which .NET randomizes per process.
        return previousLanes.Count == 0 ? 0 : maxY + 1;
    }

    private static IReadOnlyList<WorkChain> OrderedChains(
        IReadOnlyList<WorkChain> chains,
        Dictionary<string, AdminWorkItem> byId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<WorkChain>();
        if (chains is not null)
        {
            foreach (var chain in chains)
            {
                if (chain is null || chain.Id is null || !seen.Add(chain.Id))
                {
                    continue;
                }
                var members = (chain.ItemIds ?? [])
                    .Where(id => id is not null && byId.ContainsKey(id))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                if (members.Count == 0)
                {
                    continue;
                }
                result.Add(new WorkChain
                {
                    Id = chain.Id,
                    ItemIds = members,
                    SeriesPrefix = chain.SeriesPrefix,
                });
                if (result.Count >= FleetSnapshot.MaxItems)
                {
                    break;
                }
            }
        }
        var chained = new HashSet<string>(result.SelectMany(c => c.ItemIds), StringComparer.Ordinal);
        foreach (var id in byId.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (chained.Add(id))
            {
                result.Add(new WorkChain { Id = "chain-" + id, ItemIds = [id] });
            }
        }
        return result;
    }

    private static Dictionary<string, MapChainLane> MatchLanes(
        IReadOnlyList<WorkChain> chains,
        IReadOnlyList<MapChainLane> previousLanes)
    {
        var result = new Dictionary<string, MapChainLane>(StringComparer.Ordinal);
        if (previousLanes is null || previousLanes.Count == 0)
        {
            return result;
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chain in chains)
        {
            MapChainLane? best = null;
            var bestOverlap = 0;
            foreach (var lane in previousLanes)
            {
                if (lane is null || used.Contains(lane.ChainId))
                {
                    continue;
                }
                var overlap = CountOverlap(chain.ItemIds, lane.MemberIds);
                if (overlap > bestOverlap
                    || (overlap == bestOverlap && best is not null
                        && string.Compare(lane.ChainId, best.ChainId, StringComparison.Ordinal) < 0))
                {
                    best = lane;
                    bestOverlap = overlap;
                }
            }
            if (best is not null && (bestOverlap > 0 || string.Equals(best.ChainId, chain.Id, StringComparison.Ordinal)))
            {
                used.Add(best.ChainId);
                result[chain.Id] = best;
            }
        }
        return result;
    }

    private static int CountOverlap(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left is null || right is null || left.Count == 0 || right.Count == 0)
        {
            return 0;
        }
        var small = left.Count <= right.Count ? left : right;
        var large = ReferenceEquals(small, left) ? right : left;
        var set = new HashSet<string>(large, StringComparer.Ordinal);
        var count = 0;
        foreach (var id in small)
        {
            if (id is not null && set.Contains(id))
            {
                count++;
            }
        }
        return count;
    }

    private static double LayoutChain(
        WorkChain chain,
        Dictionary<string, AdminWorkItem> byId,
        Dictionary<string, int> depths,
        double laneY,
        IReadOnlyDictionary<string, MapNodeLayout>? sticky,
        Dictionary<string, MapNodeLayout> nodes,
        FleetMapOptions options)
    {
        var columns = new Dictionary<int, List<string>>();
        var maxDepth = 0;
        foreach (var id in chain.ItemIds)
        {
            if (!byId.ContainsKey(id))
            {
                continue;
            }
            var depth = Math.Clamp(
                depths.TryGetValue(id, out var d) ? d : 0, 0, options.MaxDepthColumns - 1);
            maxDepth = Math.Max(maxDepth, depth);
            if (!columns.TryGetValue(depth, out var column))
            {
                column = [];
                columns[depth] = column;
            }
            column.Add(id);
        }

        var rowsUsed = 0;
        foreach (var (depth, column) in columns)
        {
            column.Sort(StringComparer.Ordinal);
            var stickyRows = new HashSet<int>();
            foreach (var id in column)
            {
                if (sticky is not null
                    && sticky.TryGetValue(id, out var prev)
                    && string.Equals(prev.ChainId, chain.Id, StringComparison.Ordinal)
                    && prev.Depth == depth)
                {
                    var row = (int)Math.Round((prev.Y - laneY) / options.RowGap);
                    if (row >= 0)
                    {
                        stickyRows.Add(row);
                    }
                }
            }
            var nextRow = 0;
            foreach (var id in column)
            {
                if (sticky is not null
                    && sticky.TryGetValue(id, out var prev)
                    && string.Equals(prev.ChainId, chain.Id, StringComparison.Ordinal)
                    && prev.Depth == depth)
                {
                    nodes[id] = prev;
                    var keptRow = (int)Math.Round((prev.Y - laneY) / options.RowGap);
                    rowsUsed = Math.Max(rowsUsed, keptRow + 1);
                    continue;
                }
                while (stickyRows.Contains(nextRow))
                {
                    nextRow++;
                }
                stickyRows.Add(nextRow);
                nodes[id] = new MapNodeLayout
                {
                    ItemId = id,
                    ChainId = chain.Id,
                    X = depth * options.ColumnGap,
                    Y = laneY + (nextRow * options.RowGap),
                    Depth = depth,
                };
                rowsUsed = Math.Max(rowsUsed, nextRow + 1);
                nextRow++;
            }
        }
        return Math.Max(options.NodeHeight, rowsUsed * options.RowGap);
    }

    private static Dictionary<string, AdminWorkItem> IndexItems(IReadOnlyList<AdminWorkItem> items)
    {
        var byId = new Dictionary<string, AdminWorkItem>(StringComparer.Ordinal);
        if (items is null)
        {
            return byId;
        }
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrEmpty(item.Id) || byId.ContainsKey(item.Id))
            {
                continue;
            }
            byId[item.Id] = item;
            if (byId.Count >= FleetSnapshot.MaxItems)
            {
                break;
            }
        }
        return byId;
    }

    private static Dictionary<string, int> ComputeDepths(
        Dictionary<string, AdminWorkItem> byId,
        Dictionary<string, int>? previousDepths,
        FleetMapOptions options)
    {
        var cap = Math.Max(1, options.MaxDepthColumns);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        if (previousDepths is not null)
        {
            foreach (var (id, depth) in previousDepths)
            {
                if (byId.ContainsKey(id))
                {
                    depths[id] = Math.Clamp(depth, 0, cap - 1);
                }
            }
        }
        foreach (var id in byId.Keys)
        {
            depths.TryAdd(id, 0);
        }
        for (var pass = 0; pass < cap; pass++)
        {
            var changed = false;
            foreach (var (id, item) in byId)
            {
                var deps = item.DependsOn;
                if (deps is null || deps.Count == 0)
                {
                    continue;
                }
                var best = depths[id];
                var examined = 0;
                foreach (var dep in deps)
                {
                    if (dep is null || !byId.TryGetValue(dep, out _) || !depths.TryGetValue(dep, out var depDepth))
                    {
                        continue;
                    }
                    if (++examined > MaxDepsPerItem)
                    {
                        break;
                    }
                    best = Math.Max(best, Math.Min(cap - 1, depDepth + 1));
                }
                if (best != depths[id])
                {
                    depths[id] = best;
                    changed = true;
                }
            }
            if (!changed)
            {
                break;
            }
        }
        return depths;
    }
}

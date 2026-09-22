namespace CodeyBox.Admin.Model;

/// <summary>A point on the fleet map, in map units (1 unit is one pixel at working zoom).</summary>
public sealed record MapPoint(double X, double Y);

/// <summary>Laid-out position of one work item.</summary>
public sealed record MapNodeLayout
{
    public required string ItemId { get; init; }

    /// <summary>Chain the item belongs to in the current snapshot.</summary>
    public required string ChainId { get; init; }

    /// <summary>Lane the item is drawn in. Survives chain splits: when a hub
    /// lands and its dependents become separate chains, they keep their lane.</summary>
    public string LaneId { get; init; } = string.Empty;

    /// <summary>Time position: past by finish time, now at zero, future by predicted rank (pushed right of any blocker).</summary>
    public required double X { get; init; }

    public required double Y { get; init; }

    /// <summary>Dependency depth: longest in-snapshot dep path from a root, clamped.</summary>
    public required int Depth { get; init; }

    /// <summary>Row inside the lane (sticky across refreshes).</summary>
    public int Slot { get; init; }

    /// <summary>Which part of the axis this position is: observed past, observed now, or predicted future.</summary>
    public AxisZone Zone { get; init; } = AxisZone.Now;

    /// <summary>Predicted batch for future items; null otherwise.</summary>
    public int? Batch { get; init; }

    /// <summary>True when the time position had to yield to a dependency (pushed right of a blocker).</summary>
    public bool PushedByDependency { get; init; }
}

/// <summary>One lane: a horizontal band holding one or more chains.</summary>
public sealed record MapChainLane
{
    /// <summary>Lane identity (the id of the chain that first claimed it).</summary>
    public required string ChainId { get; init; }

    /// <summary>Chains currently drawn in this lane.</summary>
    public IReadOnlyList<string> ChainIds { get; init; } = [];

    public required double Y { get; init; }

    public required double Height { get; init; }

    /// <summary>Right edge of the widest column, in map units.</summary>
    public double Width { get; init; }

    public IReadOnlyList<string> MemberIds { get; init; } = [];
}

/// <summary>
/// Positions for every mapped item, the lane table that keeps rows stable
/// across refreshes, and the axis the positions are on.
/// </summary>
public sealed record FleetMapLayout
{
    public IReadOnlyDictionary<string, MapNodeLayout> Nodes { get; init; } =
        new Dictionary<string, MapNodeLayout>(StringComparer.Ordinal);

    public IReadOnlyList<MapChainLane> Lanes { get; init; } = [];

    /// <summary>The bucketed "now" the axis is anchored to.</summary>
    public DateTimeOffset NowBucket { get; init; }

    /// <summary>Ruler markers for the renderer.</summary>
    public IReadOnlyList<AxisTick> Ticks { get; init; } = [];

    /// <summary>Predicted batches in the forecast (columns to the right of now).</summary>
    public int FutureBatches { get; init; }

    /// <summary>The faithful runs of the past, newest first (what a position between two landings means).</summary>
    public IReadOnlyList<AxisStretch> Stretches { get; init; } = [];

    /// <summary>Quiet stretches cut from the past, each marked with what it skipped.</summary>
    public IReadOnlyList<AxisBreak> Breaks { get; init; } = [];
}

/// <summary>
/// Lays the fleet out on a time axis. X is time: settled work sits where it
/// finished (warped once by what the past contains — breaks where nothing
/// landed, a card's width between landings that crowd a lane — never by the
/// camera), running and stuck work sits at now, queued work sits to the
/// right by its predicted batch. Dependency is
/// the hard constraint and time the objective within it: an item is placed at
/// its time position unless that would put it left of something it waits
/// on, in which case it is pushed right of the blocker and flagged. Lanes
/// group by chain (and by release); within a lane, rows are packed so boxes
/// never overlap horizontally, and a node keeps its row across refreshes
/// whenever the row is free. Observed positions move only when the axis
/// bucket steps; predicted positions move when the forecast changes —
/// which is the forecast being honest, not the map fidgeting. Pure: no I/O;
/// the caller supplies now.
/// </summary>
public static class FleetMapBuilder
{
    private const int MaxDepsPerItem = 64;

    /// <summary>Lane id for the packed lane of dependency-free items.</summary>
    public const string LooseLaneId = "loose";

    /// <summary>
    /// Deterministic first layout. <paramref name="now"/> anchors the axis;
    /// when omitted the latest timestamp in the snapshot stands in, which
    /// keeps tests and offline projections reproducible.
    /// </summary>
    public static FleetMapLayout DeriveInitial(
        IReadOnlyList<AdminWorkItem> items,
        IReadOnlyList<WorkChain> chains,
        FleetMapOptions? options = null,
        DateTimeOffset? now = null,
        IReadOnlyDictionary<string, ItemActivity>? activities = null,
        int capacity = 0) =>
        Build(null, items, chains, options ?? new FleetMapOptions(), now, activities, capacity);

    /// <summary>
    /// Refreshes <paramref name="previous"/> for a new snapshot. Lanes keep
    /// their order and never move up; rows are sticky where free; a chain that
    /// splits when its hub lands keeps every survivor in its lane.
    /// </summary>
    public static FleetMapLayout Update(
        FleetMapLayout previous,
        IReadOnlyList<AdminWorkItem> items,
        IReadOnlyList<WorkChain> chains,
        FleetMapOptions? options = null,
        DateTimeOffset? now = null,
        IReadOnlyDictionary<string, ItemActivity>? activities = null,
        int capacity = 0)
    {
        ArgumentNullException.ThrowIfNull(previous);
        return Build(previous, items, chains, options ?? new FleetMapOptions(), now, activities, capacity);
    }

    /// <summary>Where a new item depending on <paramref name="parentId"/> would land. Null when the parent is not on the map.</summary>
    public static MapPoint? PreviewDependent(
        FleetMapLayout layout,
        IReadOnlyList<AdminWorkItem> items,
        string parentId,
        FleetMapOptions? options = null)
    {
        var slots = PreviewDependents(layout, items, parentId, 1, options);
        return slots.Count > 0 ? slots[0] : null;
    }

    /// <summary>
    /// Where <paramref name="count"/> new items depending on
    /// <paramref name="parentId"/> would land, in order — the slots suggestion
    /// ghosts occupy, and the one after them for the "+ dependent" affordance.
    /// The layout is refreshed with ghost members and their positions read
    /// back, so the preview is exact. Empty when the parent is not on the map.
    /// </summary>
    public static IReadOnlyList<MapPoint> PreviewDependents(
        FleetMapLayout layout,
        IReadOnlyList<AdminWorkItem> items,
        string parentId,
        int count,
        FleetMapOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (string.IsNullOrEmpty(parentId) || !layout.Nodes.ContainsKey(parentId) || count <= 0)
        {
            return [];
        }
        // '~' sorts after every id character the orchestrator emits, so a
        // ghost never becomes a chain's smallest member and never renames one.
        var parent = (items ?? []).FirstOrDefault(i => i is not null && string.Equals(i.Id, parentId, StringComparison.Ordinal));
        var ghosts = new List<AdminWorkItem>(count);
        for (var i = 0; i < Math.Min(count, 64); i++)
        {
            ghosts.Add(new AdminWorkItem
            {
                Id = $"~ghost{i:D2}",
                State = "Queued",
                Agent = parent?.Agent ?? string.Empty,
                CreatedAt = parent?.CreatedAt ?? default,
                UpdatedAt = parent?.UpdatedAt ?? default,
                QueuePosition = long.MaxValue - 64 + i,
                DependsOn = [parentId],
                DependsOnSatisfied = false,
                ReleaseId = parent?.ReleaseId,
            });
        }
        var withGhosts = (items ?? []).Where(i => i is not null).Concat(ghosts).ToList();
        var chains = ChainGrouping.BuildChains(withGhosts);
        var preview = Build(layout, withGhosts, chains, options ?? new FleetMapOptions(), layout.NowBucket, null, 0);
        var result = new List<MapPoint>(ghosts.Count);
        foreach (var ghost in ghosts)
        {
            if (preview.Nodes.TryGetValue(ghost.Id, out var node))
            {
                result.Add(new MapPoint(node.X, node.Y));
            }
        }
        return result;
    }

    /// <summary>Loose-lane id for a release (or the unreleased tail).</summary>
    public static string LooseLaneFor(string? releaseId) =>
        string.IsNullOrEmpty(releaseId) ? LooseLaneId : LooseLaneId + ":" + releaseId;

    /// <summary>True for any loose lane (unreleased or per-release).</summary>
    public static bool IsLooseLane(string laneId) =>
        string.Equals(laneId, LooseLaneId, StringComparison.Ordinal) || laneId.StartsWith(LooseLaneId + ":", StringComparison.Ordinal);

    // ── the layout ───────────────────────────────────────────────────────

    private static FleetMapLayout Build(
        FleetMapLayout? previous,
        IReadOnlyList<AdminWorkItem> items,
        IReadOnlyList<WorkChain> chains,
        FleetMapOptions options,
        DateTimeOffset? now,
        IReadOnlyDictionary<string, ItemActivity>? activities,
        int capacity)
    {
        var byId = IndexItems(items);
        var anchor = now ?? previous?.NowBucket ?? (byId.Count == 0 ? DateTimeOffset.UnixEpoch : byId.Values.Max(i => i.UpdatedAt > i.CreatedAt ? i.UpdatedAt : i.CreatedAt));
        var nowBucket = TimeAxisScale.Bucket(anchor, options);
        var depths = ComputeDepths(byId, previous?.Nodes.ToDictionary(kv => kv.Key, kv => kv.Value.Depth, StringComparer.Ordinal), options);
        var forecast = QueueForecast.Rank(byId.Values.ToList(), activities, capacity > 0 ? capacity : options.DefaultCapacity);

        // 1. Lanes first: the past warp spaces landings per lane.
        var ordered = OrderedChains(chains, byId);
        var laneOf = AssignLanes(ordered, byId, previous);
        var laneOrder = LaneOrder(laneOf, ordered, byId, previous);
        var laneOfItem = new Dictionary<string, string>(StringComparer.Ordinal);
        var chainOfItem = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (laneId, group) in laneOf)
        {
            foreach (var chain in group)
            {
                foreach (var id in chain.ItemIds)
                {
                    laneOfItem.TryAdd(id, laneId);
                    chainOfItem.TryAdd(id, chain.Id);
                }
            }
        }

        // 2. Time positions: the past warped by its contents, the future by rank.
        var settled = byId.Values
            .Where(i => TerminalVisibility.IsSettled(i.State))
            .Select(i => new SettledLanding(i.Id, laneOfItem.GetValueOrDefault(i.Id, string.Empty), i.UpdatedAt))
            .ToList();
        var past = TimeAxisScale.WarpPast(settled, nowBucket, options);
        var x = new Dictionary<string, double>(StringComparer.Ordinal);
        var zone = new Dictionary<string, AxisZone>(StringComparer.Ordinal);
        var pushed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in byId.Values)
        {
            // Settled work is history. A failure awaiting a decision is a present
            // fact and sits at now beside the running work, whenever it happened.
            if (past.XByItem.TryGetValue(item.Id, out var px))
            {
                x[item.Id] = px;
                zone[item.Id] = AxisZone.Past;
            }
            else if (forecast.TryGetValue(item.Id, out var slot))
            {
                x[item.Id] = TimeAxisScale.FutureX(slot.Batch, options);
                zone[item.Id] = AxisZone.Future;
            }
            else
            {
                x[item.Id] = 0;
                zone[item.Id] = AxisZone.Now;
            }
        }
        // The dependency constraint, in depth order, for history too: a
        // dependent is never left of what it waited on.
        foreach (var item in byId.Values.OrderBy(i => depths[i.Id]).ThenBy(i => i.Id, StringComparer.Ordinal))
        {
            var min = double.MinValue;
            var examined = 0;
            foreach (var dep in item.DependsOn ?? [])
            {
                if (dep is null || !x.TryGetValue(dep, out var bx))
                {
                    continue;
                }
                if (++examined > MaxDepsPerItem)
                {
                    break;
                }
                // History keeps its true spacing wherever it already reads left to
                // right; only an out-of-order pair is pushed, and by a node's width,
                // not a full column — a landing is a fact, not a forecast.
                var minGap = zone[dep] == AxisZone.Past ? options.NodeWidth : options.ColumnGap;
                min = Math.Max(min, bx + minGap);
            }
            if (min > x[item.Id])
            {
                x[item.Id] = min;
                pushed.Add(item.Id);
            }
        }

        // 3. Rows per lane, packed by horizontal overlap, sticky where free.
        var previousLaneY = previous?.Lanes.ToDictionary(l => l.ChainId, l => l.Y, StringComparer.Ordinal)
            ?? new Dictionary<string, double>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, MapNodeLayout>(StringComparer.Ordinal);
        var lanes = new List<MapChainLane>(laneOrder.Count);
        var cursorY = 0.0;
        foreach (var laneId in laneOrder)
        {
            var group = laneOf[laneId];
            var hadLane = previousLaneY.TryGetValue(laneId, out var prevY);
            var laneY = hadLane ? Math.Max(prevY, cursorY) : cursorY;
            var members = group.SelectMany(c => c.ItemIds).Where(byId.ContainsKey).Distinct(StringComparer.Ordinal)
                .OrderBy(id => x[id]).ThenBy(id => id, StringComparer.Ordinal).ToList();
            var rowRight = new List<double>();
            var rows = 0;
            foreach (var id in members)
            {
                var left = x[id] - options.NodeWidth / 2;
                var right = x[id] + options.NodeWidth / 2 + options.NodeWidth * 0.2;
                var row = -1;
                MapNodeLayout? prev = null;
                if (previous is not null && previous.Nodes.TryGetValue(id, out var p) && string.Equals(p.LaneId, laneId, StringComparison.Ordinal))
                {
                    prev = p;
                }
                if (prev is not null && prev.Slot < rowRight.Count && rowRight[prev.Slot] <= left)
                {
                    row = prev.Slot; // the remembered row is free at this x: keep it
                }
                else if (prev is not null && prev.Slot >= rowRight.Count)
                {
                    while (rowRight.Count <= prev.Slot)
                    {
                        rowRight.Add(double.MinValue);
                    }
                    row = prev.Slot;
                }
                else
                {
                    for (var r = 0; r < rowRight.Count; r++)
                    {
                        if (rowRight[r] <= left)
                        {
                            row = r;
                            break;
                        }
                    }
                    if (row < 0)
                    {
                        rowRight.Add(double.MinValue);
                        row = rowRight.Count - 1;
                    }
                }
                rowRight[row] = Math.Max(rowRight[row], right);
                rows = Math.Max(rows, row + 1);
                nodes[id] = new MapNodeLayout
                {
                    ItemId = id,
                    ChainId = chainOfItem[id],
                    LaneId = laneId,
                    X = x[id],
                    Y = laneY + row * options.RowGap,
                    Depth = depths[id],
                    Slot = row,
                    Zone = zone[id],
                    Batch = forecast.TryGetValue(id, out var fs) ? fs.Batch : null,
                    PushedByDependency = pushed.Contains(id),
                };
            }
            var height = Math.Max(options.NodeHeight, rows * options.RowGap);
            var minX = members.Count == 0 ? 0 : members.Min(id => x[id]);
            var maxX = members.Count == 0 ? 0 : members.Max(id => x[id]);
            lanes.Add(new MapChainLane
            {
                ChainId = laneId,
                ChainIds = group.Select(c => c.Id).ToList(),
                Y = laneY,
                Height = height,
                Width = maxX - minX + options.NodeWidth,
                MemberIds = members.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            });
            cursorY = laneY + height + options.ChainLaneGap;
        }
        lanes.Sort((a, b) => string.Compare(a.ChainId, b.ChainId, StringComparison.Ordinal));

        var futureBatches = forecast.Count == 0 ? 0 : forecast.Values.Max(f => f.Batch) + 1;
        return new FleetMapLayout
        {
            Nodes = nodes,
            Lanes = lanes,
            NowBucket = nowBucket,
            Ticks = TimeAxisScale.Ticks(past, futureBatches, options),
            FutureBatches = futureBatches,
            Breaks = past.Breaks,
            Stretches = past.Stretches,
        };
    }

    /// <summary>Chain → lane: the lane most members lived in before; else a new lane (singletons pack into their release's loose lane).</summary>
    private static Dictionary<string, List<WorkChain>> AssignLanes(
        IReadOnlyList<WorkChain> ordered,
        Dictionary<string, AdminWorkItem> byId,
        FleetMapLayout? previous)
    {
        var previousLanes = new HashSet<string>(previous?.Lanes.Select(l => l.ChainId) ?? [], StringComparer.Ordinal);
        var groups = new Dictionary<string, List<WorkChain>>(StringComparer.Ordinal);
        foreach (var chain in ordered)
        {
            string? laneId = null;
            if (previous is not null && previous.Nodes.Count > 0)
            {
                var votes = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var id in chain.ItemIds)
                {
                    if (previous.Nodes.TryGetValue(id, out var node) && previousLanes.Contains(node.LaneId))
                    {
                        votes[node.LaneId] = votes.GetValueOrDefault(node.LaneId) + 1;
                    }
                }
                if (votes.Count > 0)
                {
                    laneId = votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
                }
                else if (previousLanes.Contains(chain.Id))
                {
                    laneId = chain.Id;
                }
            }
            // A chain keeps its lane whether live or landed — a settled chain
            // reads as the chain it was; only a singleton, which has no shape
            // to keep, packs into the loose lane.
            laneId ??= chain.ItemIds.Count == 1 ? LooseLaneFor(ReleaseOf(chain, byId)) : chain.Id;
            if (!groups.TryGetValue(laneId, out var group))
            {
                group = [];
                groups[laneId] = group;
            }
            group.Add(chain);
        }
        return groups;
    }

    /// <summary>
    /// Lane order: previous lanes keep their vertical order; a new lane joins
    /// its release's lanes (just below the last of them) so a release stays
    /// one contiguous frame; otherwise release groups first, then the rest.
    /// </summary>
    private static List<string> LaneOrder(
        Dictionary<string, List<WorkChain>> laneOf,
        IReadOnlyList<WorkChain> ordered,
        Dictionary<string, AdminWorkItem> byId,
        FleetMapLayout? previous)
    {
        var chainIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            chainIndex.TryAdd(ordered[i].Id, i);
        }
        var previousY = previous?.Lanes.ToDictionary(l => l.ChainId, l => l.Y, StringComparer.Ordinal)
            ?? new Dictionary<string, double>(StringComparer.Ordinal);
        var releaseOfLane = laneOf.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(c => ReleaseOf(c, byId)).FirstOrDefault(r => r is not null),
            StringComparer.Ordinal);

        var keys = new List<(string LaneId, double SortY, int Index)>();
        var releaseFirstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (laneId, group) in laneOf)
        {
            var index = group.Min(c => chainIndex.GetValueOrDefault(c.Id));
            var release = releaseOfLane[laneId];
            if (release is not null && !releaseFirstSeen.ContainsKey(release))
            {
                releaseFirstSeen[release] = index;
            }
            double sortY;
            if (previousY.TryGetValue(laneId, out var y))
            {
                sortY = y;
            }
            else if (previous is not null && previous.Lanes.Count > 0)
            {
                var siblings = release is null ? [] : previous.Lanes
                    .Where(l => l.MemberIds.Any(m => byId.TryGetValue(m, out var member) && string.Equals(member.ReleaseId, release, StringComparison.Ordinal)))
                    .Select(l => l.Y)
                    .ToList();
                sortY = siblings.Count > 0 ? siblings.Max() + 0.5 : previous.Lanes.Max(l => l.Y) + 1;
            }
            else
            {
                // First layout: releases before unreleased work, each release's lanes
                // together; wholly landed chains below the live ones, the most
                // recently landed nearest; the loose lanes last.
                var settledLane = group.All(c => c.ItemIds.All(id => byId.TryGetValue(id, out var m) && TerminalVisibility.IsSettled(m.State)));
                var loose = IsLooseLane(laneId);
                var recency = settledLane && !loose
                    ? group.SelectMany(c => c.ItemIds).Select(id => byId.TryGetValue(id, out var m) ? m.UpdatedAt : DateTimeOffset.MinValue).DefaultIfEmpty(DateTimeOffset.MinValue).Max()
                    : DateTimeOffset.MaxValue;
                var tier = loose ? 20_000_000.0 : settledLane ? 10_000_000.0 + Math.Min(9_000_000, (DateTimeOffset.MaxValue - recency).TotalDays) : 0.0;
                // A release's lanes stay contiguous; the live/landed/loose order applies within it.
                var basis = release is null ? 5_000_000_000.0 : releaseFirstSeen[release] * 100_000_000.0;
                sortY = basis + tier + index;
            }
            keys.Add((laneId, sortY, index));
        }
        return keys.OrderBy(k => k.SortY).ThenBy(k => k.Index).ThenBy(k => k.LaneId, StringComparer.Ordinal).Select(k => k.LaneId).ToList();
    }

    /// <summary>The release most of a chain's members carry; null when unreleased.</summary>
    private static string? ReleaseOf(WorkChain chain, Dictionary<string, AdminWorkItem> byId) =>
        chain.ItemIds
            .Select(id => byId.TryGetValue(id, out var item) ? item.ReleaseId : null)
            .Where(r => !string.IsNullOrEmpty(r))
            .GroupBy(r => r, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();

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
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                if (members.Count == 0)
                {
                    continue;
                }
                result.Add(new WorkChain { Id = chain.Id, ItemIds = members, SeriesPrefix = chain.SeriesPrefix });
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

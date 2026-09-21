namespace CodeyBox.Admin.Model;

/// <summary>Where on the axis an item sits, and why.</summary>
public enum AxisZone
{
    /// <summary>Observed: it finished, and sits at when it finished.</summary>
    Past,
    /// <summary>Observed: it is happening (or stuck) now.</summary>
    Now,
    /// <summary>Predicted: it has not started; its position is an ordering, not a time.</summary>
    Future,
}

/// <summary>An axis marker the renderer draws so the warp is legible.</summary>
public sealed record AxisTick
{
    public required double X { get; init; }

    public required string Label { get; init; }

    public required AxisZone Zone { get; init; }

    /// <summary>The instant a past tick marks (null for now and future ticks), so the renderer can show a clock time.</summary>
    public DateTimeOffset? At { get; init; }
}

/// <summary>A stretch of quiet cut out of the past: nothing landed for <see cref="Skipped"/>.</summary>
public sealed record AxisBreak
{
    /// <summary>Left edge of the marker, in map units.</summary>
    public required double X { get; init; }

    public required double Width { get; init; }

    public required TimeSpan Skipped { get; init; }

    /// <summary>Duration text for the ruler, one unit, e.g. "16h" or "3w".</summary>
    public string Label => AgeText.Coarse(Skipped);

    /// <summary>Exact duration text, e.g. "15h 53m" or "3w 2d".</summary>
    public string Exact => AgeText.Short(Skipped);
}

/// <summary>Short elapsed-time text for markers and ticks.</summary>
public static class AgeText
{
    /// <summary>One unit, rounded: "45m", "16h", "3d", "2w". For a ruler that has no room for two.</summary>
    public static string Coarse(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }
        if (age.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))}m";
        }
        if (age.TotalDays < 1)
        {
            return $"{(int)Math.Round(age.TotalHours)}h";
        }
        if (age.TotalDays < 14)
        {
            return $"{(int)Math.Round(age.TotalDays)}d";
        }
        return $"{(int)Math.Round(age.TotalDays / 7)}w";
    }

    public static string Short(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }
        if (age.TotalMinutes < 1)
        {
            return "<1m";
        }
        if (age.TotalHours < 1)
        {
            return $"{(int)age.TotalMinutes}m";
        }
        if (age.TotalDays < 1)
        {
            return age.Minutes == 0 ? $"{(int)age.TotalHours}h" : $"{(int)age.TotalHours}h {age.Minutes}m";
        }
        if (age.TotalDays < 14)
        {
            return age.Hours == 0 ? $"{(int)age.TotalDays}d" : $"{(int)age.TotalDays}d {age.Hours}h";
        }
        var weeks = (int)(age.TotalDays / 7);
        var days = (int)age.TotalDays - weeks * 7;
        return days == 0 ? $"{weeks}w" : $"{weeks}w {days}d";
    }
}

/// <summary>A faithful run of the past axis: between <see cref="OldestX"/> and <see cref="NewestX"/>, x is time at the fixed rate.</summary>
public sealed record AxisStretch
{
    public required double OldestX { get; init; }

    public required double NewestX { get; init; }

    public required DateTimeOffset Oldest { get; init; }

    public required DateTimeOffset Newest { get; init; }
}

/// <summary>What an x position on the axis means: an instant, a cut, now, or a predicted batch.</summary>
public sealed record AxisReading
{
    public required AxisZone Zone { get; init; }

    /// <summary>The instant, for a position inside a faithful stretch of the past.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>The cut this position falls in, when it does — no instant is invented for it.</summary>
    public AxisBreak? Break { get; init; }

    /// <summary>The predicted batch, for a position in the forecast (0 = next).</summary>
    public int? Batch { get; init; }
}

/// <summary>What the past warp produced. Computed once per snapshot; the camera is not an input.</summary>
public sealed record PastAxis
{
    /// <summary>The faithful runs, newest first.</summary>
    public IReadOnlyList<AxisStretch> Stretches { get; init; } = [];

    /// <summary>X of every settled item.</summary>
    public IReadOnlyDictionary<string, double> XByItem { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Items whose spacing was inflated to a card's width because they landed within minutes of a lane-mate.</summary>
    public IReadOnlySet<string> Spaced { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyList<AxisBreak> Breaks { get; init; } = [];

    public IReadOnlyList<AxisTick> Ticks { get; init; } = [];
}

/// <summary>One settled item as the warp sees it.</summary>
public sealed record SettledLanding(string Id, string LaneId, DateTimeOffset FinishedAt);

/// <summary>
/// The horizontal axis: past on the left, now at zero, future on the right.
/// The past is warped by what it contains rather than by a curve, and it is
/// warped <em>once</em>: nothing here depends on the camera, so a landed
/// item keeps its world position for the life of the snapshot whatever the
/// zoom. Inside a burst, spacing is elapsed time at a fixed rate; a stretch
/// with no landing longer than <see cref="FleetMapOptions.QuietGapMinutes"/>
/// is cut out and replaced by a marked break of fixed width carrying the
/// skipped duration, so spacing never lies about time without saying so;
/// and two landings in one lane closer than a card's width are spaced out
/// to a card's width (older pushed left), so boxes in a lane never overlap
/// in the world. What is drawn <em>as</em> several boxes or as one counted
/// cluster is the renderer's call at draw time, from screen distance — the
/// same rule as a dot becoming a card. "Now" is quantised to a bucket; the
/// stretch between the latest landing and now follows the quiet rule, so
/// once the fleet has been quiet that long the past stops moving. The
/// future is positioned by predicted <em>rank</em>, never by a fabricated
/// timestamp. Pure.
/// </summary>
public static class TimeAxisScale
{
    /// <summary>The bucketed "now" the layout is anchored to.</summary>
    public static DateTimeOffset Bucket(DateTimeOffset now, FleetMapOptions options)
    {
        var minutes = Math.Max(1, options.NowBucketMinutes);
        var ticks = TimeSpan.FromMinutes(minutes).Ticks;
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % ticks), TimeSpan.Zero);
    }

    /// <summary>Map units per minute of faithful past time.</summary>
    public static double UnitsPerMinute(FleetMapOptions options) =>
        options.ColumnGap / Math.Max(1, options.PastMinutesPerColumn);

    /// <summary>Warps the settled work onto the past half of the axis.</summary>
    public static PastAxis WarpPast(IReadOnlyList<SettledLanding> settled, DateTimeOffset nowBucket, FleetMapOptions options)
    {
        var quiet = TimeSpan.FromMinutes(Math.Max(1, options.QuietGapMinutes));
        var breakWidth = Math.Max(options.NodeWidth * 0.5, options.BreakWidth);
        var rate = UnitsPerMinute(options);
        var minSpacing = Math.Max(options.NodeWidth, options.PastMinSpacing);

        var landings = (settled ?? []).Where(s => s is not null && !string.IsNullOrEmpty(s.Id))
            .GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.First())
            .Select(s => s with { FinishedAt = s.FinishedAt > nowBucket ? nowBucket : s.FinishedAt })
            .ToList();

        // 1. The axis, walked back from now over the distinct landing times:
        //    faithful inside a burst, cut when quiet.
        var xOfTime = new Dictionary<DateTimeOffset, double>();
        var breaks = new List<AxisBreak>();
        var ticks = new List<AxisTick>();
        var stretches = new List<AxisStretch>();
        var cursor = 0.0;
        var prev = nowBucket;
        DateTimeOffset? stretchNewest = null;
        double stretchNewestX = 0;
        foreach (var t in landings.Select(l => l.FinishedAt).Distinct().OrderByDescending(t => t))
        {
            var gap = prev - t;
            if (gap > quiet)
            {
                CloseStretch();
                var next = cursor - (options.NodeWidth + breakWidth);
                breaks.Add(new AxisBreak { X = next + options.NodeWidth / 2, Width = breakWidth, Skipped = gap });
                cursor = next;
                stretchNewest = t;
                stretchNewestX = cursor;
            }
            else
            {
                cursor -= gap.TotalMinutes * rate;
                if (stretchNewest is null)
                {
                    stretchNewest = t;
                    stretchNewestX = cursor;
                }
            }
            xOfTime[t] = cursor;
            prev = t;
        }
        CloseStretch();

        void CloseStretch()
        {
            if (stretchNewest is null)
            {
                return;
            }
            stretches.Add(new AxisStretch { OldestX = cursor, NewestX = stretchNewestX, Oldest = prev, Newest = stretchNewest.Value });
            ticks.Add(new AxisTick { X = stretchNewestX, Label = AgeText.Short(nowBucket - stretchNewest.Value) + " ago", Zone = AxisZone.Past, At = stretchNewest });
            if (prev != stretchNewest.Value && stretchNewestX - cursor >= options.ColumnGap * 0.5)
            {
                ticks.Add(new AxisTick { X = cursor, Label = AgeText.Short(nowBucket - prev) + " ago", Zone = AxisZone.Past, At = prev });
            }
            stretchNewest = null;
        }

        // 2. Per lane, newest first: a landing closer than a card's width to
        //    the one after it is pushed left to a card's width. Deterministic,
        //    cascading, and independent of anything but the landings.
        var xByItem = new Dictionary<string, double>(StringComparer.Ordinal);
        var spaced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in landings.GroupBy(l => l.LaneId ?? string.Empty, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            double? right = null;
            foreach (var l in lane.OrderByDescending(l => l.FinishedAt).ThenBy(l => l.Id, StringComparer.Ordinal))
            {
                var x = xOfTime[l.FinishedAt];
                if (right is not null && x > right.Value - minSpacing)
                {
                    x = right.Value - minSpacing;
                    spaced.Add(l.Id);
                }
                xByItem[l.Id] = x;
                right = x;
            }
        }
        return new PastAxis { XByItem = xByItem, Spaced = spaced, Breaks = breaks, Ticks = ticks, Stretches = stretches };
    }

    /// <summary>
    /// What a position on the axis means. Inside a faithful stretch it is an
    /// instant; between the latest landing and now, when that run was not
    /// cut, also an instant; inside a cut it is the cut — no timestamp is
    /// invented; at now it is now; right of now it is the predicted batch
    /// whose column is nearest. Pure.
    /// </summary>
    public static AxisReading Read(double x, FleetMapLayout layout, FleetMapOptions options)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var rate = UnitsPerMinute(options);
        if (!double.IsFinite(x))
        {
            return new AxisReading { Zone = AxisZone.Now };
        }
        if (Math.Abs(x) <= options.NodeWidth / 2)
        {
            return new AxisReading { Zone = AxisZone.Now, At = layout.NowBucket };
        }
        if (x > 0)
        {
            var best = 0;
            var bestD = double.MaxValue;
            for (var b = 0; b < Math.Max(1, layout.FutureBatches); b++)
            {
                var d = Math.Abs(FutureX(b, options) - x);
                if (d < bestD)
                {
                    bestD = d;
                    best = b;
                }
            }
            return new AxisReading { Zone = AxisZone.Future, Batch = best };
        }
        foreach (var cut in layout.Breaks ?? [])
        {
            if (x >= cut.X && x <= cut.X + cut.Width)
            {
                return new AxisReading { Zone = AxisZone.Past, Break = cut };
            }
        }
        foreach (var run in layout.Stretches ?? [])
        {
            if (x >= run.OldestX - options.NodeWidth / 2 && x <= run.NewestX + options.NodeWidth / 2)
            {
                var clamped = Math.Clamp(x, run.OldestX, run.NewestX);
                return new AxisReading { Zone = AxisZone.Past, At = run.Newest - TimeSpan.FromMinutes((run.NewestX - clamped) / rate) };
            }
        }
        // Between the newest stretch and now with no cut in between: faithful time back from now.
        var newest = (layout.Stretches ?? []).Select(r => r.NewestX).DefaultIfEmpty(double.NegativeInfinity).Max();
        var cutBetween = (layout.Breaks ?? []).Any(c => c.X >= newest && c.X + c.Width <= 0);
        if (!cutBetween && x > newest)
        {
            return new AxisReading { Zone = AxisZone.Past, At = layout.NowBucket - TimeSpan.FromMinutes(-x / rate) };
        }
        return new AxisReading { Zone = AxisZone.Past };
    }

    /// <summary>
    /// X for a predicted batch rank (0 = the next batch to start). The first
    /// <see cref="FleetMapOptions.FutureNearBatches"/> get a full column each;
    /// beyond that the future compresses logarithmically — the further out,
    /// the less certain, the less room. Always positive.
    /// </summary>
    public static double FutureX(int rank, FleetMapOptions options)
    {
        var b = Math.Max(0, rank);
        var near = Math.Max(1, options.FutureNearBatches);
        if (b < near)
        {
            return (b + 1) * options.ColumnGap;
        }
        var beyond = b - near + 1;
        return (near + Math.Max(0.1, options.FutureCompression) * Math.Log(1 + beyond)) * options.ColumnGap;
    }

    /// <summary>Markers for the ruler: the past's stretches, now, and the future's batch columns.</summary>
    public static IReadOnlyList<AxisTick> Ticks(PastAxis past, int futureBatches, FleetMapOptions options)
    {
        var ticks = new List<AxisTick>(past?.Ticks ?? []);
        ticks.Add(new AxisTick { X = 0, Label = "now", Zone = AxisZone.Now });
        var near = Math.Max(1, options.FutureNearBatches);
        for (var b = 0; b < Math.Min(Math.Max(0, futureBatches), 256); b++)
        {
            // Every near batch is marked; beyond, marks thin out as the scale compresses.
            var beyond = b - near;
            var mark = b < near || beyond % 5 == 4 || b == futureBatches - 1;
            if (mark)
            {
                ticks.Add(new AxisTick { X = FutureX(b, options), Label = b == 0 ? "next" : $"+{b}", Zone = AxisZone.Future });
            }
        }
        return ticks;
    }
}

/// <summary>A queued item's predicted place in the order of work.</summary>
public sealed record ForecastSlot
{
    public required string ItemId { get; init; }

    /// <summary>0 = nothing in flight or queued stands before it; k = it waits on something in wave k−1.</summary>
    public required int Wave { get; init; }

    /// <summary>Batch index across the whole forecast: the column it is drawn in.</summary>
    public required int Batch { get; init; }

    /// <summary>True when its agent is paused, benched or out of quota — it holds a place but no one can start it.</summary>
    public bool Unschedulable { get; init; }
}

/// <summary>
/// The honest forecast: a topological order of the queued items over the
/// dependency graph, cut into batches by the fleet's concurrency cap.
/// Wave 0 is dispatchable now (running items occupy the first slots), wave k
/// waits on wave k−1; within a wave, queue position then creation time
/// decide, and items whose agent cannot run go last. That is an ordering
/// with a capacity count — nothing more precise is knowable from the
/// surfaces, so nothing more precise is claimed. Pure.
/// </summary>
public static class QueueForecast
{
    public static IReadOnlyDictionary<string, ForecastSlot> Rank(
        IReadOnlyList<AdminWorkItem> items,
        IReadOnlyDictionary<string, ItemActivity>? activities,
        int capacity,
        int runningNow)
    {
        var byId = new Dictionary<string, AdminWorkItem>(StringComparer.Ordinal);
        foreach (var item in items ?? [])
        {
            if (item is not null && !string.IsNullOrEmpty(item.Id))
            {
                byId.TryAdd(item.Id, item);
            }
        }
        var cap = Math.Max(1, capacity);
        var queued = byId.Values.Where(i => ItemStates.IsQueued(i.State)).ToList();
        var wave = new Dictionary<string, int>(StringComparer.Ordinal);

        int WaveOf(string id, int guard)
        {
            if (wave.TryGetValue(id, out var w))
            {
                return w;
            }
            if (guard > 64 || !byId.TryGetValue(id, out var item))
            {
                return 0;
            }
            wave[id] = 0; // cycle guard
            var result = 0;
            foreach (var dep in item.DependsOn ?? [])
            {
                if (dep is null || !byId.TryGetValue(dep, out var blocker))
                {
                    continue;
                }
                if (ItemStates.IsQueued(blocker.State))
                {
                    result = Math.Max(result, WaveOf(dep, guard + 1) + 1);
                }
                else if (!ItemStates.IsTerminal(blocker.State))
                {
                    result = Math.Max(result, 1); // running (or parked): it must finish first
                }
                else if (ItemStates.IsFailedTerminal(blocker.State) || string.Equals(blocker.State, "Cancelled", StringComparison.Ordinal))
                {
                    result = Math.Max(result, 1); // a dead end: it cannot start until someone acts
                }
            }
            wave[id] = result;
            return result;
        }

        foreach (var item in queued)
        {
            WaveOf(item.Id, 0);
        }

        var slots = new Dictionary<string, ForecastSlot>(StringComparer.Ordinal);
        var batch = 0;
        var freeInBatch = Math.Max(0, cap - Math.Max(0, runningNow));
        foreach (var group in queued.GroupBy(i => wave[i.Id]).OrderBy(g => g.Key))
        {
            if (group.Key > 0)
            {
                // A new wave never shares a batch with the wave before it.
                if (freeInBatch < cap)
                {
                    batch++;
                }
                freeInBatch = cap;
            }
            var ordered = group
                .OrderBy(i => IsUnschedulable(i, activities) ? 1 : 0)
                .ThenBy(i => i.QueuePosition)
                .ThenBy(i => i.CreatedAt)
                .ThenBy(i => i.Id, StringComparer.Ordinal);
            foreach (var item in ordered)
            {
                if (freeInBatch == 0)
                {
                    batch++;
                    freeInBatch = cap;
                }
                freeInBatch--;
                slots[item.Id] = new ForecastSlot
                {
                    ItemId = item.Id,
                    Wave = group.Key,
                    Batch = batch,
                    Unschedulable = IsUnschedulable(item, activities),
                };
            }
        }
        return slots;
    }

    private static bool IsUnschedulable(AdminWorkItem item, IReadOnlyDictionary<string, ItemActivity>? activities) =>
        activities is not null && activities.TryGetValue(item.Id, out var a) && a.Kind == ActivityKind.BlockedByAgentAvailability;
}

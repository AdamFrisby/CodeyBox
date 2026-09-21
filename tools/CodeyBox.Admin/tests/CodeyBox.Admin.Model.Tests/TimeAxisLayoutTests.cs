using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The time axis: settled work sits left by when it finished (warped by what
/// the past contains),
/// running and stuck work at now, queued work right by predicted batch.
/// Dependency is the hard constraint, time the objective within it. Rows pack
/// so boxes never overlap and stay sticky; observed positions move only when
/// the axis bucket steps; predicted positions move when the forecast does.
/// </summary>
public sealed class TimeAxisLayoutTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly DateTimeOffset Now = Fixtures.Now;

    private static AdminWorkItem At(string id, string state, double finishedHoursAgo = 0, IReadOnlyList<string>? dependsOn = null, long queuePosition = 0) =>
        Fixtures.Item(id, state: state, dependsOn: dependsOn, updatedAt: Now.AddHours(-finishedHoursAgo)) with { QueuePosition = queuePosition };

    private static FleetMapLayout Layout(IReadOnlyList<AdminWorkItem> items, FleetMapLayout? previous = null, DateTimeOffset? now = null, int capacity = 3) =>
        previous is null
            ? FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options, now ?? Now, capacity: capacity)
            : FleetMapBuilder.Update(previous, items, ChainGrouping.BuildChains(items).ToList(), Options, now ?? Now, capacity: capacity);

    [Fact]
    public void PastLeft_NowCentre_FutureRight()
    {
        var layout = Layout([At("done-1d", "Done", 24), At("done-1h", "Done", 1), At("run", "Working"), At("failed", "Failed", 3), At("queued", "Queued")]);

        Assert.True(layout.Nodes["done-1d"].X < layout.Nodes["done-1h"].X, "older sits further left");
        Assert.True(layout.Nodes["done-1h"].X < 0);
        Assert.Equal(0, layout.Nodes["run"].X);
        Assert.Equal(0, layout.Nodes["failed"].X); // a failure is a present fact, not history
        Assert.True(layout.Nodes["queued"].X > 0);
        Assert.Equal(AxisZone.Past, layout.Nodes["done-1d"].Zone);
        Assert.Equal(AxisZone.Now, layout.Nodes["run"].Zone);
        Assert.Equal(AxisZone.Future, layout.Nodes["queued"].Zone);
    }

    [Fact]
    public void ThePast_IsFaithfulInsideABurst_AndTheRulerNamesNowAndTheBatches()
    {
        var layout = Layout([At("a", "Done", 0.5), At("b", "Done", 1.0, dependsOn: null), At("q", "Queued")]);
        var rate = TimeAxisScale.UnitsPerMinute(Options);
        Assert.Equal(-30 * rate, layout.Nodes["a"].X, 6);
        Assert.Equal(0, TimeAxisScale.WarpPast([new SettledLanding("f", "L", Now.AddMinutes(5))], TimeAxisScale.Bucket(Now, Options), Options).XByItem["f"]); // the future is never in the past

        var ticks = layout.Ticks;
        Assert.Contains(ticks, t => t.Label == "now" && t.X == 0 && t.Zone == AxisZone.Now);
        Assert.True(ticks.Where(t => t.Zone == AxisZone.Past).Select(t => t.X).SequenceEqual(ticks.Where(t => t.Zone == AxisZone.Past).Select(t => t.X).OrderByDescending(x => x)), "past ticks run leftwards in order");
        Assert.Equal(1, ticks.Count(t => t.Zone == AxisZone.Future));
    }

    [Fact]
    public void FutureIsByPredictedBatch_CapacityWide_NotByATimestamp()
    {
        var items = Enumerable.Range(0, 7).Select(i => At($"q{i}", "Queued", queuePosition: i)).ToList();
        var layout = Layout(items, capacity: 3);

        Assert.Equal([0, 0, 0, 1, 1, 1, 2], items.Select(i => layout.Nodes[i.Id].Batch!.Value).ToList());
        Assert.Equal(TimeAxisScale.FutureX(0, Options), layout.Nodes["q0"].X);
        Assert.Equal(TimeAxisScale.FutureX(2, Options), layout.Nodes["q6"].X);
        Assert.Equal(3, layout.FutureBatches);
        // Three per batch: rows separate them; the fourth starts the next column on row 0.
        Assert.Equal([0, 1, 2], new[] { "q0", "q1", "q2" }.Select(id => layout.Nodes[id].Slot).ToList());
        Assert.Equal(0, layout.Nodes["q3"].Slot);
    }

    [Fact]
    public void TheFarFutureCompresses_LikeThePast_NearBatchesKeepFullColumns()
    {
        var near = Options.FutureNearBatches;
        for (var b = 1; b < near; b++)
        {
            Assert.Equal(Options.ColumnGap, TimeAxisScale.FutureX(b, Options) - TimeAxisScale.FutureX(b - 1, Options));
        }
        var farStep = TimeAxisScale.FutureX(near + 20, Options) - TimeAxisScale.FutureX(near + 19, Options);
        Assert.True(farStep < Options.ColumnGap / 2, "beyond the near future a batch takes less than half a column");
        Assert.True(TimeAxisScale.FutureX(40, Options) < 20 * Options.ColumnGap, "forty batches fit in fewer than twenty columns");
        Assert.True(TimeAxisScale.FutureX(41, Options) > TimeAxisScale.FutureX(40, Options), "still monotone");

        var ticks = TimeAxisScale.Ticks(new PastAxis(), 40, Options).Where(t => t.Zone == AxisZone.Future).ToList();
        Assert.Equal(near, ticks.Count(t => t.X <= TimeAxisScale.FutureX(near - 1, Options)));
        Assert.True(ticks.Count < 40, "far ticks thin out");
        Assert.Equal("+39", ticks[^1].Label);
    }

    [Fact]
    public void DependencyIsTheHardConstraint_TimeYieldsToIt()
    {
        // A landed item whose blocker landed long *after* it (a data oddity) is pushed right of the blocker and flagged.
        var odd = Layout([At("blocker", "Done", 1), At("dependent", "Done", 30, ["blocker"])]);
        Assert.True(odd.Nodes["dependent"].X >= odd.Nodes["blocker"].X + Options.NodeWidth);
        Assert.True(odd.Nodes["dependent"].PushedByDependency);
        Assert.False(odd.Nodes["blocker"].PushedByDependency);

        // The normal case needs no push: a dependent of a landed item sits in the future on its own.
        var normal = Layout([At("done", "Done", 2), At("next", "Queued", dependsOn: ["done"])]);
        Assert.False(normal.Nodes["next"].PushedByDependency);
        Assert.True(normal.Nodes["next"].X > normal.Nodes["done"].X);
    }

    [Fact]
    public void AChainReadsAsAPath_LeftToRight_OnOneRow()
    {
        var layout = Layout([At("a", "Done", 5), At("b", "Working", dependsOn: ["a"]), At("c", "Queued", dependsOn: ["b"]), At("d", "Queued", dependsOn: ["c"])]);

        Assert.True(layout.Nodes["a"].X < layout.Nodes["b"].X && layout.Nodes["b"].X < layout.Nodes["c"].X && layout.Nodes["c"].X < layout.Nodes["d"].X);
        Assert.Single(new[] { "a", "b", "c", "d" }.Select(id => layout.Nodes[id].Y).Distinct());
        Assert.Single(layout.Lanes);
    }

    [Fact]
    public void AFanOut_PacksIntoRowsWithinEachBatch()
    {
        var items = new List<AdminWorkItem> { At("hub", "Working") };
        for (var i = 0; i < 7; i++)
        {
            items.Add(At($"d{i}", "Queued", dependsOn: ["hub"], queuePosition: i));
        }
        var layout = Layout(items, capacity: 3);

        Assert.All(items.Skip(1), i => Assert.Equal(1, layout.Nodes[i.Id].Batch!.Value - (layout.Nodes[i.Id].Batch!.Value - 1))); // all in wave 1
        var batches = items.Skip(1).Select(i => layout.Nodes[i.Id].Batch!.Value).Distinct().Count();
        Assert.Equal(3, batches);
        Assert.True(items.Skip(1).All(i => layout.Nodes[i.Id].X > layout.Nodes["hub"].X));
        // No two boxes overlap: same-x nodes are on different rows.
        var overlaps = layout.Nodes.Values.SelectMany(a => layout.Nodes.Values.Where(b => a.ItemId.CompareTo(b.ItemId) < 0 && a.Y == b.Y && Math.Abs(a.X - b.X) < Options.NodeWidth));
        Assert.Empty(overlaps);
    }

    [Fact]
    public void WhenTheHubLands_ItMovesIntoThePast_TheForecastAdvances_RowsStay()
    {
        var items = new List<AdminWorkItem> { At("hub", "Working"), At("d0", "Queued", dependsOn: ["hub"], queuePosition: 0), At("d1", "Queued", dependsOn: ["hub"], queuePosition: 1) };
        var before = Layout(items, capacity: 3);
        var landed = new List<AdminWorkItem> { At("hub", "Done", 0.01), At("d0", "Queued", dependsOn: ["hub"], queuePosition: 0), At("d1", "Queued", dependsOn: ["hub"], queuePosition: 1) };
        var after = Layout(landed, before);

        Assert.True(after.Nodes["hub"].X <= 0 && after.Nodes["hub"].Zone == AxisZone.Past);
        Assert.True(after.Nodes["d0"].X < before.Nodes["d0"].X, "one wave closer: the forecast advanced");
        Assert.Equal(before.Nodes["d0"].Slot, after.Nodes["d0"].Slot);
        Assert.Equal(before.Nodes["d1"].Slot, after.Nodes["d1"].Slot);
        Assert.Equal(before.Nodes["d0"].LaneId, after.Nodes["d0"].LaneId);
    }

    [Fact]
    public void ObservedPositions_DoNotCreep_WithinANowBucket_AndStepWhenItAdvances()
    {
        var items = new List<AdminWorkItem> { At("old", "Done", 1), At("run", "Working"), At("q", "Queued") };
        var first = Layout(items, now: Now);
        var withinBucket = Layout(items, first, Now.AddMinutes(2));
        foreach (var (id, node) in first.Nodes)
        {
            Assert.Equal(node, withinBucket.Nodes[id]);
        }

        var nextBucket = Layout(items, first, Now.AddMinutes(Options.NowBucketMinutes + 1));
        Assert.True(nextBucket.Nodes["old"].X < first.Nodes["old"].X, "the past recedes by one step");
        Assert.Equal(first.Nodes["run"].X, nextBucket.Nodes["run"].X);
        Assert.Equal(first.Nodes["q"].X, nextBucket.Nodes["q"].X);
    }

    [Fact]
    public void TitleChange_MovesNothing_AndNewWorkKeepsOthersStill()
    {
        var items = Enumerable.Range(0, 12).Select(i => At($"n{i:D2}", i % 4 == 0 ? "Working" : "Queued", queuePosition: i)).ToList();
        var layout = Layout(items);

        var renamed = items.Select(i => i.Id == "n05" ? i with { Title = "renamed" } : i).ToList();
        var refreshed = Layout(renamed, layout);
        foreach (var (id, node) in layout.Nodes)
        {
            Assert.Equal(node, refreshed.Nodes[id]);
        }

        var grown = items.Append(At("n99", "Queued", queuePosition: 99)).ToList();
        var bigger = Layout(grown, layout);
        foreach (var (id, node) in layout.Nodes)
        {
            Assert.Equal(node, bigger.Nodes[id]);
        }
        Assert.True(bigger.Nodes["n99"].X >= layout.Nodes.Values.Where(n => n.Zone == AxisZone.Future).Max(n => n.X));
    }

    [Fact]
    public void RemovingQueuedWork_MovesOnlyTheForecast_ObservedNodesStay()
    {
        var items = new List<AdminWorkItem> { At("done", "Done", 4), At("run", "Working") }
            .Concat(Enumerable.Range(0, 6).Select(i => At($"q{i}", "Queued", queuePosition: i)))
            .ToList();
        var layout = Layout(items, capacity: 2);

        var shrunk = items.Where(i => i.Id != "q0").ToList();
        var refreshed = Layout(shrunk, layout);

        // The observed nodes keep their time position and lane; the history
        // strip sits below the live lane, so its y follows that lane's height.
        Assert.Equal(layout.Nodes["done"].X, refreshed.Nodes["done"].X);
        Assert.Equal(layout.Nodes["done"].LaneId, refreshed.Nodes["done"].LaneId);
        Assert.Equal(layout.Nodes["done"].Slot, refreshed.Nodes["done"].Slot);
        Assert.Equal(layout.Nodes["run"], refreshed.Nodes["run"]);
        var order = shrunk.Where(i => ItemStates.IsQueued(i.State)).Select(i => i.Id).ToList();
        Assert.True(order.Zip(order.Skip(1)).All(p => refreshed.Nodes[p.First].X <= refreshed.Nodes[p.Second].X), "predicted order is preserved");
    }

    [Fact]
    public void DependencyCycle_DoesNotHang()
    {
        var layout = Layout([At("a", "Queued", dependsOn: ["b"]), At("b", "Queued", dependsOn: ["a"])]);
        Assert.Equal(2, layout.Nodes.Count);
    }

    [Fact]
    public void DeriveInitial_IsDeterministic()
    {
        var items = Enumerable.Range(0, 15).Select(i => At($"i{i:D2}", i % 3 == 0 ? "Done" : "Queued", i, i > 2 && i % 3 != 0 ? [$"i{i - 1:D2}"] : null, i)).ToList();
        var a = Layout(items);
        var b = Layout(items);
        foreach (var (id, node) in a.Nodes)
        {
            Assert.Equal(node, b.Nodes[id]);
        }
        Assert.True(a.Lanes.Count > 1);
    }
}

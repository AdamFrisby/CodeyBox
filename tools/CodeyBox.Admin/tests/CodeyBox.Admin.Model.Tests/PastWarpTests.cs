using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The past is warped once, from the landings alone: faithful inside a
/// burst, quiet cut and labelled, a card's width kept between landings that
/// crowd a lane. The camera is not an input.
/// </summary>
public sealed class PastWarpTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly DateTimeOffset Now = Fixtures.Now;
    private static readonly DateTimeOffset Bucket = TimeAxisScale.Bucket(Now, Options);

    private static SettledLanding L(string id, double hoursAgo, string lane = "A") => new(id, lane, Bucket.AddHours(-hoursAgo));

    private static double Rate => TimeAxisScale.UnitsPerMinute(Options);

    [Fact]
    public void InsideABurst_SpacingIsElapsedTime()
    {
        var past = TimeAxisScale.WarpPast([L("a", 0.5), L("b", 1.5, "B"), L("c", 2.0, "C")], Bucket, Options);
        Assert.Equal(-30 * Rate, past.XByItem["a"], 6);
        Assert.Equal(60 * Rate, past.XByItem["a"] - past.XByItem["b"], 6);
        Assert.Equal(30 * Rate, past.XByItem["b"] - past.XByItem["c"], 6);
        Assert.Empty(past.Breaks);
        Assert.Empty(past.Spaced);
    }

    [Fact]
    public void QuietStretches_AreCutAndLabelled_AtAFixedWidth()
    {
        var past = TimeAxisScale.WarpPast([L("n1", 1.0, "A"), L("n2", 1.5, "B"), L("o1", 26, "A"), L("o2", 26.25, "B")], Bucket, Options);
        var cut = Assert.Single(past.Breaks);
        Assert.Equal(TimeSpan.FromHours(24.5), cut.Skipped);
        Assert.Equal("1d", cut.Label);
        Assert.Equal("1d", cut.Exact);
        Assert.Equal(Math.Max(Options.NodeWidth * 0.5, Options.BreakWidth), cut.Width);
        Assert.True(cut.X + cut.Width <= past.XByItem["n2"] - Options.NodeWidth / 2 + 1e-6, "the break sits between the boxes, not under one");
        Assert.True(cut.X >= past.XByItem["o1"] + Options.NodeWidth / 2 - 1e-6);
        Assert.Equal(Options.NodeWidth + cut.Width, past.XByItem["n2"] - past.XByItem["o1"], 6);
        Assert.True(past.XByItem["o1"] > -24.5 * 60 * Rate, "a day of quiet is not a day of space");
        Assert.Contains(past.Ticks, t => t.At == Bucket.AddHours(-1) && t.Label == "1h ago");
        Assert.Contains(past.Ticks, t => t.At == Bucket.AddHours(-26));
        Assert.All(past.Ticks, t => Assert.Equal(AxisZone.Past, t.Zone));
    }

    [Fact]
    public void QuietSinceTheLastLanding_IsCutTheSameWay_SoTheSettledPastStopsMoving()
    {
        var landings = new List<SettledLanding> { L("a", 5), L("b", 5.5, "B") };
        var quiet = TimeAxisScale.WarpPast(landings, Bucket, Options);
        var later = TimeAxisScale.WarpPast(landings, Bucket.AddHours(3), Options);
        var cut = Assert.Single(quiet.Breaks);
        Assert.Equal(TimeSpan.FromHours(5), cut.Skipped);
        Assert.Equal(-(Options.NodeWidth + cut.Width), quiet.XByItem["a"], 6);
        Assert.Equal(quiet.XByItem["a"], later.XByItem["a"]);
        Assert.Equal(quiet.XByItem["b"], later.XByItem["b"]);
        Assert.Equal(TimeSpan.FromHours(8), Assert.Single(later.Breaks).Skipped);
    }

    [Fact]
    public void LandingsThatCrowdALane_AreSpacedACardApart_OlderPushedLeft_OtherLanesUntouched()
    {
        var past = TimeAxisScale.WarpPast([L("a", 1.0), L("b", 1.2), L("c", 1.3), L("other", 1.2, "B")], Bucket, Options);
        var spacing = Math.Max(Options.NodeWidth, Options.PastMinSpacing);
        Assert.Equal(-60 * Rate, past.XByItem["a"], 6); // the newest keeps its true position
        Assert.Equal(past.XByItem["a"] - spacing, past.XByItem["b"], 6);
        Assert.Equal(past.XByItem["b"] - spacing, past.XByItem["c"], 6);
        Assert.Equal(-72 * Rate, past.XByItem["other"], 6); // another lane: faithful
        Assert.Equal(["b", "c"], past.Spaced.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void SameLaneBoxes_NeverOverlap_AndTheWarpIsDeterministic()
    {
        var rng = new Random(7);
        var landings = Enumerable.Range(0, 60).Select(i => L($"i{i}", rng.NextDouble() * 72, lane: i % 3 == 0 ? "B" : "A")).ToList();
        var past = TimeAxisScale.WarpPast(landings, Bucket, Options);
        foreach (var lane in landings.GroupBy(l => l.LaneId))
        {
            var xs = lane.Select(l => past.XByItem[l.Id]).OrderBy(v => v).ToList();
            for (var i = 1; i < xs.Count; i++)
            {
                Assert.True(xs[i] - xs[i - 1] >= Options.NodeWidth - 1e-6, $"boxes {xs[i - 1]} and {xs[i]} overlap");
            }
        }
        var again = TimeAxisScale.WarpPast(landings.AsEnumerable().Reverse().ToList(), Bucket, Options);
        Assert.Equal(past.XByItem.OrderBy(kv => kv.Key).ToList(), again.XByItem.OrderBy(kv => kv.Key).ToList());
    }

    [Fact]
    public void WarpToleratesOddInput()
    {
        var past = TimeAxisScale.WarpPast([], Bucket, Options);
        Assert.Empty(past.XByItem);
        Assert.Empty(past.Breaks);
        var future = TimeAxisScale.WarpPast([L("f", -3), L("f", -3)], Bucket, Options); // finished "in the future", duplicated
        Assert.Equal(0, future.XByItem["f"]);
        Assert.Equal("<1m", AgeText.Short(TimeSpan.FromSeconds(10)));
        Assert.Equal("2w 3d", AgeText.Short(TimeSpan.FromDays(17)));
        Assert.Equal("16h", AgeText.Coarse(TimeSpan.FromMinutes(953)));
    }

    // ── through the layout ───────────────────────────────────────────────

    private static AdminWorkItem At(string id, string state, double hoursAgo = 0, IReadOnlyList<string>? dependsOn = null) =>
        Fixtures.Item(id, state: state, dependsOn: dependsOn, updatedAt: Now.AddHours(-hoursAgo));

    private static FleetMapLayout Layout(IReadOnlyList<AdminWorkItem> items) =>
        FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options, Now, capacity: 3);

    [Fact]
    public void Layout_CarriesSpacingAndBreaks_AndPushesHistoryByDependencyToo()
    {
        var layout = Layout([At("hub", "Done", 3), At("d1", "Done", 2.9, ["hub"]), At("d2", "Done", 1, ["hub"]), At("run", "Working", dependsOn: ["hub"]), At("old", "Done", 40)]);
        Assert.True(layout.Nodes["hub"].X < layout.Nodes["d1"].X && layout.Nodes["d1"].X < layout.Nodes["d2"].X);
        Assert.True(layout.Nodes["d1"].Spaced == false && layout.Nodes["hub"].Spaced, "the older of the crowded pair is the one moved");
        Assert.Equal(layout.Nodes["d1"].Slot, layout.Nodes["hub"].Slot); // spaced, so one row
        Assert.Single(layout.Breaks);
        Assert.Contains(layout.Ticks, t => t.Zone == AxisZone.Now);

        // A dependent cancelled long before its blocker landed sits right of the blocker and is flagged.
        var odd = Layout([At("blocker", "Done", 1), At("cancelled", "Cancelled", 30, ["blocker"])]);
        Assert.True(odd.Nodes["cancelled"].X >= odd.Nodes["blocker"].X + Options.ColumnGap);
        Assert.True(odd.Nodes["cancelled"].PushedByDependency);
    }
}

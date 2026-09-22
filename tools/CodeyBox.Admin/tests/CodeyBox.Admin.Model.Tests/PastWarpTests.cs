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
    public void InsideABurst_SpacingIsElapsedTime_AndLongerIdleIsCompressedNotCut()
    {
        var past = TimeAxisScale.WarpPast([L("a", 0.25), L("b", 0.5, "B"), L("c", 2.0, "C")], Bucket, Options);
        var sinceWidth = Options.NodeWidth + Math.Max(Options.NodeWidth * 0.5, Options.BreakWidth);
        Assert.Equal(-sinceWidth, past.XByItem["a"], 6); // the latest landing sits past the fixed "since" cut, whatever the clock says
        Assert.Equal(15 * Rate, past.XByItem["a"] - past.XByItem["b"], 6); // under τ: faithful
        var idle = 1.5 * 60;
        var compressed = TimeAxisScale.Compress(idle, Options);
        Assert.True(compressed < idle && compressed > Options.PastCompressAfterMinutes, "a 90-minute idle gap inside a run takes less axis than time, more than τ");
        Assert.Equal(compressed * Rate, past.XByItem["b"] - past.XByItem["c"], 6);
        var only = Assert.Single(past.Breaks); // under the quiet threshold: never a cut — only the "since" cut
        Assert.True(only.SinceLatest);
        Assert.Equal(TimeSpan.FromMinutes(15), only.Skipped);
    }

    [Fact]
    public void Compression_IsInvertible_AndFaithfulUpToTau()
    {
        foreach (var minutes in new[] { 0.0, 5, 29.9, 30, 31, 90, 600, 3000, 100_000 })
        {
            Assert.Equal(minutes, TimeAxisScale.Decompress(TimeAxisScale.Compress(minutes, Options), Options), 6);
        }
        Assert.Equal(20, TimeAxisScale.Compress(20, Options));
        Assert.True(TimeAxisScale.Compress(1440, Options) < 120, "a day of idle inside a run is under two axis hours");
    }

    [Fact]
    public void Cuts_AreBounded_TheLongestIdleStretchesWin_AndTheQuietSinceTheLastLandingAlwaysCounts()
    {
        // 40 bursts a few hours apart over ~10 days, one big hole in the middle, and 5h of quiet since the last.
        var landings = new List<SettledLanding>();
        var t = 5.0;
        for (var i = 0; i < 40; i++)
        {
            landings.Add(L($"b{i}", t, lane: "L" + i % 4));
            t += i == 20 ? 72 : 3 + i % 3; // one 3-day hole; otherwise 3–5h idle
        }
        var options = Options with { MaxCuts = 3 };
        var past = TimeAxisScale.WarpPast(landings, Bucket, options);

        Assert.Equal(4, past.Breaks.Count); // the gap since the last landing + the 3 longest idle stretches
        Assert.Contains(past.Breaks, b => b.Skipped == TimeSpan.FromHours(5));
        Assert.Contains(past.Breaks, b => b.Skipped == TimeSpan.FromHours(72));
        Assert.Equal(4, past.Stretches.Count);
        Assert.True(past.Stretches.All(r => r.Points.Count >= 1));
        // Every other idle stretch was compressed into its run, none cut.
        var noCap = TimeAxisScale.WarpPast(landings, Bucket, Options with { MaxCuts = 1000 });
        Assert.True(noCap.Breaks.Count > past.Breaks.Count);
        Assert.True(past.XByItem["b39"] > noCap.XByItem["b39"], "fewer cuts, less width");

        // Newest first, x descending, and every landing has a point in exactly one run.
        Assert.Equal(landings.Count, past.Stretches.Sum(r => r.Points.Count));
        foreach (var run in past.Stretches)
        {
            Assert.True(run.Points.Zip(run.Points.Skip(1)).All(p => p.First.X >= p.Second.X && p.First.At >= p.Second.At));
        }
    }

    [Fact]
    public void TheGapSinceTheLastLanding_IsAlwaysCut_SoTheClockMovesNothing()
    {
        var landings = new List<SettledLanding> { L("a", 5), L("b", 5.5, "B") };
        var quiet = TimeAxisScale.WarpPast(landings, Bucket, Options);
        var later = TimeAxisScale.WarpPast(landings, Bucket.AddHours(3), Options);
        var cut = Assert.Single(quiet.Breaks);
        Assert.True(cut.SinceLatest);
        Assert.Equal(TimeSpan.FromHours(5), cut.Skipped);
        Assert.Equal(Math.Max(Options.NodeWidth * 0.5, Options.BreakWidth), cut.Width);
        Assert.Equal(-(Options.NodeWidth + cut.Width), quiet.XByItem["a"], 6);
        Assert.Equal(quiet.XByItem["a"], later.XByItem["a"]);
        Assert.Equal(quiet.XByItem["b"], later.XByItem["b"]);
        Assert.Equal(TimeSpan.FromHours(8), Assert.Single(later.Breaks).Skipped);
        Assert.Contains(quiet.Ticks, t => t.At == Bucket.AddHours(-5) && t.Label == "5h ago");
    }

    [Fact]
    public void Breaks_SitBetweenBoxes_AndCarryWhatTheySkipped()
    {
        var past = TimeAxisScale.WarpPast([L("n1", 1.0, "A"), L("n2", 1.5, "B"), L("o1", 26, "A"), L("o2", 26.25, "B")], Bucket, Options);
        var cut = Assert.Single(past.Breaks, b => !b.SinceLatest);
        Assert.Equal(TimeSpan.FromHours(24.5), cut.Skipped);
        Assert.Equal("1d", cut.Label);
        Assert.True(cut.X + cut.Width <= past.XByItem["n2"] - Options.NodeWidth / 2 + 1e-6, "the break sits between the boxes, not under one");
        Assert.True(cut.X >= past.XByItem["o1"] + Options.NodeWidth / 2 - 1e-6);
        Assert.Equal(-(Options.NodeWidth + Math.Max(Options.NodeWidth * 0.5, Options.BreakWidth)), past.XByItem["n1"], 6); // past the fixed "since" cut
    }

    [Fact]
    public void TheWarpIsDeterministic_AndCrowdedLandingsAreNotInflated()
    {
        var rng = new Random(7);
        var landings = Enumerable.Range(0, 60).Select(i => L($"i{i}", rng.NextDouble() * 72, lane: i % 3 == 0 ? "B" : "A")).ToList();
        var past = TimeAxisScale.WarpPast(landings, Bucket, Options);
        var again = TimeAxisScale.WarpPast(landings.AsEnumerable().Reverse().ToList(), Bucket, Options);
        Assert.Equal(past.XByItem.OrderBy(kv => kv.Key).ToList(), again.XByItem.OrderBy(kv => kv.Key).ToList());
        Assert.True(past.Breaks.Count <= Options.MaxCuts + 1);
        Assert.Equal(1, past.Breaks.Count(b => b.SinceLatest));
        // Two landings a minute apart sit a minute apart: rows, not spacing, keep their boxes apart.
        var close = TimeAxisScale.WarpPast([L("p", 1.0), L("q", 1.0 + 1.0 / 60)], Bucket, Options);
        Assert.Equal(1 * Rate, close.XByItem["p"] - close.XByItem["q"], 6);
    }

    [Fact]
    public void WarpToleratesOddInput()
    {
        var past = TimeAxisScale.WarpPast([], Bucket, Options);
        Assert.Empty(past.XByItem);
        Assert.Empty(past.Breaks);
        var future = TimeAxisScale.WarpPast([L("f", -3), L("f", -3)], Bucket, Options); // finished "in the future", duplicated
        Assert.Equal(-(Options.NodeWidth + Math.Max(Options.NodeWidth * 0.5, Options.BreakWidth)), future.XByItem["f"]); // clamped to now, past the "since" cut
        Assert.Equal(TimeSpan.Zero, Assert.Single(future.Breaks).Skipped);
        Assert.All(TimeAxisScale.WarpPast([L("z", 40)], Bucket, Options with { MaxCuts = 0 }).Breaks, b => Assert.True(b.SinceLatest && b.Skipped == TimeSpan.FromHours(40)));
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
    public void Layout_CarriesBreaks_PacksCrowdedLandingsIntoRows_AndPushesHistoryByDependencyToo()
    {
        var layout = Layout([At("hub", "Done", 3), At("d1", "Done", 2.9, ["hub"]), At("d2", "Done", 1, ["hub"]), At("run", "Working", dependsOn: ["hub"]), At("old", "Done", 40)]);
        Assert.True(layout.Nodes["hub"].X < layout.Nodes["d1"].X && layout.Nodes["d1"].X < layout.Nodes["d2"].X);
        Assert.NotEqual(layout.Nodes["d1"].Slot, layout.Nodes["hub"].Slot); // six minutes apart: the boxes overlap, so rows keep them apart
        Assert.Equal(2, layout.Breaks.Count); // the "since" cut and the 37h hole before "old"
        Assert.Contains(layout.Ticks, t => t.Zone == AxisZone.Now);

        // A dependent cancelled long before its blocker landed sits right of the blocker and is flagged.
        var odd = Layout([At("blocker", "Done", 1), At("cancelled", "Cancelled", 30, ["blocker"])]);
        Assert.True(odd.Nodes["cancelled"].X >= odd.Nodes["blocker"].X + Options.NodeWidth);
        Assert.True(odd.Nodes["cancelled"].PushedByDependency);
    }
}

using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Zoom is a view transform and nothing else. The layout has no camera
/// input at all: a node's world position is a function of the fleet and
/// the axis bucket, identical across refreshes, and untouched by any camera
/// move. (The page-level version of this test, across the whole zoom range,
/// lives in the web tests.)
/// </summary>
public sealed class LayoutInvarianceTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly DateTimeOffset Now = Fixtures.Now;

    private static AdminWorkItem At(string id, string state, double hoursAgo = 0, IReadOnlyList<string>? dependsOn = null, long queuePosition = 0) =>
        Fixtures.Item(id, state: state, dependsOn: dependsOn, updatedAt: Now.AddHours(-hoursAgo)) with { QueuePosition = queuePosition };

    private static List<AdminWorkItem> Fleet()
    {
        var items = new List<AdminWorkItem>
        {
            At("old-a", "Done", 40), At("old-b", "Done", 41, ["old-a"]),
            At("hub", "Done", 3), At("h1", "Done", 2.9, ["hub"]), At("h2", "Done", 1, ["hub"]),
            At("run", "Working", dependsOn: ["hub"]), At("parked", "Parked", 5), At("failed", "Failed", 9),
        };
        for (var i = 0; i < 9; i++)
        {
            items.Add(At($"q{i}", "Queued", dependsOn: ["run"], queuePosition: i));
        }
        items.Add(At("loose", "Queued", queuePosition: 50));
        return items;
    }

    [Fact]
    public void RefreshesWithUnchangedInput_AreByteIdentical_AndNoCameraMoveTouchesTheLayout()
    {
        var items = Fleet();
        var chains = ChainGrouping.BuildChains(items).ToList();
        var baseline = FleetMapBuilder.DeriveInitial(items, chains, Options, Now, capacity: 3);
        var again = baseline;
        for (var i = 0; i < 5; i++)
        {
            again = FleetMapBuilder.Update(again, items, chains, Options, Now, capacity: 3);
            Assert.Equal(baseline.Nodes.OrderBy(kv => kv.Key).ToList(), again.Nodes.OrderBy(kv => kv.Key).ToList());
            Assert.Equal(baseline.Breaks, again.Breaks);
            Assert.Equal(baseline.Lanes.Select(l => l.ChainId), again.Lanes.Select(l => l.ChainId));
        }

        // The camera reads the layout; nothing reads the camera.
        var camera = CameraDirector.Initial(baseline, Now, null, Options);
        foreach (var zoom in new[] { 0.08, 0.13, 0.2, 0.33, 0.5, 0.8, 1.0, 1.6, 2.2, 3.0, 5.0 })
        {
            camera = CameraDirector.ApplyManual(camera, new CameraViewport { CenterX = 0, CenterY = 0, Zoom = zoom }, Now, baseline, Options);
        }
        camera = CameraDirector.OpenItem(camera, "run", baseline, Now, Options);
        camera = CameraDirector.Overview(camera, baseline, Now, null, Options);
        Assert.Equal(baseline.Nodes.OrderBy(kv => kv.Key).ToList(), FleetMapBuilder.Update(baseline, items, chains, Options, Now, capacity: 3).Nodes.OrderBy(kv => kv.Key).ToList());
    }
}

/// <summary>Months of history share a strip; the overview frames the live work when the whole map cannot fit.</summary>
public sealed class HistoryStripTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly DateTimeOffset Now = Fixtures.Now;

    private static AdminWorkItem At(string id, string state, double hoursAgo = 0, IReadOnlyList<string>? dependsOn = null) =>
        Fixtures.Item(id, state: state, dependsOn: dependsOn, updatedAt: Now.AddHours(-hoursAgo));

    [Fact]
    public void WhollyLandedChains_ShareTheHistoryStrip_LiveChainsKeepLanes()
    {
        var items = new List<AdminWorkItem>
        {
            At("a1", "Done", 100), At("a2", "Done", 99, ["a1"]),
            At("b1", "Done", 50), At("b2", "Cancelled", 49, ["b1"]),
            At("c1", "Done", 5), At("c2", "Working", dependsOn: ["c1"]),
        };
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options, Now);
        Assert.Equal(FleetMapBuilder.HistoryLaneId, layout.Nodes["a1"].LaneId);
        Assert.Equal(FleetMapBuilder.HistoryLaneId, layout.Nodes["b2"].LaneId);
        Assert.NotEqual(FleetMapBuilder.HistoryLaneId, layout.Nodes["c1"].LaneId);
        Assert.Equal(layout.Nodes["c1"].LaneId, layout.Nodes["c2"].LaneId);
        Assert.True(layout.Lanes.Single(l => l.ChainId == FleetMapBuilder.HistoryLaneId).Y > layout.Nodes["c1"].Y, "history sits below the live work");
        Assert.True(FleetMapBuilder.IsHistoryLane(FleetMapBuilder.HistoryLaneFor("rel-1")));

        // When the last live member lands, the chain moves into the strip: a data change, not a camera one.
        var landed = items.Select(i => i.Id == "c2" ? i with { State = "Done", UpdatedAt = Now } : i).ToList();
        var after = FleetMapBuilder.Update(layout, landed, ChainGrouping.BuildChains(landed).ToList(), Options, Now);
        Assert.Equal(FleetMapBuilder.HistoryLaneId, after.Nodes["c2"].LaneId);
    }

    [Fact]
    public void Overview_FramesTheLiveWork_WhenMonthsOfHistoryCannotFit()
    {
        var items = new List<AdminWorkItem> { At("run", "Working"), At("q", "Queued", dependsOn: ["run"]) };
        for (var d = 1; d <= 120; d++)
        {
            items.Add(At($"h{d}", "Done", d * 24 + 0.5));
        }
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options, Now);
        var view = new CameraViewSize(1400, 800);
        var camera = CameraDirector.Initial(layout, Now, view, Options);
        var halfW = view.Width / 2 / camera.Viewport.Zoom;
        Assert.True(camera.Viewport.CenterX - halfW <= 0 && camera.Viewport.CenterX + halfW >= layout.Nodes["q"].X, "now and the forecast are in the frame");
        Assert.True(camera.Viewport.Zoom > Options.MinFitZoom, "not squashed to the floor to fit history");
        Assert.True(layout.Nodes["h120"].X < camera.Viewport.CenterX - halfW, "deep history runs off to the left");
    }
}

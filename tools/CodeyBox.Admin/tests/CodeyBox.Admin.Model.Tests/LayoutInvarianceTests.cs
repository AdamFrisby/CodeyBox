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

/// <summary>Landed chains keep their shape; the overview reaches the whole board.</summary>
public sealed class LandedChainTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly DateTimeOffset Now = Fixtures.Now;

    private static AdminWorkItem At(string id, string state, double hoursAgo = 0, IReadOnlyList<string>? dependsOn = null) =>
        Fixtures.Item(id, state: state, dependsOn: dependsOn, updatedAt: Now.AddHours(-hoursAgo));

    [Fact]
    public void WhollyLandedChains_KeepTheirOwnLanes_BelowLiveWork_MostRecentFirst_SingletonsPack()
    {
        var items = new List<AdminWorkItem>
        {
            At("a1", "Done", 100), At("a2", "Done", 96, ["a1"]),
            At("b1", "Done", 50), At("b2", "Cancelled", 46, ["b1"]),
            At("c1", "Done", 5), At("c2", "Working", dependsOn: ["c1"]),
            At("lone", "Done", 70),
        };
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options, Now);
        Assert.Equal(layout.Nodes["a1"].LaneId, layout.Nodes["a2"].LaneId);
        Assert.NotEqual(layout.Nodes["a1"].LaneId, layout.Nodes["b1"].LaneId); // each landed chain its own lane
        Assert.Equal(layout.Nodes["a1"].Y, layout.Nodes["a2"].Y); // one row: the chain's shape
        Assert.True(layout.Nodes["c1"].Y < layout.Nodes["b1"].Y && layout.Nodes["b1"].Y < layout.Nodes["a1"].Y, "live first, then landed by recency");
        Assert.True(FleetMapBuilder.IsLooseLane(layout.Nodes["lone"].LaneId));
        Assert.True(layout.Nodes["lone"].Y > layout.Nodes["a1"].Y, "the loose lane last");

        // When the last live member lands, the chain keeps the lane it had.
        var landed = items.Select(i => i.Id == "c2" ? i with { State = "Done", UpdatedAt = Now } : i).ToList();
        var after = FleetMapBuilder.Update(layout, landed, ChainGrouping.BuildChains(landed).ToList(), Options, Now);
        Assert.Equal(layout.Nodes["c2"].LaneId, after.Nodes["c2"].LaneId);
        Assert.Equal(layout.Nodes["c2"].Y, after.Nodes["c2"].Y);
    }

    [Fact]
    public void Overview_ReachesTheWholeBoard_HoweverLarge()
    {
        var items = new List<AdminWorkItem> { At("run", "Working"), At("q", "Queued", dependsOn: ["run"]) };
        for (var d = 1; d <= 120; d++)
        {
            items.Add(At($"h{d}a", "Done", d * 24 + 0.5));
            items.Add(At($"h{d}b", "Done", d * 24 + 0.4, [$"h{d}a"]));
        }
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options, Now);
        var view = new CameraViewSize(1400, 800);
        var camera = CameraDirector.Initial(layout, Now, view, Options);
        var halfW = view.Width / 2 / camera.Viewport.Zoom;
        var halfH = view.Height / 2 / camera.Viewport.Zoom;
        Assert.True(camera.Viewport.Zoom < Options.MinFitZoom, "far below the legible floor: dots, not labels");
        Assert.True(camera.Viewport.Zoom >= Options.WholeBoardMinZoom);
        Assert.All(layout.Nodes.Values, n => Assert.True(n.X >= camera.Viewport.CenterX - halfW && n.X <= camera.Viewport.CenterX + halfW && n.Y >= camera.Viewport.CenterY - halfH && n.Y <= camera.Viewport.CenterY + halfH, "every node is in the frame"));
        Assert.True(CameraBounds.MinZoom(layout, view, Options) <= camera.Viewport.Zoom + 1e-9, "the pan clamp lets the operator reach it");
    }
}

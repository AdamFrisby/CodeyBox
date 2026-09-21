using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The camera as editorial layer: every move carries an eased travel time,
/// dwell scales with what is framed, an urgent item is held (and not flapped
/// away from), the idle lap ends on the overview, dormant chains are never a
/// stop, and semantic zoom opens the item under the centre.
/// </summary>
public sealed class CameraTravelTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly CameraViewSize View = new(1600, 900);

    private static (FleetProjection Projection, FleetMapLayout Layout) Build(IReadOnlyList<AdminWorkItem> items, DateTimeOffset now)
    {
        var snapshot = Fixtures.Snapshot(items) with { Now = now };
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(snapshot.Items.ToList(), projection.Chains.ToList(), Options);
        return (projection, layout);
    }

    [Fact]
    public void TravelTime_GrowsWithDistanceAndZoomChange_AndIsClamped()
    {
        var origin = new CameraViewport { CenterX = 0, CenterY = 0, Zoom = 1 };
        Assert.Equal(0, CameraDirector.TravelTime(origin, origin, Options));

        var near = CameraDirector.TravelTime(origin, origin with { CenterX = 100 }, Options);
        var far = CameraDirector.TravelTime(origin, origin with { CenterX = 900 }, Options);
        var zoomed = CameraDirector.TravelTime(origin, origin with { Zoom = 1.8 }, Options);
        var huge = CameraDirector.TravelTime(origin, origin with { CenterX = 1e6 }, Options);

        Assert.InRange(near, Options.TravelMinMs, Options.TravelMaxMs);
        Assert.True(far > near);
        Assert.True(zoomed > Options.TravelMinMs);
        Assert.Equal(Options.TravelMaxMs, huge);
        Assert.Equal(far, CameraDirector.TravelTime(origin, origin with { CenterX = 900 }, Options)); // deterministic
    }

    [Fact]
    public void EveryAutomaticMove_CarriesATravelTime()
    {
        var t0 = Fixtures.Now;
        var (projection, layout) = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Working"),
            Fixtures.Item("b2", state: "Queued", dependsOn: ["b1"]),
        ], t0);
        var camera = CameraDirector.Initial(layout, t0, View, Options);
        Assert.Equal(0, camera.TravelMs);

        camera = CameraDirector.Next(camera, projection, layout, t0, View, Options);
        Assert.Equal(CameraFocusKind.Chain, camera.FocusKind);
        Assert.True(camera.TravelMs >= Options.TravelMinMs);
        Assert.True(camera.DwellSeconds >= Options.DwellSeconds);
    }

    [Fact]
    public void Dwell_ScalesWithChainSize_UpToTheCap()
    {
        var t0 = Fixtures.Now;
        var small = Build([Fixtures.Item("s1", state: "Working"), Fixtures.Item("s2", state: "Queued", dependsOn: ["s1"])], t0);
        var members = new List<AdminWorkItem> { Fixtures.Item("big-0", state: "Working") };
        for (var i = 1; i < 40; i++)
        {
            members.Add(Fixtures.Item($"big-{i:D2}", dependsOn: ["big-0"]));
        }
        var big = Build(members, t0);

        var smallStop = CameraDirector.Next(CameraDirector.Initial(small.Layout, t0, View, Options), small.Projection, small.Layout, t0, View, Options);
        var bigStop = CameraDirector.Next(CameraDirector.Initial(big.Layout, t0, View, Options), big.Projection, big.Layout, t0, View, Options);

        Assert.True(bigStop.DwellSeconds > smallStop.DwellSeconds);
        Assert.Equal(Options.MaxDwellSeconds, bigStop.DwellSeconds);
    }

    [Fact]
    public void IdleLap_VisitsActiveChains_ThenRestsOnOverview()
    {
        var t0 = Fixtures.Now;
        var (projection, layout) = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Working"),
        ], t0);
        var camera = CameraDirector.Initial(layout, t0, View, Options);
        var seen = new List<CameraFocusKind>();
        var t = t0;
        for (var step = 0; step < 3; step++)
        {
            camera = CameraDirector.Next(camera, projection, layout, t, View, Options);
            seen.Add(camera.FocusKind);
            t = t.AddSeconds(camera.DwellSeconds + 1);
        }
        Assert.Equal([CameraFocusKind.Chain, CameraFocusKind.Chain, CameraFocusKind.All], seen);
        Assert.Equal(Options.OverviewDwellSeconds, camera.DwellSeconds);

        // …and the lap wraps back to the first chain.
        camera = CameraDirector.Next(camera, projection, layout, t, View, Options);
        Assert.Equal(CameraFocusKind.Chain, camera.FocusKind);
    }

    [Fact]
    public void DormantChain_IsNeverAStop()
    {
        var t0 = Fixtures.Now;
        // One chain running; one chain blocked end to end by a dependency outside the snapshot.
        var (projection, layout) = Build([
            Fixtures.Item("live", state: "Working"),
            Fixtures.Item("dorm-1", dependsOn: ["ghost"], dependsOnSatisfied: false),
            Fixtures.Item("dorm-2", dependsOn: ["dorm-1"]),
        ], t0);
        var dormantChain = projection.Chains.Single(c => c.ItemIds.Contains("dorm-1")).Id;

        var camera = CameraDirector.Initial(layout, t0, View, Options);
        var t = t0;
        for (var step = 0; step < 6; step++)
        {
            camera = CameraDirector.Next(camera, projection, layout, t, View, Options);
            Assert.NotEqual(dormantChain, camera.FocusId);
            t = t.AddSeconds(camera.DwellSeconds + 1);
        }
    }

    [Fact]
    public void UrgentItem_OpensAndHolds_WithReason_AndDoesNotFlap()
    {
        var t0 = Fixtures.Now;
        var broken = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Failed"),
        ], t0);
        var camera = CameraDirector.Initial(broken.Layout, t0, View, Options);
        camera = CameraDirector.Next(camera, broken.Projection, broken.Layout, t0, View, Options);

        Assert.Equal(CameraFocusKind.Item, camera.FocusKind);
        Assert.Equal("b1", camera.OpenItemId);
        Assert.Contains("Failed", camera.HoldReason);
        Assert.True(camera.Viewport.Zoom >= Options.OpenZoomEnd, "a focused item is fully open");

        // The failure clears two seconds later: the camera stays for the minimum hold.
        var healed = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Working"),
        ], t0.AddSeconds(2));
        var held = CameraDirector.Next(camera, healed.Projection, healed.Layout, t0.AddSeconds(2), View, Options);
        Assert.Equal("b1", held.FocusId);

        // After the hold it resumes the lap.
        var released = CameraDirector.Next(held, healed.Projection, healed.Layout, t0.AddSeconds(Options.ItemHoldMinSeconds + 1), View, Options);
        Assert.Equal(CameraFocusKind.Chain, released.FocusKind);
        Assert.Null(released.HoldReason);
        Assert.NotEqual("b1", released.FocusId);
    }

    [Fact]
    public void NewUrgentItem_PreemptsANonUrgentItemFocus_Immediately()
    {
        var t0 = Fixtures.Now;
        var (projection, layout) = Build([Fixtures.Item("a1", state: "Working"), Fixtures.Item("b1", state: "Working")], t0);
        // A singleton chain stop opens its item at item zoom — a non-urgent Item-level focus.
        var camera = CameraDirector.Next(CameraDirector.Initial(layout, t0, View, Options), projection, layout, t0, View, Options);
        Assert.Equal("a1", camera.OpenItemId);
        Assert.Null(camera.HoldReason);

        var broken = Build([Fixtures.Item("a1", state: "Working"), Fixtures.Item("b1", state: "Failed")], t0.AddSeconds(1));
        var next = CameraDirector.Next(camera, broken.Projection, broken.Layout, t0.AddSeconds(1), View, Options);

        Assert.Equal("b1", next.FocusId);
        Assert.NotNull(next.HoldReason);
    }

    [Fact]
    public void ChainStop_NeverOpensAMemberByAccident()
    {
        var t0 = Fixtures.Now;
        var (projection, layout) = Build([
            Fixtures.Item("c1", state: "Working"),
            Fixtures.Item("c2", state: "Queued", dependsOn: ["c1"]),
        ], t0);
        var camera = CameraDirector.Next(CameraDirector.Initial(layout, t0, View, Options), projection, layout, t0, View, Options);

        Assert.Equal(CameraFocusKind.Chain, camera.FocusKind);
        Assert.True(camera.Viewport.Zoom < Options.OpenZoomStart);
        Assert.Null(camera.OpenItemId);
    }

    [Fact]
    public void OpenItem_IsManual_TravelsAndOpens_CloseItemPullsBack()
    {
        var t0 = Fixtures.Now;
        var (projection, layout) = Build([Fixtures.Item("a1", state: "Working"), Fixtures.Item("a2", state: "Working")], t0);
        var camera = CameraDirector.Initial(layout, t0, View, Options);

        var opened = CameraDirector.OpenItem(camera, "a2", layout, t0, Options);
        Assert.True(opened.Manual);
        Assert.Equal("a2", opened.OpenItemId);
        Assert.Equal(layout.Nodes["a2"].X, opened.Viewport.CenterX);
        Assert.True(opened.TravelMs > 0);
        Assert.Equal(opened, CameraDirector.Next(opened, projection, layout, t0.AddMinutes(5), View, Options)); // manual holds

        var closed = CameraDirector.CloseItem(opened, t0.AddSeconds(3), Options);
        Assert.Null(closed.OpenItemId);
        Assert.True(closed.Viewport.Zoom < Options.OpenZoomStart);
        Assert.True(closed.Manual);

        Assert.Equal(camera, CameraDirector.OpenItem(camera, "nope", layout, t0, Options)); // unknown id: no-op
    }

    [Fact]
    public void ManualZoom_OverANode_OpensIt_AndZoomingOutClosesIt()
    {
        var t0 = Fixtures.Now;
        var (_, layout) = Build([Fixtures.Item("a1", state: "Working"), Fixtures.Item("far", state: "Working")], t0);
        var node = layout.Nodes["a1"];
        var camera = CameraDirector.Initial(layout, t0, View, Options);

        var zoomedIn = CameraDirector.ApplyManual(camera,
            new CameraViewport { CenterX = node.X + 10, CenterY = node.Y - 5, Zoom = Options.OpenZoomEnd }, t0, layout, Options);
        Assert.Equal("a1", zoomedIn.OpenItemId);
        Assert.Equal(0, zoomedIn.TravelMs);

        var zoomedOut = CameraDirector.ApplyManual(zoomedIn,
            new CameraViewport { CenterX = node.X, CenterY = node.Y, Zoom = Options.OpenZoomStart * 0.5 }, t0, layout, Options);
        Assert.Null(zoomedOut.OpenItemId);

        var offTarget = CameraDirector.ApplyManual(camera,
            new CameraViewport { CenterX = node.X + 10_000, CenterY = node.Y, Zoom = Options.OpenZoomEnd }, t0, layout, Options);
        Assert.Null(offTarget.OpenItemId);
    }

    [Fact]
    public void OpenFraction_IsContinuousAcrossTheBand()
    {
        Assert.Equal(0, SemanticZoom.OpenFraction(Options.OpenZoomStart - 0.1, Options));
        Assert.Equal(1, SemanticZoom.OpenFraction(Options.OpenZoomEnd + 0.1, Options));
        var mid = SemanticZoom.OpenFraction((Options.OpenZoomStart + Options.OpenZoomEnd) / 2, Options);
        Assert.InRange(mid, 0.45, 0.55);
        Assert.Equal(0, SemanticZoom.OpenFraction(double.NaN, Options));
    }

    [Fact]
    public void Overview_FramesEverything_AndSuspendsAuto()
    {
        var t0 = Fixtures.Now;
        var (_, layout) = Build([Fixtures.Item("a1", state: "Working"), Fixtures.Item("b1", state: "Failed")], t0);
        var camera = CameraDirector.Initial(layout, t0, View, Options);

        var overview = CameraDirector.Overview(camera with { Viewport = camera.Viewport with { Zoom = 1.9 } }, layout, t0, View, Options);
        Assert.True(overview.Manual);
        Assert.Equal(CameraFocusKind.All, overview.FocusKind);
        Assert.Null(overview.OpenItemId);
        Assert.True(overview.TravelMs > 0);
    }
}

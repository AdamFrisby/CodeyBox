using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Camera behaviour over a scripted sequence of model states: it goes to a
/// new failure, holds while the failure is unresolved, resumes cycling once
/// cleared, and yields to manual input until explicitly resumed.
/// </summary>
public sealed class CameraTests
{
    private static readonly FleetMapOptions Options = new() { DwellSeconds = 12, LandingDwellSeconds = 0 };
    private static readonly CameraViewSize View = new(1600, 900);

    private sealed record Scripted(
        FleetProjection Projection,
        FleetMapLayout Layout,
        DateTimeOffset Now);

    private static Scripted Build(IReadOnlyList<AdminWorkItem> items, DateTimeOffset now)
    {
        var snapshot = Fixtures.Snapshot(items) with { Now = now };
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(snapshot.Items.ToList(), projection.Chains.ToList(), Options);
        return new Scripted(projection, layout, now);
    }

    [Fact]
    public void Idle_CyclesBetweenActiveChains()
    {
        var t0 = Fixtures.Now;
        var first = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Working"),
        ], t0);
        var camera = CameraDirector.Initial(first.Layout, t0, View, Options);
        camera = CameraDirector.Next(camera, first.Projection, first.Layout, t0, View, Options);

        var firstChain = camera.FocusId;
        Assert.Equal(CameraFocusKind.Chain, camera.FocusKind);

        // Still dwelling: same chain.
        var dwell = CameraDirector.Next(camera, first.Projection, first.Layout, t0.AddSeconds(5), View, Options);
        Assert.Equal(firstChain, dwell.FocusId);

        // Past the dwell: moves on to the other chain.
        var moved = CameraDirector.Next(camera, first.Projection, first.Layout, t0.AddSeconds(13), View, Options);
        Assert.Equal(CameraFocusKind.Chain, moved.FocusKind);
        Assert.NotEqual(firstChain, moved.FocusId);
    }

    [Fact]
    public void NewFailure_TakesCameraAndHoldsWhileUnresolved()
    {
        var t0 = Fixtures.Now;
        var healthy = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Working"),
        ], t0);
        var camera = CameraDirector.Initial(healthy.Layout, t0, View, Options);
        camera = CameraDirector.Next(camera, healthy.Projection, healthy.Layout, t0, View, Options);

        // A failure appears: the camera goes to it and zooms to the item.
        var t1 = t0.AddMinutes(1);
        var broken = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Failed"),
        ], t1);
        camera = CameraDirector.Next(camera, broken.Projection, broken.Layout, t1, View, Options);
        Assert.Equal(CameraFocusKind.Item, camera.FocusKind);
        Assert.Equal("b1", camera.FocusId);
        Assert.Equal(Options.ItemFocusZoom, camera.Viewport.Zoom);

        // Ticks pass with the failure unresolved: the camera holds.
        for (var tick = 1; tick <= 10; tick++)
        {
            var held = CameraDirector.Next(
                camera, broken.Projection, broken.Layout, t1.AddSeconds(tick * 30), View, Options);
            Assert.Equal("b1", held.FocusId);
            Assert.Equal(CameraFocusKind.Item, held.FocusKind);
            camera = held;
        }
    }

    [Fact]
    public void ClearedFailure_ResumesCycling()
    {
        var t0 = Fixtures.Now;
        var broken = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Failed"),
        ], t0);
        var camera = CameraDirector.Initial(broken.Layout, t0, View, Options);
        camera = CameraDirector.Next(camera, broken.Projection, broken.Layout, t0, View, Options);
        Assert.Equal("b1", camera.FocusId);

        // The failure clears (retried back to work): the camera leaves the item.
        var t1 = t0.AddMinutes(5);
        var healed = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Working"),
        ], t1);
        camera = CameraDirector.Next(camera, healed.Projection, healed.Layout, t1, View, Options);
        Assert.NotEqual("b1", camera.FocusId);
        Assert.Equal(CameraFocusKind.Chain, camera.FocusKind);
    }

    [Fact]
    public void ManualInput_SuspendsAutoUntilResumed()
    {
        var t0 = Fixtures.Now;
        var healthy = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Working"),
        ], t0);
        var camera = CameraDirector.Initial(healthy.Layout, t0, View, Options);
        camera = CameraDirector.Next(camera, healthy.Projection, healthy.Layout, t0, View, Options);

        // The operator grabs the view.
        var manualView = new CameraViewport { CenterX = 10, CenterY = 20, Zoom = 2.0 };
        camera = CameraDirector.ApplyManual(camera, manualView, t0.AddSeconds(1));
        Assert.True(camera.Manual);

        // A failure appears while held manually: the camera does not move.
        var t1 = t0.AddMinutes(1);
        var broken = Build([
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Failed"),
        ], t1);
        var held = CameraDirector.Next(camera, broken.Projection, broken.Layout, t1, View, Options);
        Assert.Equal(manualView, held.Viewport);
        Assert.True(held.Manual);

        // The operator resumes auto: the pending failure takes the camera.
        camera = CameraDirector.ResumeAuto(held, t1.AddSeconds(1));
        Assert.False(camera.Manual);
        camera = CameraDirector.Next(camera, broken.Projection, broken.Layout, t1.AddSeconds(2), View, Options);
        Assert.Equal("b1", camera.FocusId);
        Assert.Equal(CameraFocusKind.Item, camera.FocusKind);
    }

    [Fact]
    public void EmptyFleet_FramesOverview()
    {
        var empty = Build([], Fixtures.Now);
        var camera = CameraDirector.Initial(empty.Layout, Fixtures.Now, View, Options);

        Assert.Equal(CameraFocusKind.All, camera.FocusKind);
        Assert.Equal(Options.OverviewZoom, camera.Viewport.Zoom);
    }

    [Fact]
    public void Zoom_FollowsFocusScale()
    {
        var t0 = Fixtures.Now;
        var scripted = Build([
            Fixtures.Item("solo", state: "Working"),
            Fixtures.Item("c1", state: "Working"),
            Fixtures.Item("c2", state: "Working", dependsOn: ["c1"]),
            Fixtures.Item("c3", state: "Working", dependsOn: ["c2"]),
        ], t0);

        // A lone item in a chain frames at item zoom; the busy chain fits its bounds.
        var soloChain = scripted.Projection.Chains.Single(c => c.ItemIds.Count == 1);
        var busyChain = scripted.Projection.Chains.Single(c => c.ItemIds.Count == 3);
        var camera = new CameraState
        {
            Viewport = new CameraViewport { CenterX = 0, CenterY = 0, Zoom = 1 },
            LastChangeAt = t0,
            CycleIndex = -1,
        };
        var solo = CameraDirector.Next(camera, scripted.Projection with
        {
            Attention = [new AttentionScore { ItemId = "x", Score = 0 }],
            ChainAttention =
            [
                new ChainAttention { ChainId = soloChain.Id, Score = 5, ItemIds = soloChain.ItemIds, TopItemId = "solo" },
            ],
        }, scripted.Layout, t0, View, Options);
        Assert.Equal(soloChain.Id, solo.FocusId);
        Assert.Equal(Options.ItemFocusZoom, solo.Viewport.Zoom);

        var busy = CameraDirector.Next(camera, scripted.Projection with
        {
            Attention = [new AttentionScore { ItemId = "x", Score = 0 }],
            ChainAttention =
            [
                new ChainAttention { ChainId = busyChain.Id, Score = 5, ItemIds = busyChain.ItemIds, TopItemId = "c1" },
            ],
        }, scripted.Layout, t0, View, Options);
        Assert.Equal(busyChain.Id, busy.FocusId);
        Assert.InRange(busy.Viewport.Zoom, Options.MinFitZoom, Options.MaxFitZoom);
    }
}

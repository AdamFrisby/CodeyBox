using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Under prefers-reduced-motion the camera never moves on its own, whatever
/// the fleet does — and the operator can still drive it by hand.
/// </summary>
public sealed class ReducedMotionTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly CameraViewSize View = new(1600, 900);

    [Fact]
    public void NoAutomaticMovement_AcrossFailuresClearsAndCycles()
    {
        var t0 = Fixtures.Now;
        var healthyItems = new List<AdminWorkItem>
        {
            Fixtures.Item("a1", state: "Working"),
            Fixtures.Item("b1", state: "Working"),
        };
        var healthy = Project(healthyItems, t0);
        var camera = CameraDirector.Initial(healthy.Layout, t0, View, Options);
        var parked = camera;

        // Cycle ticks pass: nothing moves.
        for (var tick = 0; tick < 5; tick++)
        {
            parked = CameraDirector.Next(
                parked, healthy.Projection, healthy.Layout, t0.AddSeconds(tick * 30),
                View, Options, reducedMotion: true);
            Assert.Equal(camera.Viewport, parked.Viewport);
            Assert.Equal(camera.FocusId, parked.FocusId);
        }

        // A failure appears and clears: still nothing moves.
        var t1 = t0.AddMinutes(2);
        var broken = Project(
            [
                Fixtures.Item("a1", state: "Working"),
                Fixtures.Item("b1", state: "Failed"),
            ], t1);
        var onFailure = CameraDirector.Next(
            parked, broken.Projection, broken.Layout, t1, View, Options, reducedMotion: true);
        Assert.Equal(camera.Viewport, onFailure.Viewport);

        var t2 = t1.AddMinutes(5);
        var healed = Project(
            [
                Fixtures.Item("a1", state: "Working"),
                Fixtures.Item("b1", state: "Done"),
            ], t2);
        var onClear = CameraDirector.Next(
            onFailure, healed.Projection, healed.Layout, t2, View, Options, reducedMotion: true);
        Assert.Equal(camera.Viewport, onClear.Viewport);
    }

    [Fact]
    public void ManualControl_StillWorksUnderReducedMotion()
    {
        var scripted = Project(
            [Fixtures.Item("a1", state: "Working")], Fixtures.Now);
        var camera = CameraDirector.Initial(scripted.Layout, Fixtures.Now, View, Options);

        var moved = new CameraViewport { CenterX = 42, CenterY = 43, Zoom = 1.2 };
        var manual = CameraDirector.ApplyManual(camera, moved, Fixtures.Now);
        var next = CameraDirector.Next(
            manual, scripted.Projection, scripted.Layout, Fixtures.Now.AddMinutes(1),
            View, Options, reducedMotion: true);

        Assert.Equal(moved, next.Viewport);
    }

    private static (FleetProjection Projection, FleetMapLayout Layout) Project(
        IReadOnlyList<AdminWorkItem> items, DateTimeOffset now)
    {
        var snapshot = Fixtures.Snapshot(items) with { Now = now };
        var projection = FleetProjectionBuilder.Project(snapshot);
        var layout = FleetMapBuilder.DeriveInitial(
            snapshot.Items.ToList(), projection.Chains.ToList(), Options);
        return (projection, layout);
    }
}

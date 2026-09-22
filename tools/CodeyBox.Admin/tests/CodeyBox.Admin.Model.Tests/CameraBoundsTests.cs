using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// The camera is bounded to the content plus a margin; the layout is not an
/// output of any of this, so bounding the camera can never move a node.
/// </summary>
public sealed class CameraBoundsTests
{
    private static readonly FleetMapOptions Options = new();
    private static readonly DateTimeOffset Now = Fixtures.Now;
    private static readonly CameraViewSize View = new(1000, 1000); // wider content than the view, shorter content than the view

    private static FleetMapLayout Layout()
    {
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("a", state: "Done", updatedAt: Now.AddHours(-2)), Fixtures.Item("run", state: "Working"),
        }.Concat(Enumerable.Range(0, 6).Select(i => Fixtures.Item($"q{i}", state: "Queued") with { QueuePosition = i })).ToList();
        return FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options, Now, capacity: 2);
    }

    [Fact]
    public void Bounds_AreTheContentPlusAMargin_ThatGrowsAsTheZoomShrinks()
    {
        var layout = Layout();
        var tight = CameraBounds.Of(layout, View, 2.0, Options);
        var loose = CameraBounds.Of(layout, View, 0.2, Options);
        var minX = layout.Nodes.Values.Min(n => n.X);
        Assert.True(tight.MinX < minX - Options.NodeWidth / 2);
        Assert.True(loose.MinX < tight.MinX, "zoomed out, the margin covers more of the world");
        Assert.Equal(new MapBounds(-Options.ColumnGap, -Options.RowGap, Options.ColumnGap, Options.RowGap), CameraBounds.Of(new FleetMapLayout(), View, 1, Options));
    }

    [Fact]
    public void Clamp_HoldsTheViewInsideTheContent_CentresWhatIsNarrowerThanTheView_AndFloorsTheZoom()
    {
        var layout = Layout();
        var b = CameraBounds.Of(layout, View, 1.0, Options);

        var farRight = CameraBounds.Clamp(new CameraViewport { CenterX = 1e6, CenterY = 0, Zoom = 1.0 }, layout, View, Options);
        Assert.Equal(b.MaxX - View.Width / 2, farRight.CenterX, 6);
        Assert.Equal((b.MinY + b.MaxY) / 2, farRight.CenterY, 6); // taller view than content: centred
        Assert.Equal(1.0, farRight.Zoom);

        var inside = new CameraViewport { CenterX = (b.MinX + b.MaxX) / 2, CenterY = (b.MinY + b.MaxY) / 2, Zoom = 2.0 };
        Assert.Equal(inside, CameraBounds.Clamp(inside, layout, View, Options));

        var tooFar = CameraBounds.Clamp(new CameraViewport { CenterX = 0, CenterY = 0, Zoom = 0.001 }, layout, View, Options);
        Assert.Equal(CameraBounds.MinZoom(layout, View, Options), tooFar.Zoom);
        Assert.True(tooFar.Zoom >= Options.WholeBoardMinZoom && tooFar.Zoom <= Options.MinFitZoom);
    }

    [Fact]
    public void ApplyManual_KeepsTheViewportInBounds_AndTheLayoutUntouched()
    {
        var layout = Layout();
        var before = layout.Nodes.OrderBy(kv => kv.Key).ToList();
        var state = CameraDirector.Initial(layout, Now, View, Options);
        var wild = CameraDirector.ApplyManual(state, new CameraViewport { CenterX = -1e7, CenterY = 1e7, Zoom = 0.0001 }, Now, layout, Options, View);
        var b = CameraBounds.Of(layout, View, wild.Viewport.Zoom, Options);
        Assert.True(wild.Viewport.CenterX >= b.MinX && wild.Viewport.CenterX <= b.MaxX);
        Assert.True(wild.Viewport.CenterY >= b.MinY && wild.Viewport.CenterY <= b.MaxY);
        Assert.True(wild.Manual);
        Assert.Equal(before, layout.Nodes.OrderBy(kv => kv.Key).ToList());
    }
}

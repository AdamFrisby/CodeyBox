using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// An edge never runs through a node that stands between its endpoints: it
/// arcs over it, and says which node made it.
/// </summary>
public sealed class EdgeRouterTests
{
    private static readonly FleetMapOptions Options = new();

    private static MapNodeLayout N(string id, double x, double y, string lane = "L") =>
        new() { ItemId = id, ChainId = lane, LaneId = lane, X = x, Y = y, Depth = 0 };

    private static FleetMapLayout Layout(params MapNodeLayout[] nodes) => new()
    {
        Nodes = nodes.ToDictionary(n => n.ItemId, n => n, StringComparer.Ordinal),
        Lanes = nodes.GroupBy(n => n.LaneId).Select(g => new MapChainLane { ChainId = g.Key, Y = g.Min(n => n.Y), Height = Options.RowGap }).ToList(),
    };

    [Fact]
    public void AClearRun_IsAPlainCurve()
    {
        var routes = EdgeRouter.Route(Layout(N("a", 0, 0), N("b", 500, 0), N("c", 250, 300)), [("a", "b")], Options);
        var r = Assert.Single(routes);
        Assert.Equal(0, r.Bend);
        Assert.Empty(r.Obstacles);
    }

    [Fact]
    public void ANodeInTheWay_OnTheSameRow_LiftsTheEdgeIntoTheGapAbove()
    {
        var routes = EdgeRouter.Route(Layout(N("a", 0, 0), N("mid", 250, 0), N("b", 500, 0)), [("a", "b"), ("a", "mid")], Options);
        var ab = routes.Single(r => r.To == "b");
        Assert.Equal(["mid"], ab.Obstacles);
        Assert.True(ab.Bend < 0, "lifted");
        Assert.True(-ab.Bend <= Options.RowGap, "but no further than the row above");
        Assert.Equal(0, routes.Single(r => r.To == "mid").Bend); // adjacent: nothing between
    }

    [Fact]
    public void ANodeInTheWay_OnAnotherRow_ArcsOverTheLane()
    {
        // a on row 1, b on row 0, and a node on row 0 between them where the straight run would pass.
        var lanes = Layout(N("a", 0, 144), N("b", 750, 0), N("mid", 375, 72 - 60), N("far", 375, 600, "M"));
        var routes = EdgeRouter.Route(lanes, [("a", "b")], Options);
        var r = Assert.Single(routes);
        Assert.Contains("mid", r.Obstacles);
        Assert.True(-r.Bend > Options.NodeHeight / 2, "clears the top edge of the lane's first row");
    }

    [Fact]
    public void OtherLanes_AndUnknownIds_AreIgnored()
    {
        var layout = Layout(N("a", 0, 0), N("b", 500, 0), N("elsewhere", 250, 0, "M"), N("c1", -900, 0));
        var routes = EdgeRouter.Route(layout, [("a", "b"), ("a", "ghost"), ("c1", "a")], Options);
        Assert.Equal(2, routes.Count);
        Assert.Equal(0, routes.Single(r => r.To == "b").Bend);
        Assert.Equal(0, routes.Single(r => r.From == "c1").Bend); // c1 → a: nothing between
    }
}

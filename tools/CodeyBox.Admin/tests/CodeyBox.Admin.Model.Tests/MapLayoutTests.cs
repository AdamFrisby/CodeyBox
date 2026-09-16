using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Layout stability: an item that did not change does not move. Every test
/// derives an initial layout, refreshes it through Update with one change,
/// and asserts pre-existing nodes keep byte-exact positions.
/// </summary>
public sealed class MapLayoutTests
{
    private static readonly FleetMapOptions Options = new();

    private static (List<AdminWorkItem> Items, List<WorkChain> Chains) Fleet(int count)
    {
        var items = new List<AdminWorkItem>();
        for (var i = 0; i < count; i++)
        {
            var id = $"item-{i:D3}";
            items.Add(Fixtures.Item(id, title: $"Work {i}"));
        }
        // Chain every third item onto its predecessor so depths vary.
        for (var i = 3; i < count; i += 3)
        {
            items[i] = items[i] with { DependsOn = [items[i - 1].Id] };
        }
        var snapshot = Fixtures.Snapshot(items);
        var chains = ChainGrouping.BuildChains(snapshot.Items).ToList();
        return (items, chains);
    }

    [Fact]
    public void DeriveInitial_IsDeterministic()
    {
        var (items, chains) = Fleet(20);

        var first = FleetMapBuilder.DeriveInitial(items, chains, Options);
        var second = FleetMapBuilder.DeriveInitial(items, chains, Options);

        Assert.Equal(first.Nodes.Count, second.Nodes.Count);
        foreach (var (id, node) in first.Nodes)
        {
            Assert.Equal(node, second.Nodes[id]);
        }
    }

    [Fact]
    public void MetadataChange_MovesNothing()
    {
        var (items, chains) = Fleet(20);
        var layout = FleetMapBuilder.DeriveInitial(items, chains, Options);

        var changed = items.Select(i => i.Id == "item-005"
            ? i with { State = "Working", Title = "Work 5 (edited)" }
            : i).ToList();
        var refreshed = FleetMapBuilder.Update(layout, changed, chains, Options);

        Assert.Equal(layout.Nodes.Count, refreshed.Nodes.Count);
        foreach (var (id, node) in layout.Nodes)
        {
            Assert.Equal(node, refreshed.Nodes[id]);
        }
    }

    [Fact]
    public void AddedItem_KeepsEveryExistingNodeStill()
    {
        var (items, chains) = Fleet(20);
        var layout = FleetMapBuilder.DeriveInitial(items, chains, Options);

        var grown = items.Append(Fixtures.Item("item-new", title: "Brand new")).ToList();
        var grownChains = ChainGrouping.BuildChains(grown).ToList();
        var refreshed = FleetMapBuilder.Update(layout, grown, grownChains, Options);

        Assert.True(refreshed.Nodes.ContainsKey("item-new"));
        foreach (var (id, node) in layout.Nodes)
        {
            Assert.Equal(node, refreshed.Nodes[id]);
        }
    }

    [Fact]
    public void RemovedItem_KeepsEverySurvivorStill()
    {
        var (items, chains) = Fleet(20);
        var layout = FleetMapBuilder.DeriveInitial(items, chains, Options);

        var shrunk = items.Where(i => i.Id != "item-007").ToList();
        var refreshed = FleetMapBuilder.Update(layout, shrunk, chains, Options);

        Assert.False(refreshed.Nodes.ContainsKey("item-007"));
        foreach (var (id, node) in layout.Nodes)
        {
            if (id == "item-007")
            {
                continue;
            }
            Assert.Equal(node, refreshed.Nodes[id]);
        }
    }

    [Fact]
    public void Dependency_ReadsLeftToRight()
    {
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("root"),
            Fixtures.Item("child", dependsOn: ["root"]),
            Fixtures.Item("grandchild", dependsOn: ["child"]),
        };
        var chains = ChainGrouping.BuildChains(items).ToList();

        var layout = FleetMapBuilder.DeriveInitial(items, chains, Options);

        Assert.True(layout.Nodes["root"].X < layout.Nodes["child"].X);
        Assert.True(layout.Nodes["child"].X < layout.Nodes["grandchild"].X);
        Assert.Equal(0, layout.Nodes["root"].Depth);
        Assert.Equal(1, layout.Nodes["child"].Depth);
        Assert.Equal(2, layout.Nodes["grandchild"].Depth);
    }

    [Fact]
    public void DependencyCycle_DoesNotHangAndClampsDepth()
    {
        var items = new List<AdminWorkItem>
        {
            Fixtures.Item("a", dependsOn: ["b"]),
            Fixtures.Item("b", dependsOn: ["a"]),
        };
        var chains = ChainGrouping.BuildChains(items).ToList();

        var layout = FleetMapBuilder.DeriveInitial(items, chains, Options);

        Assert.Equal(2, layout.Nodes.Count);
        foreach (var node in layout.Nodes.Values)
        {
            Assert.InRange(node.Depth, 0, Options.MaxDepthColumns - 1);
        }
    }

    [Fact]
    public void SeventyItemFleet_LaysOutEveryNode()
    {
        var (items, chains) = Fleet(78);
        var layout = FleetMapBuilder.DeriveInitial(items, chains, Options);

        Assert.Equal(78, layout.Nodes.Count);
        Assert.True(layout.Lanes.Count > 1);
    }
}

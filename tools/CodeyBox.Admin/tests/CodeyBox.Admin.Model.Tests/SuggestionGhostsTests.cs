using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Suggestions as ghost nodes: only where the parent is on the map, most
/// severe first, capped per parent with the rest folded, placed exactly
/// where promotion would land them, and never for dismissed or promoted ones
/// (the caller passes open suggestions only — the rule is asserted here).
/// </summary>
public sealed class SuggestionGhostsTests
{
    private static readonly FleetMapOptions Options = new();

    private static GhostSuggestion Sug(string id, string parent, string severity, int minutesAgo = 0) => new()
    {
        Id = id,
        ParentId = parent,
        Title = $"Suggestion {id}",
        Severity = severity,
        CreatedAt = Fixtures.Now.AddMinutes(-minutesAgo),
    };

    private static (List<AdminWorkItem> Items, FleetMapLayout Layout) Fleet()
    {
        var items = new List<AdminWorkItem> { Fixtures.Item("hub", state: "Working"), Fixtures.Item("dep", dependsOn: ["hub"]), Fixtures.Item("solo") };
        return (items, FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options));
    }

    [Fact]
    public void OnlyParentsOnTheMap_GetGhosts_MostSevereFirst_CappedAndFolded()
    {
        var (items, layout) = Fleet();
        var open = new List<GhostSuggestion>
        {
            Sug("s1", "hub", "minor", 10), Sug("s2", "hub", "important", 50), Sug("s3", "hub", "notable", 5),
            Sug("s4", "hub", "notable", 60), Sug("s5", "hub", "minor", 1),
            Sug("s6", "gone-parent", "important"),
        };

        var groups = SuggestionGhosts.Place(open, layout, items, new SuggestionGhostOptions { MaxPerParent = 3 }, Options);

        var hub = Assert.Single(groups);
        Assert.Equal("hub", hub.ParentId);
        Assert.Equal(["s2", "s3", "s4"], hub.Shown.Select(p => p.Suggestion.Id).ToList()); // important, then notable newest-first
        Assert.Equal(2, hub.Folded);
        Assert.Equal([0, 1, 2], hub.Shown.Select(p => p.Rank).ToList());
    }

    [Fact]
    public void Ghosts_SitWherePromotionWouldLand_AndDoNotCollideWithEachOther()
    {
        var (items, layout) = Fleet();
        var groups = SuggestionGhosts.Place([Sug("a", "hub", "notable"), Sug("b", "hub", "notable", 1)], layout, items, null, Options);

        var shown = Assert.Single(groups).Shown;
        Assert.Equal(2, shown.Count);
        Assert.All(shown, p => Assert.True(p.X > layout.Nodes["hub"].X, "to the right of the parent"));
        Assert.NotEqual((shown[0].X, shown[0].Y), (shown[1].X, shown[1].Y));

        // Promoting the first one lands it exactly on its ghost's slot.
        var promoted = items.Append(Fixtures.Item("promoted", dependsOn: ["hub"])).ToList();
        var refreshed = FleetMapBuilder.Update(layout, promoted, ChainGrouping.BuildChains(promoted).ToList(), Options);
        Assert.Equal((shown[0].X, shown[0].Y), (refreshed.Nodes["promoted"].X, refreshed.Nodes["promoted"].Y));
    }

    [Fact]
    public void SeverityThreshold_AndToggle_Apply()
    {
        var (items, layout) = Fleet();
        var open = new List<GhostSuggestion> { Sug("i", "hub", "important"), Sug("n", "hub", "notable"), Sug("m", "hub", "minor") };

        var notable = SuggestionGhosts.Place(open, layout, items, new SuggestionGhostOptions { MinSeverity = "notable" }, Options);
        Assert.Equal(["i", "n"], Assert.Single(notable).Shown.Select(p => p.Suggestion.Id).ToList());

        var important = SuggestionGhosts.Place(open, layout, items, new SuggestionGhostOptions { MinSeverity = "important" }, Options);
        Assert.Equal(["i"], Assert.Single(important).Shown.Select(p => p.Suggestion.Id).ToList());

        Assert.Empty(SuggestionGhosts.Place(open, layout, items, new SuggestionGhostOptions { Show = false }, Options));
        Assert.Equal(3, SuggestionGhosts.SeverityRank("important"));
        Assert.Equal(0, SuggestionGhosts.SeverityRank("weird"));
    }

    [Fact]
    public void PreviewDependents_ReturnsDistinctSlots_InOrder_AndNothingForAnUnknownParent()
    {
        var (items, layout) = Fleet();
        var slots = FleetMapBuilder.PreviewDependents(layout, items, "hub", 4, Options);
        Assert.Equal(4, slots.Count);
        Assert.Equal(4, slots.Distinct().Count());
        Assert.Empty(FleetMapBuilder.PreviewDependents(layout, items, "nope", 2, Options));
        Assert.Empty(FleetMapBuilder.PreviewDependents(layout, items, "hub", 0, Options));
    }
}

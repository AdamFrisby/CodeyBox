using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>Chain grouping: explicit edges, title series, both, and must-not-join.</summary>
public sealed class ChainGroupingTests
{
    [Fact]
    public void ExplicitEdgesOnly_FormsOneChain()
    {
        var chains = ChainGrouping.BuildChains([
            Fixtures.Item("a", state: "Done"),
            Fixtures.Item("b", dependsOn: ["a"]),
            Fixtures.Item("c", dependsOn: ["b"]),
        ]);

        var withAll = Assert.Single(chains, c => c.ItemIds.Count == 3);
        Assert.Equal(["a", "b", "c"], withAll.ItemIds);
    }

    [Fact]
    public void TitleSeriesOnly_JoinsWithoutEdges()
    {
        var chains = ChainGrouping.BuildChains([
            Fixtures.Item("a", title: "Deployment verification 1/3"),
            Fixtures.Item("b", title: "Deployment verification 2/3"),
            Fixtures.Item("c", title: "Deployment verification 3/3"),
        ]);

        var joined = Assert.Single(chains);
        Assert.Equal(["a", "b", "c"], joined.ItemIds);
        Assert.Equal("Deployment verification", joined.SeriesPrefix);
    }

    [Fact]
    public void HashSeries_Joins()
    {
        var chains = ChainGrouping.BuildChains([
            Fixtures.Item("a", title: "Decompose PipelineRunner #1"),
            Fixtures.Item("b", title: "Decompose PipelineRunner #2"),
        ]);

        var joined = Assert.Single(chains);
        Assert.Equal(["a", "b"], joined.ItemIds);
    }

    [Fact]
    public void EdgesAndSeriesTogether_FormOneChain()
    {
        // a —edge→ b, and b/c share a title series: all three are one chain.
        var chains = ChainGrouping.BuildChains([
            Fixtures.Item("a", title: "Test selection (RTS) 1/7", state: "Done"),
            Fixtures.Item("b", title: "Test selection (RTS) 2/7", dependsOn: ["a"]),
            Fixtures.Item("c", title: "Test selection (RTS) 3/7"),
        ]);

        var joined = Assert.Single(chains);
        Assert.Equal(["a", "b", "c"], joined.ItemIds);
    }

    [Fact]
    public void DifferentPrefix_DoesNotJoin()
    {
        var chains = ChainGrouping.BuildChains([
            Fixtures.Item("a", title: "Release 1/3"),
            Fixtures.Item("b", title: "Sprint 1/3"),
        ]);

        Assert.Equal(2, chains.Count);
        Assert.All(chains, c => Assert.Single(c.ItemIds));
    }

    [Fact]
    public void CoincidentalBareNumbers_DoNotJoin()
    {
        var chains = ChainGrouping.BuildChains([
            Fixtures.Item("a", title: "Fix login 1"),
            Fixtures.Item("b", title: "Fix login 2"),
        ]);

        Assert.Equal(2, chains.Count);
    }

    [Fact]
    public void UntitledItems_JoinOnlyByEdges()
    {
        var chains = ChainGrouping.BuildChains([
            Fixtures.Item("a", title: ""),
            Fixtures.Item("b", title: ""),
        ]);

        Assert.Equal(2, chains.Count);
    }

    [Fact]
    public void UnknownDepIds_AreIgnoredForGrouping()
    {
        var chains = ChainGrouping.BuildChains([
            Fixtures.Item("a", dependsOn: ["ghost"]),
            Fixtures.Item("b", title: "Lone work"),
        ]);

        Assert.Equal(2, chains.Count);
    }
}

using CodeyBox.Admin.Model;
using CodeyBox.Composition;

namespace CodeyBox.Admin.Model.Tests;

public sealed class ChainEdgesTests
{
    [Fact]
    public void Series_EachWaitsForPrevious()
    {
        var edges = ChainEdges.Series(4);

        Assert.Empty(edges[0]);
        Assert.Equal([1], edges[1]);
        Assert.Equal([3], edges[3]);
    }

    [Fact]
    public void FanOutAfter_FoundationInSeriesThenParallel()
    {
        var edges = ChainEdges.FanOutAfter(7, 3);

        Assert.Empty(edges[0]);
        Assert.Equal([1], edges[1]);
        Assert.Equal([2], edges[2]);
        Assert.Equal([3], edges[3]);
        Assert.Equal([3], edges[6]);
        Assert.Equal([1], ChainEdges.Roots(edges));
    }

    [Fact]
    public void FanOutAfter_RejectsRootOutsideChain()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChainEdges.FanOutAfter(3, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChainEdges.FanOutAfter(3, 0));
    }

    [Theory]
    [InlineData("1, 3", new[] { 1, 3 })]
    [InlineData("after 2", new[] { 2 })]
    [InlineData("waits for: #4 5", new[] { 4, 5 })]
    [InlineData("3 3 1", new[] { 1, 3 })]
    [InlineData("", new int[0])]
    [InlineData("   ", new int[0])]
    public void ParseWaitsFor_ReadsTypedEdges(string text, int[] expected)
    {
        var (edges, problem) = ChainEdges.ParseWaitsFor(text, count: 6, self: 6);

        Assert.Null(problem);
        Assert.Equal(expected, edges);
    }

    [Fact]
    public void ParseWaitsFor_ReportsBadTokensMissingItemsAndSelf()
    {
        Assert.Contains("not an item number", ChainEdges.ParseWaitsFor("1, two", 4, 3).Problem);
        Assert.Contains("no item 9", ChainEdges.ParseWaitsFor("9", 4, 3).Problem);
        Assert.Contains("cannot wait for itself", ChainEdges.ParseWaitsFor("3", 4, 3).Problem);
    }

    [Fact]
    public void Remove_SplicesDependentsOntoRemovedItemsParentsAndShiftsLater()
    {
        IReadOnlyList<IReadOnlyList<int>> edges = [[], [1], [1, 2], [3]];

        var after = ChainEdges.Remove(edges, 2);

        Assert.Equal(3, after.Count);
        Assert.Empty(after[0]);
        Assert.Equal([1], after[1]);
        Assert.Equal([2], after[2]);
    }

    [Fact]
    public void Remove_MiddleOfSeries_KeepsItASeries()
    {
        var after = ChainEdges.Remove(ChainEdges.Series(4), 2);

        Assert.Equal(ChainEdges.Series(3), after);
    }

    [Fact]
    public void Remove_RootOfFanOut_MakesTheFanRoots()
    {
        var after = ChainEdges.Remove(ChainEdges.FanOutAfter(4, 1), 1);

        Assert.Equal(ChainEdges.Parallel(3), after);
    }

    [Fact]
    public void CycleMembers_FindsTheLoopAndOnlyTheLoop()
    {
        IReadOnlyList<IReadOnlyList<int>> edges = [[3], [1], [2], [1]];

        Assert.Equal([1, 2, 3], ChainEdges.CycleMembers(edges));
        Assert.Empty(ChainEdges.CycleMembers(ChainEdges.Series(5)));
        Assert.Empty(ChainEdges.CycleMembers(ChainEdges.FanOutAfter(10, 2)));
    }

    [Fact]
    public void Describe_ReadsCommonShapesInWords()
    {
        Assert.Equal("1 → 2 → 3", ChainEdges.Describe(ChainEdges.Series(3)));
        Assert.Equal("1 → 2 → … → 9 in series", ChainEdges.Describe(ChainEdges.Series(9)));
        Assert.Equal("all 4 in parallel", ChainEdges.Describe(ChainEdges.Parallel(4)));
        Assert.Equal("1 → 2 → 3, then 4–9 in parallel after 3", ChainEdges.Describe(ChainEdges.FanOutAfter(9, 3)));
        Assert.Equal("1, then 2–5 in parallel after 1", ChainEdges.Describe(ChainEdges.FanOutAfter(5, 1)));
        Assert.Equal("4 items, custom edges", ChainEdges.Describe([[], [1], [1], [2, 3]]));
    }

    [Fact]
    public void Format_SortsAndDeduplicates()
    {
        Assert.Equal("1, 3, 5", ChainEdges.Format([5, 1, 3, 1]));
    }
}

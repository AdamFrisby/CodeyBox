using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Motion is signal: transitions fire exactly for state changes, unblocks,
/// chain completions, arrivals and departures — and never for a quiet fleet.
/// </summary>
public sealed class TransitionTests
{
    private static readonly FleetMapOptions Options = new();

    private static (FleetSnapshot Snapshot, IReadOnlyDictionary<string, ItemActivity> Activities)
        Analyze(IReadOnlyList<AdminWorkItem> items)
    {
        var snapshot = Fixtures.Snapshot(items);
        return (snapshot, ActivityAnalyzer.AnalyzeAll(snapshot));
    }

    [Fact]
    public void IdenticalSnapshots_EmitNothing()
    {
        var (snapshot, activities) = Analyze([
            Fixtures.Item("a", state: "Working"),
            Fixtures.Item("b", state: "Queued"),
        ]);
        var chains = ChainGrouping.BuildChains(snapshot.Items).ToList();

        var events = MapTransitionDetector.Detect(
            snapshot, activities, snapshot, activities, chains, Options);

        Assert.Empty(events);
    }

    [Fact]
    public void FirstFrame_EmitsNothing()
    {
        var (snapshot, activities) = Analyze([Fixtures.Item("a", state: "Working")]);

        var events = MapTransitionDetector.Detect(null, null, snapshot, activities, null, Options);

        Assert.Empty(events);
    }

    [Fact]
    public void StateChange_EmitsExactlyOneSignal()
    {
        var (prev, prevActivities) = Analyze([Fixtures.Item("a", state: "Queued")]);
        var (curr, currActivities) = Analyze([Fixtures.Item("a", state: "Working")]);

        var events = MapTransitionDetector.Detect(
            prev, prevActivities, curr, currActivities, null, Options);

        var change = Assert.Single(events);
        Assert.Equal(MapTransitionKind.StateChanged, change.Kind);
        Assert.Equal("a", change.ItemId);
        Assert.Contains("Queued", change.Detail);
        Assert.Contains("Working", change.Detail);
    }

    [Fact]
    public void DependencyLanding_EmitsUnblocked()
    {
        var (prev, prevActivities) = Analyze([
            Fixtures.Item("parent", state: "Working"),
            Fixtures.Item("child", state: "Queued", dependsOn: ["parent"]),
        ]);
        Assert.Equal(ActivityKind.BlockedByDependency, prevActivities["child"].Kind);

        var (curr, currActivities) = Analyze([
            Fixtures.Item("parent", state: "Done"),
            Fixtures.Item("child", state: "Queued", dependsOn: ["parent"]),
        ]);

        var events = MapTransitionDetector.Detect(
            prev, prevActivities, curr, currActivities, null, Options);

        Assert.Contains(events, e => e.Kind == MapTransitionKind.Unblocked && e.ItemId == "child");
    }

    [Fact]
    public void ChainCompletion_FiresWhenEveryMemberResolves()
    {
        var prevItems = new List<AdminWorkItem>
        {
            Fixtures.Item("c1", state: "Working"),
            Fixtures.Item("c2", state: "Queued", dependsOn: ["c1"]),
            Fixtures.Item("other", state: "Working"),
        };
        var currItems = new List<AdminWorkItem>
        {
            Fixtures.Item("c1", state: "Done"),
            Fixtures.Item("c2", state: "Done", dependsOn: ["c1"]),
            Fixtures.Item("other", state: "Working"),
        };
        var (prev, prevActivities) = Analyze(prevItems);
        var (curr, currActivities) = Analyze(currItems);
        var chains = ChainGrouping.BuildChains(currItems).ToList();
        var chainId = chains.Single(c => c.ItemIds.Contains("c1")).Id;

        var events = MapTransitionDetector.Detect(
            prev, prevActivities, curr, currActivities, chains, Options);

        var completion = Assert.Single(events, e => e.Kind == MapTransitionKind.ChainCompleted);
        Assert.Equal(chainId, completion.ChainId);
        Assert.DoesNotContain(events, e =>
            e.Kind == MapTransitionKind.ChainCompleted && e.ChainId != chainId);
    }

    [Fact]
    public void PartialChainCompletion_FiresNothing()
    {
        var (prev, prevActivities) = Analyze([
            Fixtures.Item("c1", state: "Done"),
            Fixtures.Item("c2", state: "Working", dependsOn: ["c1"]),
        ]);
        var (curr, currActivities) = Analyze([
            Fixtures.Item("c1", state: "Done"),
            Fixtures.Item("c2", state: "Working", dependsOn: ["c1"]),
        ]);
        var chains = ChainGrouping.BuildChains(currItemsForChains()).ToList();

        var events = MapTransitionDetector.Detect(
            prev, prevActivities, curr, currActivities, chains, Options);

        Assert.DoesNotContain(events, e => e.Kind == MapTransitionKind.ChainCompleted);

        static List<AdminWorkItem> currItemsForChains() =>
        [
            Fixtures.Item("c1", state: "Done"),
            Fixtures.Item("c2", state: "Working", dependsOn: ["c1"]),
        ];
    }
}

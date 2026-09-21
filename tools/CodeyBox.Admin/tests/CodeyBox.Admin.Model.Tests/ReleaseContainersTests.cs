using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// Releases as containers: a release's lanes are laid out together so its
/// frame is one box around its members; the frame carries honest counts;
/// unreleased items are in no container; empty or shipped releases fold.
/// </summary>
public sealed class ReleaseContainersTests
{
    private static readonly FleetMapOptions Options = new();

    private static AdminWorkItem In(string id, string? release, string state = "Queued", IReadOnlyList<string>? dependsOn = null) =>
        Fixtures.Item(id, state: state, dependsOn: dependsOn) with { ReleaseId = release };

    [Fact]
    public void ReleaseLanes_AreContiguous_AndUnreleasedWorkIsNotInAnyFrame()
    {
        // Two chains and a singleton in release R, interleaved (by creation order) with unreleased work.
        var items = new List<AdminWorkItem>
        {
            In("u1", null), In("u2", null, dependsOn: ["u1"]),
            In("r1", "R"), In("r2", "R", dependsOn: ["r1"]),
            In("u3", null),
            In("r3", "R"), In("r4", "R", dependsOn: ["r3"]),
            In("r5", "R"),
        };
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options);

        var releaseYs = new[] { "r1", "r2", "r3", "r4", "r5" }.Select(id => layout.Nodes[id].Y).ToList();
        var otherYs = new[] { "u1", "u2", "u3" }.Select(id => layout.Nodes[id].Y).ToList();
        Assert.True(releaseYs.Max() < otherYs.Min(), "release lanes come first and are contiguous");
        Assert.Equal(FleetMapBuilder.LooseLaneFor("R"), layout.Nodes["r5"].LaneId);
        Assert.Equal(FleetMapBuilder.LooseLaneId, layout.Nodes["u3"].LaneId);

        var frames = ReleaseContainers.Build(
            [new ReleaseInfo { Id = "R", Name = "Release one", State = "Open", BlockingFindings = 2, RemediationItemIds = ["u3"] }],
            items.ToDictionary(i => i.Id, i => i.ReleaseId, StringComparer.Ordinal),
            items.ToDictionary(i => i.Id, i => i.State, StringComparer.Ordinal),
            layout, Options);

        var frame = Assert.Single(frames);
        Assert.Equal("Release one", frame.Name);
        Assert.Equal(5, frame.Total);
        Assert.Equal(0, frame.Done);
        Assert.Equal(2, frame.Blocking);
        Assert.Equal(["u3"], frame.RemediationOnMap);
        foreach (var id in new[] { "r1", "r2", "r3", "r4", "r5" })
        {
            var n = layout.Nodes[id];
            Assert.InRange(n.X, frame.X, frame.X + frame.W);
            Assert.InRange(n.Y, frame.Y, frame.Y + frame.H);
        }
        foreach (var id in new[] { "u1", "u2", "u3" })
        {
            Assert.False(layout.Nodes[id].Y > frame.Y && layout.Nodes[id].Y < frame.Y + frame.H, $"{id} is unreleased and must not sit inside the frame");
        }
    }

    [Fact]
    public void Counts_IncludeMembersNotOnTheMap_AndDoneIsSuccessOnly()
    {
        var onMap = new List<AdminWorkItem> { In("a", "R", "Working"), In("b", "R", "Done") };
        var layout = FleetMapBuilder.DeriveInitial(onMap, ChainGrouping.BuildChains(onMap).ToList(), Options);
        var releaseByItem = new Dictionary<string, string?>(StringComparer.Ordinal) { ["a"] = "R", ["b"] = "R", ["c-folded"] = "R", ["d-cancelled"] = "R" };
        var stateById = new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "Working", ["b"] = "Done", ["c-folded"] = "Done", ["d-cancelled"] = "Cancelled" };

        var frame = Assert.Single(ReleaseContainers.Build([new ReleaseInfo { Id = "R", Name = "R", State = "Open" }], releaseByItem, stateById, layout, Options));

        Assert.Equal(4, frame.Total);
        Assert.Equal(2, frame.Done);
        Assert.Equal(["a", "b"], frame.MemberIds);
    }

    [Fact]
    public void EmptyOrShippedReleases_Fold_OpenOnesWithSettledMembersStay()
    {
        var items = new List<AdminWorkItem> { In("a", "shipped", "Done"), In("b", "open-done", "Done") };
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options);
        var releaseByItem = items.ToDictionary(i => i.Id, i => i.ReleaseId, StringComparer.Ordinal);
        var stateById = items.ToDictionary(i => i.Id, i => i.State, StringComparer.Ordinal);

        var frames = ReleaseContainers.Build(
        [
            new ReleaseInfo { Id = "shipped", Name = "Shipped", State = "Released" },
            new ReleaseInfo { Id = "open-done", Name = "Still open", State = "Open" },
            new ReleaseInfo { Id = "empty", Name = "Nothing in it", State = "Open" },
        ], releaseByItem, stateById, layout, Options);

        Assert.Equal(["open-done"], frames.Select(f => f.Id).ToList());
    }

    [Fact]
    public void NewChainInARelease_JoinsItsReleasesLanes_NotTheBottom()
    {
        var items = new List<AdminWorkItem> { In("r1", "R"), In("r2", "R", dependsOn: ["r1"]), In("u1", null), In("u2", null, dependsOn: ["u1"]) };
        var layout = FleetMapBuilder.DeriveInitial(items, ChainGrouping.BuildChains(items).ToList(), Options);

        var grown = items.Concat([In("r3", "R"), In("r4", "R", dependsOn: ["r3"])]).ToList();
        var refreshed = FleetMapBuilder.Update(layout, grown, ChainGrouping.BuildChains(grown).ToList(), Options);

        Assert.True(refreshed.Nodes["r3"].Y < refreshed.Nodes["u1"].Y, "the new release chain sits with its release, above the unreleased lanes");
        Assert.Equal(layout.Nodes["r1"], refreshed.Nodes["r1"]);
    }
}

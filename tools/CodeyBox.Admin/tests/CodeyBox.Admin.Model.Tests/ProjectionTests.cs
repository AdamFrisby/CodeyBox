using CodeyBox.Admin.Model;

namespace CodeyBox.Admin.Model.Tests;

/// <summary>
/// End-to-end projection over one mixed snapshot: determinism plus one
/// assertion per layer so the facade cannot silently drop a section.
/// </summary>
public sealed class ProjectionTests
{
    [Fact]
    public void SameSnapshot_ProducesSameScreen()
    {
        var snapshot = MixedSnapshot();

        var first = FleetProjectionBuilder.Project(snapshot);
        var second = FleetProjectionBuilder.Project(snapshot);

        Assert.Equal(first.Chains.Count, second.Chains.Count);
        Assert.Equal(
            first.Attention.Select(a => (a.ItemId, a.Score)),
            second.Attention.Select(a => (a.ItemId, a.Score)));
        Assert.Equal(
            first.Vitals.Select(v => (v.Name, v.Value, v.Band)),
            second.Vitals.Select(v => (v.Name, v.Value, v.Band)));
        Assert.Equal(
            first.Activities.ToDictionary(k => k.Key, v => v.Value.Kind),
            second.Activities.ToDictionary(k => k.Key, v => v.Value.Kind));
    }

    [Fact]
    public void Projection_CoversEveryItem()
    {
        var projection = FleetProjectionBuilder.Project(MixedSnapshot());

        Assert.Equal(5, projection.Activities.Count);
        Assert.Equal(5, projection.Attention.Count);
        Assert.NotEmpty(projection.Chains);
        Assert.Equal(5, projection.Vitals.Count);
        Assert.NotEmpty(projection.ChainAttention);
        Assert.Equal(
            projection.Chains.Sum(c => c.ItemIds.Count),
            projection.Attention.Count);
    }

    private static FleetSnapshot MixedSnapshot() => Fixtures.Snapshot(
        [
            Fixtures.Item("vfy-1", title: "Deployment verification 1/3", state: "Done"),
            Fixtures.Item("vfy-2", title: "Deployment verification 2/3", dependsOn: ["vfy-1"]),
            Fixtures.Item("stuck", state: "WaitingForAgentResume"),
            Fixtures.Item("solo", state: "Working"),
            Fixtures.Item("oops", state: "AuditFailed"),
        ],
        history: Fixtures.FlatHistory());
}

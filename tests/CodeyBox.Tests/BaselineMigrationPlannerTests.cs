using CodeyBox.Core;

namespace CodeyBox.Tests;

/// <summary>
/// Unit tests for the pure <see cref="BaselineMigrationPlanner"/> decision core.
/// Exercises the exact rules an operator baseline migration must obey: clear a
/// pin only when it differs from the project's current-config ref, skip terminal
/// items and items already on the current baseline, honour the filter, and
/// summarise the recompute targets.
/// </summary>
public sealed class BaselineMigrationPlannerTests
{
    private static readonly ProjectId ProjA = new("proj-a");
    private static readonly ProjectId ProjB = new("proj-b");

    private static BaselinePinnedWorkItem Item(
        string pin, WorkItemState state = WorkItemState.Working, ProjectId? project = null) =>
        new(WorkItemId.New(), project ?? ProjA, state, pin);

    [Fact]
    public void ClearsPinThatDiffersFromCurrent_AndReportsRecomputeTarget()
    {
        var item = Item("cb-baseline-old");
        var current = new Dictionary<ProjectId, string?> { [ProjA] = "cb-baseline-new" };

        var plan = BaselineMigrationPlanner.Plan([item], default, current);

        Assert.Single(plan.ItemIdsToClear);
        Assert.Equal(item.Id, plan.ItemIdsToClear[0]);
        var target = Assert.Single(plan.RecomputeTargets);
        Assert.Equal("cb-baseline-new", target.BaselineImageRef);
        Assert.Equal(1, target.Count);
    }

    [Fact]
    public void SkipsItemAlreadyOnCurrentBaseline()
    {
        var item = Item("cb-baseline-current");
        var current = new Dictionary<ProjectId, string?> { [ProjA] = "cb-baseline-current" };

        var plan = BaselineMigrationPlanner.Plan([item], default, current);

        Assert.Empty(plan.ItemIdsToClear);
        Assert.Empty(plan.RecomputeTargets);
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.MergeConflictResolutionFailed)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    public void SkipsTerminalItems(WorkItemState terminal)
    {
        var item = Item("cb-baseline-old", terminal);
        var current = new Dictionary<ProjectId, string?> { [ProjA] = "cb-baseline-new" };

        var plan = BaselineMigrationPlanner.Plan([item], default, current);

        Assert.Empty(plan.ItemIdsToClear);
    }

    [Fact]
    public void RespectsProjectFilter()
    {
        var a = Item("cb-baseline-old", project: ProjA);
        var b = Item("cb-baseline-old", project: ProjB);
        var current = new Dictionary<ProjectId, string?>
        {
            [ProjA] = "cb-baseline-new-a",
            [ProjB] = "cb-baseline-new-b",
        };

        var plan = BaselineMigrationPlanner.Plan(
            [a, b], new BaselineMigrationFilter(ProjectId: ProjB), current);

        Assert.Equal([b.Id], plan.ItemIdsToClear);
    }

    [Fact]
    public void RespectsBaselineRefFilter()
    {
        var oldPin = Item("cb-baseline-old");
        var otherPin = Item("cb-baseline-other");
        var current = new Dictionary<ProjectId, string?> { [ProjA] = "cb-baseline-new" };

        var plan = BaselineMigrationPlanner.Plan(
            [oldPin, otherPin],
            new BaselineMigrationFilter(BaselineImageRef: "cb-baseline-old"),
            current);

        Assert.Equal([oldPin.Id], plan.ItemIdsToClear);
    }

    [Fact]
    public void MissingProjectTreatedAsNoCurrentBaseline_MigratesToNullTarget()
    {
        var item = Item("cb-baseline-old"); // ProjA not present in the map
        var plan = BaselineMigrationPlanner.Plan(
            [item], default, new Dictionary<ProjectId, string?>());

        Assert.Single(plan.ItemIdsToClear);
        var target = Assert.Single(plan.RecomputeTargets);
        Assert.Null(target.BaselineImageRef);
        Assert.Equal(1, target.Count);
    }

    [Fact]
    public void GroupsRecomputeTargetsByRef_OrderedByCountDescending()
    {
        var current = new Dictionary<ProjectId, string?>
        {
            [ProjA] = "cb-baseline-a",
            [ProjB] = "cb-baseline-b",
        };
        var items = new[]
        {
            Item("cb-baseline-old", project: ProjA),
            Item("cb-baseline-old", project: ProjA),
            Item("cb-baseline-old", project: ProjB),
        };

        var plan = BaselineMigrationPlanner.Plan(items, default, current);

        Assert.Equal(3, plan.ItemIdsToClear.Count);
        Assert.Equal(2, plan.RecomputeTargets.Count);
        Assert.Equal("cb-baseline-a", plan.RecomputeTargets[0].BaselineImageRef);
        Assert.Equal(2, plan.RecomputeTargets[0].Count);
        Assert.Equal("cb-baseline-b", plan.RecomputeTargets[1].BaselineImageRef);
        Assert.Equal(1, plan.RecomputeTargets[1].Count);
    }

    [Fact]
    public void IgnoresEmptyPins()
    {
        var item = Item("");
        var plan = BaselineMigrationPlanner.Plan(
            [item], default, new Dictionary<ProjectId, string?> { [ProjA] = "cb-baseline-new" });

        Assert.Empty(plan.ItemIdsToClear);
    }
}

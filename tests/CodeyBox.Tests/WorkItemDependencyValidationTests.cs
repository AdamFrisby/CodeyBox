using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Tests;

/// <summary>
/// Tests for <see cref="WorkItemDependencies"/> cycle detection and
/// dependency satisfaction logic.
/// </summary>
public sealed class WorkItemDependencyValidationTests
{
    private static WorkItem Item(WorkItemId id, WorkItemState state = WorkItemState.Queued, params WorkItemId[] deps) => new()
    {
        Id = id,
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = state,
        DependsOn = deps,
    };

    // ── AreSatisfied ─────────────────────────────────────────────────────────

    [Fact]
    public void AreSatisfied_NoDependencies_ReturnsTrue()
    {
        var states = new Dictionary<WorkItemId, WorkItemState>();
        Assert.True(WorkItemDependencies.AreSatisfied([], states));
    }

    [Fact]
    public void AreSatisfied_AllDepsTerminal_ReturnsTrue()
    {
        var dep1 = WorkItemId.New();
        var dep2 = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState>
        {
            [dep1] = WorkItemState.Done,
            [dep2] = WorkItemState.Failed,
        };
        Assert.True(WorkItemDependencies.AreSatisfied([dep1, dep2], states));
    }

    [Fact]
    public void AreSatisfied_OneDepsNonTerminal_ReturnsFalse()
    {
        var dep1 = WorkItemId.New();
        var dep2 = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState>
        {
            [dep1] = WorkItemState.Done,
            [dep2] = WorkItemState.Working,
        };
        Assert.False(WorkItemDependencies.AreSatisfied([dep1, dep2], states));
    }

    [Fact]
    public void AreSatisfied_DepNotInMap_ReturnsFalse()
    {
        var dep = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState>(); // empty
        Assert.False(WorkItemDependencies.AreSatisfied([dep], states));
    }

    [Theory]
    [InlineData(WorkItemState.Done)]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.Cancelled)]
    public void AreSatisfied_EachTerminalState_ReturnsTrue(WorkItemState terminal)
    {
        var dep = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState> { [dep] = terminal };
        Assert.True(WorkItemDependencies.AreSatisfied([dep], states));
    }

    [Theory]
    [InlineData(WorkItemState.Queued)]
    [InlineData(WorkItemState.Working)]
    [InlineData(WorkItemState.WorkComplete)]
    [InlineData(WorkItemState.Merging)]
    [InlineData(WorkItemState.Merged)]
    [InlineData(WorkItemState.UpstreamPushing)]
    [InlineData(WorkItemState.Auditing)]
    [InlineData(WorkItemState.Reworking)]
    [InlineData(WorkItemState.AuditPassed)]
    public void AreSatisfied_NonTerminalState_ReturnsFalse(WorkItemState nonTerminal)
    {
        var dep = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState> { [dep] = nonTerminal };
        Assert.False(WorkItemDependencies.AreSatisfied([dep], states));
    }

    // ── FindCycle ─────────────────────────────────────────────────────────────

    [Fact]
    public void FindCycle_NoDependencies_ReturnsNull()
    {
        var newId = WorkItemId.New();
        var result = WorkItemDependencies.FindCycle(newId, [], []);
        Assert.Null(result);
    }

    [Fact]
    public void FindCycle_LinearChain_NoCycle()
    {
        // a → b → c (linear DAG), new item d → c — no cycle
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var c = WorkItemId.New();
        var d = WorkItemId.New();

        var existing = new List<WorkItem>
        {
            Item(a),
            Item(b, WorkItemState.Queued, a),
            Item(c, WorkItemState.Queued, b),
        };

        var result = WorkItemDependencies.FindCycle(d, [c], existing);
        Assert.Null(result);
    }

    [Fact]
    public void FindCycle_SelfLoop_ReturnsCyclePath()
    {
        var newId = WorkItemId.New();
        // Passing newId as its own dep — self-dependency.
        var result = WorkItemDependencies.FindCycle(newId, [newId], []);
        Assert.NotNull(result);
        Assert.Contains(newId.ToString(), result);
    }

    [Fact]
    public void FindCycle_ThreeItemCycle_ReturnsCyclePath()
    {
        // Simulate a corrupted store with a→b→c→a.
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var c = WorkItemId.New();

        // Build a graph where a depends on b, b depends on c, and we're
        // trying to add something where c would depend on a (completing the
        // cycle). We inject this by making "c" the new item with dep on a.
        var existing = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued, b),
            Item(b, WorkItemState.Queued, c),
        };

        // New item is c, depending on a — would create c→a→b→c.
        var result = WorkItemDependencies.FindCycle(c, [a], existing);
        Assert.NotNull(result);
    }

    [Fact]
    public void FindCycle_DiamondDag_NoCycle()
    {
        // Diamond: d depends on b and c; b and c both depend on a.
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var c = WorkItemId.New();
        var d = WorkItemId.New();

        var existing = new List<WorkItem>
        {
            Item(a),
            Item(b, WorkItemState.Queued, a),
            Item(c, WorkItemState.Queued, a),
        };

        var result = WorkItemDependencies.FindCycle(d, [b, c], existing);
        Assert.Null(result);
    }

    // ── FindCascadeCancelTargets ──────────────────────────────────────────────

    [Fact]
    public void FindCascadeCancelTargets_NoDirectDependents_ReturnsEmpty()
    {
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var allItems = new List<WorkItem> { Item(a), Item(b) };

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a, allItems);
        Assert.Empty(targets);
    }

    [Fact]
    public void FindCascadeCancelTargets_QueuedDirectDependent_IsIncluded()
    {
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            Item(b, WorkItemState.Queued, a),
        };

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a, allItems);
        Assert.Single(targets);
        Assert.Equal(b, targets[0].Id);
    }

    [Fact]
    public void FindCascadeCancelTargets_WorkingDependent_IsExcluded()
    {
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            Item(b, WorkItemState.Working, a), // in-flight: excluded
        };

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a, allItems);
        Assert.Empty(targets);
    }

    [Fact]
    public void FindCascadeCancelTargets_TransitiveQueuedDependents_AllIncluded()
    {
        // a → b → c (all Queued); cancel a should cascade to b and c.
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var c = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            Item(b, WorkItemState.Queued, a),
            Item(c, WorkItemState.Queued, b),
        };

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a, allItems);
        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.Id == b);
        Assert.Contains(targets, t => t.Id == c);
    }

    [Fact]
    public void FindCascadeCancelTargets_MixedStates_OnlyQueuedCascaded()
    {
        // a → b (Queued), a → c (Working). Cancel a: only b cascaded.
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var c = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            Item(b, WorkItemState.Queued, a),
            Item(c, WorkItemState.Working, a),
        };

        var targets = WorkItemDependencies.FindCascadeCancelTargets(a, allItems);
        Assert.Single(targets);
        Assert.Equal(b, targets[0].Id);
    }
}

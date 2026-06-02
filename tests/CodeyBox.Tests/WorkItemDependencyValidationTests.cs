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
    public void AreSatisfied_AllDepsDone_ReturnsTrue()
    {
        var dep1 = WorkItemId.New();
        var dep2 = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState>
        {
            [dep1] = WorkItemState.Done,
            [dep2] = WorkItemState.Done,
        };
        Assert.True(WorkItemDependencies.AreSatisfied([dep1, dep2], states));
    }

    [Fact]
    public void AreSatisfied_OneDepInProgress_ReturnsFalse()
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
    public void AreSatisfied_OneDepFailed_ReturnsFalse()
    {
        // Failed deps block the gate — see SatisfyingStates.
        var dep1 = WorkItemId.New();
        var dep2 = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState>
        {
            [dep1] = WorkItemState.Done,
            [dep2] = WorkItemState.Failed,
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

    [Fact]
    public void AreSatisfied_DoneDep_ReturnsTrue()
    {
        var dep = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState> { [dep] = WorkItemState.Done };
        Assert.True(WorkItemDependencies.AreSatisfied([dep], states));
    }

    [Theory]
    [InlineData(WorkItemState.Failed)]
    [InlineData(WorkItemState.AuditFailed)]
    [InlineData(WorkItemState.Cancelled)]
    [InlineData(WorkItemState.MergeConflictResolutionFailed)]
    [InlineData(WorkItemState.AbandonedAfterRecoveryAttempts)]
    public void AreSatisfied_NonSuccessTerminalState_ReturnsFalse(WorkItemState terminal)
    {
        // Terminal but not Done — dependent must wait for an operator-driven
        // retry-and-resolve of the parent.
        var dep = WorkItemId.New();
        var states = new Dictionary<WorkItemId, WorkItemState> { [dep] = terminal };
        Assert.False(WorkItemDependencies.AreSatisfied([dep], states));
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

    // ── FindMissingDependency ─────────────────────────────────────────────────

    [Fact]
    public void FindMissingDependency_EmptyDeps_ReturnsNull()
    {
        var result = WorkItemDependencies.FindMissingDependency([], []);
        Assert.Null(result);
    }

    [Fact]
    public void FindMissingDependency_AllDepsExist_ReturnsNull()
    {
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var existing = new List<WorkItem> { Item(a), Item(b) };
        var result = WorkItemDependencies.FindMissingDependency([a, b], existing);
        Assert.Null(result);
    }

    [Fact]
    public void FindMissingDependency_OneMissing_ReturnsMissingId()
    {
        var a = WorkItemId.New();
        var missing = WorkItemId.New();
        var existing = new List<WorkItem> { Item(a) };
        var result = WorkItemDependencies.FindMissingDependency([a, missing], existing);
        Assert.Equal(missing, result);
    }

    [Fact]
    public void FindMissingDependency_AllMissing_ReturnsFirst()
    {
        var first = WorkItemId.New();
        var second = WorkItemId.New();
        var result = WorkItemDependencies.FindMissingDependency([first, second], []);
        Assert.Equal(first, result);
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

    // ── FindDescendantsToRestore (inverse of cascade-cancel) ──────────────────

    private static WorkItem CascadedItem(WorkItemId id, params WorkItemId[] deps) => new()
    {
        Id = id,
        ProjectId = new ProjectId("test-project"),
        Title = "t",
        Prompt = "p",
        State = WorkItemState.Cancelled,
        CancellationReason = WorkItemCancellationReason.ParentCascaded,
        LastError = "parent dependency cancelled",
        DependsOn = deps,
    };

    [Fact]
    public void FindDescendantsToRestore_NoCancelledDependents_ReturnsEmpty()
    {
        var a = WorkItemId.New();
        var allItems = new List<WorkItem> { Item(a, WorkItemState.Queued) };

        var restore = WorkItemDependencies.FindDescendantsToRestore(a, allItems);
        Assert.Empty(restore);
    }

    [Fact]
    public void FindDescendantsToRestore_DirectCascadedDependent_IsIncluded()
    {
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            CascadedItem(b, a),
        };

        var restore = WorkItemDependencies.FindDescendantsToRestore(a, allItems);
        Assert.Single(restore);
        Assert.Equal(b, restore[0].Id);
    }

    [Fact]
    public void FindDescendantsToRestore_TransitiveCascade_AllIncluded()
    {
        // a (head, being recovered) → b → c, both b and c were cascade-cancelled
        // because of a. Both should be restored.
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var c = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            CascadedItem(b, a),
            CascadedItem(c, b),
        };

        var restore = WorkItemDependencies.FindDescendantsToRestore(a, allItems);
        Assert.Equal(2, restore.Count);
        Assert.Contains(restore, r => r.Id == b);
        Assert.Contains(restore, r => r.Id == c);
    }

    [Fact]
    public void FindDescendantsToRestore_OperatorCancelledDescendant_IsExcluded()
    {
        // An operator-cancelled descendant must NOT be quietly resurrected by
        // a watchdog recovery. Only ParentCascaded items are restored.
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var operatorCancelled = new WorkItem
        {
            Id = b,
            ProjectId = new ProjectId("test-project"),
            Title = "t",
            Prompt = "p",
            State = WorkItemState.Cancelled,
            CancellationReason = WorkItemCancellationReason.OperatorRequested,
            DependsOn = [a],
        };
        var allItems = new List<WorkItem> { Item(a, WorkItemState.Queued), operatorCancelled };

        var restore = WorkItemDependencies.FindDescendantsToRestore(a, allItems);
        Assert.Empty(restore);
    }

    [Fact]
    public void FindDescendantsToRestore_DescendantBlockedByOtherFailure_IsExcluded()
    {
        // c depends on both a (recovered) and b (Failed). Even though c was
        // cascade-cancelled because of a, restoring c would let it run against
        // a genuinely failed prerequisite — leave it parked.
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var c = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            Item(b, WorkItemState.Failed),
            CascadedItem(c, a, b),
        };

        var restore = WorkItemDependencies.FindDescendantsToRestore(a, allItems);
        Assert.Empty(restore);
    }

    [Fact]
    public void FindDescendantsToRestore_DescendantWithSatisfyingSiblingDep_IsIncluded()
    {
        // c depends on a (recovered) and b (Done). b satisfies its half of the
        // gate already, so once a is back to Queued, c is restorable.
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var c = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            Item(b, WorkItemState.Done),
            CascadedItem(c, a, b),
        };

        var restore = WorkItemDependencies.FindDescendantsToRestore(a, allItems);
        Assert.Single(restore);
        Assert.Equal(c, restore[0].Id);
    }

    [Fact]
    public void FindDescendantsToRestore_TransitiveChainWithBlockedSibling_PartialRestore()
    {
        // a (recovered) → b cascaded; c depends on b and on an independently
        // Failed item. b is restorable; c is not (b is being restored to
        // Queued, not Done, so c would have to wait — but its OTHER dependency
        // is Failed and blocks it permanently regardless).
        var a = WorkItemId.New();
        var b = WorkItemId.New();
        var failed = WorkItemId.New();
        var c = WorkItemId.New();
        var allItems = new List<WorkItem>
        {
            Item(a, WorkItemState.Queued),
            CascadedItem(b, a),
            Item(failed, WorkItemState.Failed),
            CascadedItem(c, b, failed),
        };

        var restore = WorkItemDependencies.FindDescendantsToRestore(a, allItems);
        Assert.Single(restore);
        Assert.Equal(b, restore[0].Id);
    }
}

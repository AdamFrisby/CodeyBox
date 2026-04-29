using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure static helpers for dependency-graph operations. All methods are
/// deterministic over their inputs and carry no side effects.
/// </summary>
public static class WorkItemDependencies
{
    /// <summary>
    /// States that count as "satisfied" for dependency gating. A dependent
    /// item may be picked up once ALL of its DependsOn items are in one of
    /// these states, regardless of which terminal state they reached — if a
    /// dependency fails, its dependents still become eligible so an operator
    /// can retry the parent and the dependents will run automatically.
    /// </summary>
    public static readonly IReadOnlySet<WorkItemState> TerminalStates =
        new HashSet<WorkItemState>
        {
            WorkItemState.Done,
            WorkItemState.Failed,
            WorkItemState.AuditFailed,
            WorkItemState.Cancelled,
        };

    /// <summary>
    /// Returns true iff every ID in <paramref name="dependsOn"/> maps to a
    /// terminal state in <paramref name="statesById"/>.
    /// </summary>
    public static bool AreSatisfied(
        IReadOnlyList<WorkItemId> dependsOn,
        IReadOnlyDictionary<WorkItemId, WorkItemState> statesById)
    {
        foreach (var id in dependsOn)
        {
            if (!statesById.TryGetValue(id, out var state) || !TerminalStates.Contains(state))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Runs DFS cycle detection over the proposed graph (all existing items
    /// plus the hypothetical new item <paramref name="newId"/> with deps
    /// <paramref name="newDeps"/>). Returns a human-readable cycle path string
    /// like "a -> b -> c -> a" if a cycle would be introduced, or null if the
    /// graph remains acyclic.
    ///
    /// By construction, existing items cannot depend on the new item (it
    /// doesn't exist yet), so a cycle is mathematically impossible. This
    /// method is a safety net for DB corruption or future code changes that
    /// might violate that invariant.
    /// </summary>
    public static string? FindCycle(
        WorkItemId newId,
        IReadOnlyList<WorkItemId> newDeps,
        IReadOnlyList<WorkItem> allExistingItems)
    {
        // Build adjacency list: node -> its direct dependencies.
        var adj = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var item in allExistingItems)
            adj[item.Id.ToString()] = item.DependsOn.Select(d => d.ToString()).ToList();
        adj[newId.ToString()] = newDeps.Select(d => d.ToString()).ToList();

        var visited = new HashSet<string>();
        var onStack = new HashSet<string>();
        var stackPath = new List<string>(); // tracks current DFS path for cycle reporting

        string? cyclePath = null;

        bool Visit(string node)
        {
            if (onStack.Contains(node))
            {
                // Found back-edge: reconstruct the cycle segment.
                var cycleStart = stackPath.IndexOf(node);
                var cycle = stackPath.Skip(cycleStart).ToList();
                cycle.Add(node);
                cyclePath = string.Join(" -> ", cycle);
                return true;
            }
            if (visited.Contains(node)) return false;

            visited.Add(node);
            onStack.Add(node);
            stackPath.Add(node);

            if (adj.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    if (Visit(dep)) return true;
                }
            }

            stackPath.RemoveAt(stackPath.Count - 1);
            onStack.Remove(node);
            return false;
        }

        foreach (var node in adj.Keys)
        {
            if (!visited.Contains(node) && Visit(node))
                return cyclePath;
        }

        return null;
    }

    /// <summary>
    /// Returns all Queued items from <paramref name="allItems"/> whose
    /// DependsOn includes <paramref name="completedId"/> and whose every
    /// dependency is now in a terminal state per <paramref name="statesById"/>.
    /// These items are ready to be enqueued for processing.
    /// </summary>
    public static IEnumerable<WorkItem> FindSatisfiedDependents(
        WorkItemId completedId,
        IReadOnlyList<WorkItem> allItems,
        IReadOnlyDictionary<WorkItemId, WorkItemState> statesById)
    {
        foreach (var item in allItems)
        {
            if (item.State != WorkItemState.Queued) continue;
            if (!item.DependsOn.Contains(completedId)) continue;
            if (AreSatisfied(item.DependsOn, statesById))
                yield return item;
        }
    }

    /// <summary>
    /// Returns all Queued items that transitively depend on
    /// <paramref name="cancelledId"/> — i.e., items that should be
    /// cascade-cancelled when their ancestor is cancelled. In-flight
    /// items (any state other than Queued) are excluded; they run their
    /// course independently.
    /// </summary>
    public static IReadOnlyList<WorkItem> FindCascadeCancelTargets(
        WorkItemId cancelledId,
        IReadOnlyList<WorkItem> allItems)
    {
        // BFS over the dependency graph (following edges in reverse:
        // find items whose DependsOn contains an already-cancelled node).
        var toVisit = new Queue<WorkItemId>();
        toVisit.Enqueue(cancelledId);
        var visited = new HashSet<WorkItemId> { cancelledId };
        var targets = new List<WorkItem>();

        while (toVisit.Count > 0)
        {
            var parentId = toVisit.Dequeue();
            foreach (var item in allItems)
            {
                if (item.State != WorkItemState.Queued) continue;
                if (!item.DependsOn.Contains(parentId)) continue;
                if (!visited.Add(item.Id)) continue;

                targets.Add(item);
                toVisit.Enqueue(item.Id);
            }
        }

        return targets;
    }

    /// <summary>
    /// Builds a state-by-id lookup from a flat item list.
    /// </summary>
    public static Dictionary<WorkItemId, WorkItemState> BuildStateMap(IReadOnlyList<WorkItem> items)
        => items.ToDictionary(i => i.Id, i => i.State);
}

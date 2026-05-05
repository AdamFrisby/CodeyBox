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
            WorkItemState.AbandonedAfterRecoveryAttempts,
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
    /// Runs DFS cycle detection starting from the proposed new item
    /// (<paramref name="newId"/> with edges to <paramref name="newDeps"/>)
    /// over the combined graph of all existing items. Returns a human-readable
    /// cycle path string like "a -> b -> c -> a" if a cycle is reachable from
    /// <paramref name="newId"/>, or null if the graph remains acyclic.
    ///
    /// Uses an iterative DFS with an explicit stack to avoid unbounded call-stack
    /// growth on long dependency chains (StackOverflowException risk with recursion).
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
        var stackPath = new List<string>();
        // Each frame: (node, next-neighbor-index-to-visit).
        var dfsStack = new Stack<(string Node, int NeighborIdx)>();

        var start = newId.ToString();
        visited.Add(start);
        onStack.Add(start);
        stackPath.Add(start);
        dfsStack.Push((start, 0));

        while (dfsStack.Count > 0)
        {
            var (node, idx) = dfsStack.Peek();
            adj.TryGetValue(node, out var neighbors);

            if (neighbors is null || idx >= neighbors.Count)
            {
                // All neighbors of this node processed — backtrack.
                dfsStack.Pop();
                onStack.Remove(node);
                stackPath.RemoveAt(stackPath.Count - 1);
                continue;
            }

            // Advance the neighbor index for this frame before pushing.
            dfsStack.Pop();
            dfsStack.Push((node, idx + 1));

            var neighbor = neighbors[idx];

            if (onStack.Contains(neighbor))
            {
                // Back-edge found: reconstruct the cycle segment.
                var cycleStart = stackPath.IndexOf(neighbor);
                var cycle = stackPath.Skip(cycleStart).Append(neighbor).ToList();
                return string.Join(" -> ", cycle);
            }

            if (!visited.Contains(neighbor))
            {
                visited.Add(neighbor);
                onStack.Add(neighbor);
                stackPath.Add(neighbor);
                dfsStack.Push((neighbor, 0));
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the first ID in <paramref name="dependsOnIds"/> that does not
    /// appear in <paramref name="existingItems"/>, or null if every dep exists.
    /// </summary>
    public static WorkItemId? FindMissingDependency(
        IReadOnlyList<WorkItemId> dependsOnIds,
        IReadOnlyList<WorkItem> existingItems)
    {
        var existingIds = new HashSet<WorkItemId>(existingItems.Select(i => i.Id));
        foreach (var id in dependsOnIds)
        {
            if (!existingIds.Contains(id))
                return id;
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

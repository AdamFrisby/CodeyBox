using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Pure static helpers for dependency-graph operations. All methods are
/// deterministic over their inputs and carry no side effects.
/// </summary>
public static class WorkItemDependencies
{
    /// <summary>
    /// States from which a work item cannot exit without explicit operator
    /// action (retry / uncancel / resume). Used by callers that need to know
    /// "is this item terminal?" — e.g. PATCH /priority refusing to mutate a
    /// terminal row, the SSE stream closing its connection on terminal arrival.
    ///
    /// <para>
    /// Distinct from <see cref="SatisfyingStates"/>: terminal does NOT imply
    /// satisfying. A Failed dependency is terminal (the item is parked until
    /// an operator retries it) but does NOT satisfy a dependent's gate — the
    /// dependent waits until the parent reaches Done.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<WorkItemState> TerminalStates = WorkItemStates.Terminal;

    /// <summary>
    /// States that count as "satisfied" for the dependsOn gate. A dependent
    /// item may be picked up once every ID in its DependsOn maps to one of
    /// these states.
    ///
    /// <para>
    /// Only successful completion (<see cref="WorkItemState.Done"/>) satisfies
    /// the gate by default — Failed / AuditFailed / Cancelled /
    /// MergeConflictResolutionFailed / AbandonedAfterRecoveryAttempts all
    /// block. This is the conservative posture: a dependent built on a
    /// failed prerequisite cannot be validated end-to-end, so running it
    /// burns agent quota on speculative work. Operators must retry-and-
    /// resolve the failed parent (or uncancel a cascade-cancelled one)
    /// before the dependent becomes eligible.
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<WorkItemState> SatisfyingStates =
        new HashSet<WorkItemState>
        {
            WorkItemState.Done,
        };

    /// <summary>
    /// Returns true iff every ID in <paramref name="dependsOn"/> maps to a
    /// <see cref="SatisfyingStates"/> entry in <paramref name="statesById"/>.
    /// </summary>
    public static bool AreSatisfied(
        IReadOnlyList<WorkItemId> dependsOn,
        IReadOnlyDictionary<WorkItemId, WorkItemState> statesById)
    {
        foreach (var id in dependsOn)
        {
            if (!statesById.TryGetValue(id, out var state) || !SatisfyingStates.Contains(state))
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
    /// dependency now satisfies the gate per <paramref name="statesById"/>
    /// (see <see cref="SatisfyingStates"/>). These items are ready to be
    /// enqueued for processing.
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

    /// <summary>
    /// Returns every Cancelled item that was cascade-cancelled SOLELY because
    /// of <paramref name="recoveredId"/> (directly or transitively through
    /// other ParentCascaded items in the same chain). These are descendants
    /// the watchdog / recovery path should restore to Queued when the
    /// chain-head is recovered — otherwise they remain silently stranded.
    ///
    /// <para>
    /// "Solely because of <paramref name="recoveredId"/>" means: every entry
    /// in the item's <see cref="WorkItem.DependsOn"/> either equals
    /// <paramref name="recoveredId"/>, or is itself in this returned set, or
    /// is in a satisfying state (<see cref="SatisfyingStates"/>). If ANY
    /// dependency is in a terminal-but-not-satisfying state (e.g. Failed,
    /// AuditFailed, MergeConflictResolutionFailed) the item is not restored —
    /// it has a real blocker beyond the recovered parent.
    /// </para>
    ///
    /// <para>
    /// Items whose <see cref="WorkItem.CancellationReason"/> is not
    /// <see cref="WorkItemCancellationReason.ParentCascaded"/> are excluded:
    /// operator-cancelled items must not be quietly resurrected.
    /// </para>
    /// </summary>
    public static IReadOnlyList<WorkItem> FindDescendantsToRestore(
        WorkItemId recoveredId,
        IReadOnlyList<WorkItem> allItems)
    {
        var byId = allItems.ToDictionary(i => i.Id);
        var toRestore = new Dictionary<WorkItemId, WorkItem>();

        // Repeatedly scan for ParentCascaded items whose every dependency is
        // either the recovered head, an item already queued for restoration,
        // or a satisfying state. Iterate to a fixed point so a chain (A → B → C)
        // restores B once A is in the set, then restores C once B is.
        bool changed;
        do
        {
            changed = false;
            foreach (var item in allItems)
            {
                if (toRestore.ContainsKey(item.Id)) continue;
                if (item.State != WorkItemState.Cancelled) continue;
                if (item.CancellationReason != WorkItemCancellationReason.ParentCascaded) continue;
                if (item.DependsOn.Count == 0) continue;

                bool linksToRecovered = false;
                bool allDepsRestorable = true;
                foreach (var depId in item.DependsOn)
                {
                    if (depId == recoveredId)
                    {
                        linksToRecovered = true;
                        continue;
                    }
                    if (toRestore.ContainsKey(depId))
                    {
                        linksToRecovered = true;
                        continue;
                    }
                    if (!byId.TryGetValue(depId, out var dep))
                    {
                        allDepsRestorable = false;
                        break;
                    }
                    if (SatisfyingStates.Contains(dep.State)) continue;

                    // Any other state — Failed, AuditFailed, MergeConflictResolutionFailed,
                    // Cancelled (OperatorRequested), AbandonedAfterRecoveryAttempts, an
                    // unrelated still-running item, etc. — means this descendant has a
                    // blocker beyond the recovered head; leave it parked.
                    allDepsRestorable = false;
                    break;
                }

                if (linksToRecovered && allDepsRestorable)
                {
                    toRestore[item.Id] = item;
                    changed = true;
                }
            }
        } while (changed);

        return toRestore.Values.ToList();
    }
}

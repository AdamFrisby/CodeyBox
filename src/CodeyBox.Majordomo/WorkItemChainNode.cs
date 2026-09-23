using System.Collections.Immutable;
using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// One node of a <c>create_work_item_chain</c> call: the item to create plus
/// its explicit intra-chain dependency edges.
/// </summary>
/// <param name="Item">The work item to create at this position.</param>
/// <param name="DependsOnIndexes">
/// Positions of earlier nodes in the same chain that must reach a terminal
/// state before this node is dispatched. The node alone cannot see its own
/// position, so the backward-edge invariant is enforced when nodes are
/// assembled into a chain: <see cref="CreateWorkItemChainArgs"/> and
/// <see cref="MajordomoPlannedChange.CreateChain"/> both run
/// <see cref="RequireChainShape"/>, which also re-runs when either is
/// rewritten through a <c>with</c> expression. Dependencies on pre-existing
/// work items go on <see cref="NewWorkItemSpec.DependsOn"/> instead.
/// </param>
public sealed record WorkItemChainNode(
    NewWorkItemSpec Item,
    IReadOnlyList<int> DependsOnIndexes)
{
    public NewWorkItemSpec Item { get; init; } =
        Item ?? throw new ArgumentNullException(nameof(Item));

    // Backed by an ImmutableArray so the edge list validated by
    // RequireChainShape cannot be rewritten through a mutable cast.
    public IReadOnlyList<int> DependsOnIndexes { get; init; } =
        DependsOnIndexes is null
            ? throw new ArgumentNullException(nameof(DependsOnIndexes))
            : ImmutableArray.CreateRange(DependsOnIndexes);

    /// <summary>Empty-edge convenience ctor for root nodes.</summary>
    public WorkItemChainNode(NewWorkItemSpec item) : this(item, []) { }

    /// <summary>
    /// Validates an assembled chain — a non-null list of
    /// 2..<see cref="CreateWorkItemChainArgs.MaxItems"/> non-null nodes whose
    /// dependency edges each point to an earlier position exactly once, with
    /// at least one edge overall — and returns it as an immutable copy. This
    /// is the single definition of chain shape: every entry point that
    /// builds a chain (tool arguments, planned changes) must run it so a
    /// cyclic, dangling, or degenerate chain is unrepresentable in the
    /// contract.
    /// </summary>
    internal static ImmutableArray<WorkItemChainNode> RequireChainShape(
        IReadOnlyList<WorkItemChainNode>? nodes,
        string paramName)
    {
        if (nodes is null) throw new ArgumentNullException(paramName);
        if (nodes.Count is < 2 or > CreateWorkItemChainArgs.MaxItems)
            throw new ArgumentException(
                $"a chain must contain between 2 and {CreateWorkItemChainArgs.MaxItems} items; use create_work_item for a single item",
                paramName);

        var hasAnyEdge = false;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i] ?? throw new ArgumentException("chain nodes must not be null", paramName);
            var seen = new HashSet<int>();
            foreach (var edge in node.DependsOnIndexes)
            {
                if (edge < 0 || edge >= i)
                    throw new ArgumentException(
                        $"{paramName}[{i}].dependsOnIndexes entry {edge} must reference an earlier position (0..{i - 1})",
                        paramName);
                if (!seen.Add(edge))
                    throw new ArgumentException(
                        $"{paramName}[{i}].dependsOnIndexes contains duplicate edge {edge}", paramName);
                hasAnyEdge = true;
            }
        }

        if (!hasAnyEdge)
            throw new ArgumentException(
                "a chain must contain at least one dependency edge; use create_work_item for independent items",
                paramName);

        // ImmutableArray: the validated node list cannot be rewritten through
        // a mutable cast of the exposed IReadOnlyList.
        return ImmutableArray.CreateRange(nodes);
    }
}

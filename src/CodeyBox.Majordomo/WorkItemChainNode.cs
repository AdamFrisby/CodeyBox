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
/// state before this node is dispatched. Edges point strictly backwards —
/// every index must be less than the node's own position — so a dependency
/// cycle is unrepresentable by construction. Dependencies on pre-existing
/// work items go on <see cref="NewWorkItemSpec.DependsOn"/> instead.
/// </param>
public sealed record WorkItemChainNode(
    NewWorkItemSpec Item,
    IReadOnlyList<int> DependsOnIndexes)
{
    public NewWorkItemSpec Item { get; init; } =
        Item ?? throw new ArgumentNullException(nameof(Item));

    // Backed by an ImmutableArray so the edge list validated by
    // CreateWorkItemChainArgs cannot be rewritten through a mutable cast.
    public IReadOnlyList<int> DependsOnIndexes { get; init; } =
        DependsOnIndexes is null
            ? throw new ArgumentNullException(nameof(DependsOnIndexes))
            : ImmutableArray.CreateRange(DependsOnIndexes);

    /// <summary>Empty-edge convenience ctor for root nodes.</summary>
    public WorkItemChainNode(NewWorkItemSpec item) : this(item, []) { }
}

using System.Text.Json.Serialization;
using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// One planned change inside a <see cref="MajordomoChangeSet"/>. The closed
/// union mirrors the mutate vocabulary one-to-one — a change kind outside
/// this set is unrepresentable.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(CreateItem), "create_item")]
[JsonDerivedType(typeof(CreateChain), "create_chain")]
[JsonDerivedType(typeof(UpdateItem), "update_item")]
[JsonDerivedType(typeof(CancelItem), "cancel_item")]
[JsonDerivedType(typeof(CancelDependents), "cancel_dependents")]
[JsonDerivedType(typeof(RetryItem), "retry_item")]
public abstract record MajordomoPlannedChange
{
    private MajordomoPlannedChange() { }

    /// <summary>Create one work item.</summary>
    public sealed record CreateItem(NewWorkItemSpec Item) : MajordomoPlannedChange;

    /// <summary>
    /// Create a dependent chain. Nodes are ordered; each node's
    /// <see cref="WorkItemChainNode.DependsOnIndexes"/> references earlier
    /// positions in the same list. The <see cref="Nodes"/> init accessor
    /// re-runs <see cref="WorkItemChainNode.RequireChainShape"/>, so the
    /// chain-shape invariant holds even for nodes assembled without
    /// <see cref="CreateWorkItemChainArgs"/> or rewritten via <c>with</c>.
    /// </summary>
    public sealed record CreateChain(IReadOnlyList<WorkItemChainNode> Nodes) : MajordomoPlannedChange
    {
        private readonly IReadOnlyList<WorkItemChainNode> _nodes =
            WorkItemChainNode.RequireChainShape(Nodes, nameof(Nodes));

        /// <summary>Ordered chain nodes; assigning re-validates the chain shape.</summary>
        public IReadOnlyList<WorkItemChainNode> Nodes
        {
            get => _nodes;
            init => _nodes = WorkItemChainNode.RequireChainShape(value, nameof(Nodes));
        }
    }

    /// <summary>Apply a replace-set field edit to one item.</summary>
    public sealed record UpdateItem(WorkItemId Id, WorkItemPatch Patch) : MajordomoPlannedChange;

    /// <summary>Cancel one item with an operator-facing reason.</summary>
    public sealed record CancelItem(WorkItemId Id, string Reason) : MajordomoPlannedChange;

    /// <summary>
    /// Cancel every queued item that transitively depends on
    /// <paramref name="ParentId"/> — the cascade a cancel commits alongside
    /// the named item. Listed explicitly so a dry-run or proposal review sees
    /// the full blast radius, not just the item the call named.
    /// </summary>
    public sealed record CancelDependents(WorkItemId ParentId, IReadOnlyList<WorkItemId> Ids)
        : MajordomoPlannedChange;

    /// <summary>Re-queue one item resuming from a pipeline phase.</summary>
    public sealed record RetryItem(
        WorkItemId Id,
        WorkItemRetryFrom From,
        TimeSpan? WorkTimeout) : MajordomoPlannedChange;
}

/// <summary>
/// Result of every MUTATE tool, for both dry-run and real execution.
/// <see cref="Changes"/> is the change set the call produces (or, for a
/// dry-run, would produce); <see cref="AffectedItems"/> carries the concrete
/// ids a real execution touched — empty under dry-run because created items
/// have no ids yet and existing targets are already named by the changes.
/// </summary>
public sealed record MajordomoChangeSet(
    bool DryRun,
    IReadOnlyList<MajordomoPlannedChange> Changes,
    IReadOnlyList<WorkItemId> AffectedItems) : MajordomoToolResult
{
    /// <summary>
    /// How many work items <see cref="Changes"/> would mutate: one per change
    /// except a chain (its node count) and a dependent cascade (its id count).
    /// </summary>
    public int PlannedItemCount => Changes.Sum(static change => change switch
    {
        MajordomoPlannedChange.CreateChain chain => chain.Nodes.Count,
        MajordomoPlannedChange.CancelDependents dependents => dependents.Ids.Count,
        _ => 1,
    });
}

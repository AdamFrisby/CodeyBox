using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// One planned change inside a <see cref="MajordomoChangeSet"/>. The closed
/// union mirrors the mutate vocabulary one-to-one — a change kind outside
/// this set is unrepresentable.
/// </summary>
public abstract record MajordomoPlannedChange
{
    private MajordomoPlannedChange() { }

    /// <summary>Create one work item.</summary>
    public sealed record CreateItem(NewWorkItemSpec Item) : MajordomoPlannedChange;

    /// <summary>
    /// Create a dependent chain. Nodes are ordered; each node's
    /// <see cref="WorkItemChainNode.DependsOnIndexes"/> references earlier
    /// positions in the same list.
    /// </summary>
    public sealed record CreateChain(IReadOnlyList<WorkItemChainNode> Nodes) : MajordomoPlannedChange;

    /// <summary>Apply a replace-set field edit to one item.</summary>
    public sealed record UpdateItem(WorkItemId Id, WorkItemPatch Patch) : MajordomoPlannedChange;

    /// <summary>Cancel one item with an operator-facing reason.</summary>
    public sealed record CancelItem(WorkItemId Id, string Reason) : MajordomoPlannedChange;

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
    IReadOnlyList<WorkItemId> AffectedItems) : MajordomoToolResult;

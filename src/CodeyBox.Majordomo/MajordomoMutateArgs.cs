using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>Arguments for <c>create_work_item</c>: enqueue one work item.</summary>
public sealed record CreateWorkItemArgs : MajordomoMutateArgs
{
    public CreateWorkItemArgs(NewWorkItemSpec item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
    }

    /// <summary>The item to create.</summary>
    public NewWorkItemSpec Item { get; }

    public override int AffectedItemCount => 1;
}

/// <summary>
/// Arguments for <c>create_work_item_chain</c>: enqueue a batch of work items
/// as a dependency DAG. Edges are explicit indexes into <see cref="Items"/>
/// and may only point to earlier positions, so a cyclic or dangling chain is
/// unrepresentable.
/// </summary>
public sealed record CreateWorkItemChainArgs : MajordomoMutateArgs
{
    /// <summary>Hard contract bound on nodes in one chain call.</summary>
    public const int MaxItems = 32;

    public CreateWorkItemChainArgs(IReadOnlyList<WorkItemChainNode> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (items.Count is < 2 or > MaxItems)
            throw new ArgumentException(
                $"a chain must contain between 2 and {MaxItems} items; use create_work_item for a single item",
                nameof(items));

        var hasAnyEdge = false;
        for (var i = 0; i < items.Count; i++)
        {
            var node = items[i] ?? throw new ArgumentException("chain nodes must not be null", nameof(items));
            var seen = new HashSet<int>();
            foreach (var edge in node.DependsOnIndexes)
            {
                if (edge < 0 || edge >= i)
                    throw new ArgumentException(
                        $"items[{i}].dependsOnIndexes entry {edge} must reference an earlier position (0..{i - 1})",
                        nameof(items));
                if (!seen.Add(edge))
                    throw new ArgumentException(
                        $"items[{i}].dependsOnIndexes contains duplicate edge {edge}", nameof(items));
                hasAnyEdge = true;
            }
        }

        if (!hasAnyEdge)
            throw new ArgumentException(
                "a chain must contain at least one dependency edge; use create_work_item for independent items",
                nameof(items));

        Items = [.. items];
    }

    /// <summary>Ordered chain nodes; <c>Items[i].DependsOnIndexes</c> only references positions &lt; i.</summary>
    public IReadOnlyList<WorkItemChainNode> Items { get; }

    public override int AffectedItemCount => Items.Count;
}

/// <summary>Arguments for <c>update_work_item</c>: replace-set edit of an item's fields.</summary>
public sealed record UpdateWorkItemArgs : MajordomoMutateArgs
{
    public UpdateWorkItemArgs(WorkItemId id, WorkItemPatch patch)
    {
        Id = id;
        Patch = patch ?? throw new ArgumentNullException(nameof(patch));
    }

    /// <summary>The work item to edit.</summary>
    public WorkItemId Id { get; }

    /// <summary>The replace-set edit; never an empty patch.</summary>
    public WorkItemPatch Patch { get; }

    public override int AffectedItemCount => 1;
}

/// <summary>
/// Arguments for <c>cancel_work_item</c>. A reason is required (unlike the
/// raw REST surface, where it is optional): the majordomo must justify every
/// cancellation so autonomous cancels and proposals are reviewable.
/// </summary>
public sealed record CancelWorkItemArgs : MajordomoMutateArgs
{
    public CancelWorkItemArgs(WorkItemId id, string reason)
    {
        if (AgentPauseValidation.ValidateRequiredReason(reason, "reason") is { } error)
            throw new ArgumentException(error, nameof(reason));
        Id = id;
        Reason = reason;
    }

    /// <summary>The work item to cancel.</summary>
    public WorkItemId Id { get; }

    /// <summary>Operator-facing justification recorded on the item.</summary>
    public string Reason { get; }

    public override int AffectedItemCount => 1;
}

/// <summary>
/// Arguments for <c>retry_work_item</c>: re-queue a terminated or parked item
/// resuming from a chosen pipeline phase.
/// </summary>
public sealed record RetryWorkItemArgs : MajordomoMutateArgs
{
    public RetryWorkItemArgs(
        WorkItemId id,
        WorkItemRetryFrom from = WorkItemRetryFrom.Work,
        TimeSpan? workTimeout = null)
    {
        Id = id;
        From = from;
        WorkTimeout = workTimeout is { } wt ? WorkItemFieldValidation.WorkTimeout(wt) : null;
    }

    /// <summary>The work item to retry.</summary>
    public WorkItemId Id { get; }

    /// <summary>Pipeline phase to resume from; defaults to a full work-phase re-run.</summary>
    public WorkItemRetryFrom From { get; }

    /// <summary>Optional new work-phase budget for the retried iteration.</summary>
    public TimeSpan? WorkTimeout { get; }

    public override int AffectedItemCount => 1;
}

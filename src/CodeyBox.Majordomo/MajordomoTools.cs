using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace CodeyBox.Majordomo;

/// <summary>
/// The closed majordomo tool vocabulary — the complete set of operations the
/// assistant may invoke against the queue. The majordomo's hands are the
/// queue and nothing else: no tool here executes shell commands, writes or
/// reads host files, changes orchestrator configuration, or controls service
/// state (queue pause/drain, agent pause, availability resets are all
/// deliberately absent).
///
/// Lookup is ordinal exact-match only: unknown names, case variants, and
/// substrings never resolve.
/// </summary>
public static class MajordomoTools
{
    public static readonly MajordomoTool GetQueueStatus = new(
        "get_queue_status",
        MajordomoToolClass.Read,
        typeof(GetQueueStatusArgs),
        typeof(QueueStatusResult),
        "Read the global queue gate (running/paused) and per-state work-item counts.");

    public static readonly MajordomoTool GetDispatchStatus = new(
        "get_dispatch_status",
        MajordomoToolClass.Read,
        typeof(GetDispatchStatusArgs),
        typeof(DispatchStatusResult),
        "Read dispatcher concurrency: slots in use, queued depth, and which items are in flight.");

    public static readonly MajordomoTool GetAgentCapacity = new(
        "get_agent_capacity",
        MajordomoToolClass.Read,
        typeof(GetAgentCapacityArgs),
        typeof(AgentCapacityResult),
        "Read per-agent quota and concurrency: routability, in-flight counts, caps, and quota reset times.");

    public static readonly MajordomoTool ListWorkItems = new(
        "list_work_items",
        MajordomoToolClass.Read,
        typeof(ListWorkItemsArgs),
        typeof(ListWorkItemsResult),
        "List work items, optionally filtered by project and lifecycle state, up to a bounded page size.");

    public static readonly MajordomoTool GetWorkItem = new(
        "get_work_item",
        MajordomoToolClass.Read,
        typeof(GetWorkItemArgs),
        typeof(WorkItemDetailResult),
        "Inspect one work item in full: prompt, state, routing, dependencies, and failure text.");

    public static readonly MajordomoTool GetWorkItemAudit = new(
        "get_work_item_audit",
        MajordomoToolClass.Read,
        typeof(GetWorkItemAuditArgs),
        typeof(WorkItemAuditResult),
        "Read the audit reports recorded for one work item, optionally scoped to one iteration.");

    public static readonly MajordomoTool CreateWorkItem = new(
        "create_work_item",
        MajordomoToolClass.Mutate,
        typeof(CreateWorkItemArgs),
        typeof(MajordomoChangeSet),
        "Enqueue one new work item for a project.");

    public static readonly MajordomoTool CreateWorkItemChain = new(
        "create_work_item_chain",
        MajordomoToolClass.Mutate,
        typeof(CreateWorkItemChainArgs),
        typeof(MajordomoChangeSet),
        "Enqueue a dependent chain of work items; edges are explicit indexes of earlier nodes.");

    public static readonly MajordomoTool UpdateWorkItem = new(
        "update_work_item",
        MajordomoToolClass.Mutate,
        typeof(UpdateWorkItemArgs),
        typeof(MajordomoChangeSet),
        "Replace-set edit of one work item's fields (title, prompt, routing, priority, timeouts, dependencies, external ids).");

    public static readonly MajordomoTool CancelWorkItem = new(
        "cancel_work_item",
        MajordomoToolClass.Mutate,
        typeof(CancelWorkItemArgs),
        typeof(MajordomoChangeSet),
        "Cancel one work item with a required operator-facing reason.");

    public static readonly MajordomoTool RetryWorkItem = new(
        "retry_work_item",
        MajordomoToolClass.Mutate,
        typeof(RetryWorkItemArgs),
        typeof(MajordomoChangeSet),
        "Re-queue one work item resuming from a chosen pipeline phase.");

    /// <summary>
    /// Every tool in the vocabulary, in declaration order. Backed by an
    /// <see cref="ImmutableArray{T}"/> so the vocabulary itself cannot be
    /// rewritten through a mutable-cast of the exposed list.
    /// </summary>
    public static readonly IReadOnlyList<MajordomoTool> All =
        ImmutableArray.Create(
            GetQueueStatus,
            GetDispatchStatus,
            GetAgentCapacity,
            ListWorkItems,
            GetWorkItem,
            GetWorkItemAudit,
            CreateWorkItem,
            CreateWorkItemChain,
            UpdateWorkItem,
            CancelWorkItem,
            RetryWorkItem);

    private static readonly FrozenDictionary<string, MajordomoTool> ByName =
        All.ToFrozenDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>
    /// Resolves a tool by exact ordinal name. No substring, prefix,
    /// case-folding, or whitespace tolerance — anything that is not a
    /// canonical name returns false.
    /// </summary>
    public static bool TryGet(string? name, [MaybeNullWhen(false)] out MajordomoTool tool)
    {
        if (name is not null && ByName.TryGetValue(name, out var found))
        {
            tool = found;
            return true;
        }
        tool = null;
        return false;
    }
}

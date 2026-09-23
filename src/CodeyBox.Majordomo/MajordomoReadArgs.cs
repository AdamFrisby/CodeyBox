using System.Collections.Frozen;
using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Arguments for <c>get_queue_status</c>: global queue gate state and
/// per-state work-item counts. Takes no parameters — a transport decoding
/// an empty payload must substitute <see cref="Instance"/>; the constructor
/// is private by design.
/// </summary>
public sealed record GetQueueStatusArgs : MajordomoToolArgs
{
    /// <summary>The single (parameterless) instance.</summary>
    public static readonly GetQueueStatusArgs Instance = new();

    private GetQueueStatusArgs() { }
}

/// <summary>
/// Arguments for <c>get_dispatch_status</c>: dispatcher concurrency and the
/// slots currently occupied by in-flight work. Takes no parameters — a
/// transport decoding an empty payload must substitute
/// <see cref="Instance"/>; the constructor is private by design.
/// </summary>
public sealed record GetDispatchStatusArgs : MajordomoToolArgs
{
    /// <summary>The single (parameterless) instance.</summary>
    public static readonly GetDispatchStatusArgs Instance = new();

    private GetDispatchStatusArgs() { }
}

/// <summary>
/// Arguments for <c>get_agent_capacity</c>: per-agent quota and concurrency
/// state. Null <see cref="Agent"/> covers every registered agent.
/// </summary>
public sealed record GetAgentCapacityArgs : MajordomoToolArgs
{
    public GetAgentCapacityArgs(AgentKind? agent = null)
    {
        Agent = agent;
    }

    /// <summary>Restrict the report to one agent kind; null = all agents.</summary>
    public AgentKind? Agent { get; }
}

/// <summary>
/// Arguments for <c>list_work_items</c>: a bounded scan of the queue.
/// </summary>
public sealed record ListWorkItemsArgs : MajordomoToolArgs
{
    /// <summary>Smallest accepted <see cref="Limit"/>.</summary>
    public const int MinLimit = 1;

    /// <summary>Largest accepted <see cref="Limit"/>: a list call may never page unbounded.</summary>
    public const int MaxLimit = 500;

    /// <summary>Default page size when the caller does not supply one.</summary>
    public const int DefaultLimit = 50;

    public ListWorkItemsArgs(
        ProjectId? projectId = null,
        IReadOnlySet<WorkItemState>? states = null,
        int limit = DefaultLimit)
    {
        if (states is { Count: 0 })
            throw new ArgumentException("states must be non-empty when provided", nameof(states));
        if (states is not null)
        {
            foreach (var state in states)
            {
                if (!Enum.IsDefined(state))
                    throw new ArgumentOutOfRangeException(nameof(states), state,
                        $"states entry '{state}' is not a defined {nameof(WorkItemState)}");
            }
        }
        if (limit is < MinLimit or > MaxLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), limit,
                $"limit must be within [{MinLimit}, {MaxLimit}]");

        ProjectId = projectId;
        // Copy to a frozen set: the caller's collection must not be able to
        // mutate the filter after it was validated.
        States = states?.ToFrozenSet();
        Limit = limit;
    }

    /// <summary>Restrict to one project; null = every project.</summary>
    public ProjectId? ProjectId { get; }

    /// <summary>Restrict to these lifecycle states; null = every state.</summary>
    public IReadOnlySet<WorkItemState>? States { get; }

    /// <summary>Maximum items returned, within [<see cref="MinLimit"/>, <see cref="MaxLimit"/>].</summary>
    public int Limit { get; }
}

/// <summary>
/// Arguments for <c>get_work_item</c>: full detail on one item, including
/// failure text.
/// </summary>
public sealed record GetWorkItemArgs : MajordomoToolArgs
{
    public GetWorkItemArgs(WorkItemId id)
    {
        Id = id;
    }

    /// <summary>The work item to inspect.</summary>
    public WorkItemId Id { get; }
}

/// <summary>
/// Arguments for <c>get_work_item_audit</c>: the audit reports recorded for
/// one work item.
/// </summary>
public sealed record GetWorkItemAuditArgs : MajordomoToolArgs
{
    public GetWorkItemAuditArgs(WorkItemId id, int? iteration = null)
    {
        if (iteration is < 0)
            throw new ArgumentOutOfRangeException(nameof(iteration), iteration,
                "iteration must be >= 0");
        Id = id;
        Iteration = iteration;
    }

    /// <summary>The work item whose audit reports to read.</summary>
    public WorkItemId Id { get; }

    /// <summary>Restrict to a single audit iteration; null = all iterations.</summary>
    public int? Iteration { get; }
}

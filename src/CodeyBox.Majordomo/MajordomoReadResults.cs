using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Result of <c>get_queue_status</c>: the global queue gate plus per-state
/// item counts.
/// </summary>
/// <param name="State">Whether the queue is picking up new work.</param>
/// <param name="PausedAt">When the queue was paused; null while running.</param>
/// <param name="PausedReason">Operator-supplied pause reason; null while running.</param>
/// <param name="ItemCountsByState">Live item counts keyed by lifecycle state.</param>
public sealed record QueueStatusResult(
    QueueState State,
    DateTimeOffset? PausedAt,
    string? PausedReason,
    IReadOnlyDictionary<WorkItemState, int> ItemCountsByState) : MajordomoToolResult;

/// <summary>One occupied dispatch slot in <see cref="DispatchStatusResult"/>.</summary>
/// <param name="WorkerIndex">Dispatcher slot index.</param>
/// <param name="WorkItemId">Item currently bound to the slot.</param>
/// <param name="AcquiredAt">When the slot was acquired.</param>
public sealed record OccupiedDispatchSlot(
    int WorkerIndex,
    WorkItemId WorkItemId,
    DateTimeOffset AcquiredAt);

/// <summary>
/// Result of <c>get_dispatch_status</c>: dispatcher concurrency usage.
/// </summary>
/// <param name="MaxConcurrent">Configured dispatcher concurrency ceiling.</param>
/// <param name="CurrentlyRunning">Slots currently occupied.</param>
/// <param name="QueuedCount">Items in <see cref="WorkItemState.Queued"/> awaiting pickup.</param>
/// <param name="LastSpawnAt">When the dispatcher last started a worker; null if never.</param>
/// <param name="OccupiedSlots">The items currently bound to dispatch slots.</param>
public sealed record DispatchStatusResult(
    int MaxConcurrent,
    int CurrentlyRunning,
    int QueuedCount,
    DateTimeOffset? LastSpawnAt,
    IReadOnlyList<OccupiedDispatchSlot> OccupiedSlots) : MajordomoToolResult;

/// <summary>Quota and concurrency state for one registered agent kind.</summary>
/// <param name="Agent">The agent kind.</param>
/// <param name="Routable">Whether the agent can currently take work.</param>
/// <param name="ExclusionReason">Why the agent is not routable; null when routable.</param>
/// <param name="InFlight">Items currently dispatched to this agent.</param>
/// <param name="MaxConcurrent">Configured per-agent concurrency cap; null = uncapped.</param>
/// <param name="QuotaExhausted">Whether the agent's quota window is spent.</param>
/// <param name="QuotaResetsAt">When the exhausted window resets; null when unknown or not exhausted.</param>
public sealed record AgentCapacityEntry(
    AgentKind Agent,
    bool Routable,
    string? ExclusionReason,
    int InFlight,
    int? MaxConcurrent,
    bool QuotaExhausted,
    DateTimeOffset? QuotaResetsAt);

/// <summary>Result of <c>get_agent_capacity</c>.</summary>
/// <param name="Entries">One entry per agent in scope.</param>
public sealed record AgentCapacityResult(
    IReadOnlyList<AgentCapacityEntry> Entries) : MajordomoToolResult;

/// <summary>Queue-row summary returned by <c>list_work_items</c>.</summary>
public sealed record WorkItemSummary(
    WorkItemId Id,
    ProjectId ProjectId,
    string Title,
    WorkItemState State,
    int Priority,
    AgentKind? Agent,
    string? AgentClassId,
    string? FailureKind,
    IReadOnlyList<WorkItemId> DependsOn,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Result of <c>list_work_items</c>.</summary>
/// <param name="Items">The matching rows, capped at the request's limit.</param>
/// <param name="TotalMatched">Total rows matching the filter, including those past the limit.</param>
public sealed record ListWorkItemsResult(
    IReadOnlyList<WorkItemSummary> Items,
    int TotalMatched) : MajordomoToolResult;

/// <summary>Full queue-row detail returned by <c>get_work_item</c>.</summary>
public sealed record WorkItemDetail(
    WorkItemId Id,
    ProjectId ProjectId,
    string Title,
    string Prompt,
    WorkItemState State,
    int Priority,
    AgentKind? Agent,
    string? AgentClassId,
    string? AuditorProfile,
    string? BaseBranch,
    string? WorkBranch,
    IReadOnlyList<WorkItemId> DependsOn,
    string? LastError,
    string? FailureKind,
    WorkItemCancellationReason? CancellationReason,
    DateTimeOffset? QuotaResetAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Result of <c>get_work_item</c>.</summary>
/// <param name="Item">The item; null when the id does not exist.</param>
public sealed record WorkItemDetailResult(
    WorkItemDetail? Item) : MajordomoToolResult;

/// <summary>One auditor report inside <see cref="WorkItemAuditResult"/>.</summary>
public sealed record AuditReportSummary(
    int Iteration,
    AuditTarget Target,
    string AuditorName,
    string WorstSeverity,
    IReadOnlyList<AuditReportFinding> Findings,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt);

/// <summary>
/// Result of <c>get_work_item_audit</c>: findings-level audit history.
/// Raw auditor output is deliberately not part of this contract.
/// </summary>
public sealed record WorkItemAuditResult(
    WorkItemId WorkItemId,
    IReadOnlyList<AuditReportSummary> Reports) : MajordomoToolResult;

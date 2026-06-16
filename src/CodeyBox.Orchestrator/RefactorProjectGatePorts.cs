using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Narrow read-side port for project-scoped refactor drain and lock state.
/// </summary>
public interface IRefactorProjectGateStatusProvider
{
    Task<IReadOnlyList<RefactorProjectGateStatus>> GetRefactorProjectGateStatusAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Dispatch-health port for project-scoped refactor drain and lock decisions.
/// </summary>
public interface IRefactorProjectDispatchGate
{
    Task<RefactorDispatchGateDecision> CheckRefactorDispatchGateAsync(
        RefactorDispatchCandidate candidate,
        CancellationToken ct = default);
}

public sealed record RefactorDispatchCandidate(
    WorkItemId Id,
    ProjectId ProjectId,
    JobType JobType,
    WorkItemState State,
    int Priority,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt)
{
    public static RefactorDispatchCandidate FromWorkItem(WorkItem item) => new(
        item.Id,
        item.ProjectId,
        item.JobType,
        item.State,
        item.Priority,
        item.CreatedAt,
        item.StartedAt);
}

public sealed record RefactorDispatchGateDecision(bool IsBlocked, string? Reason)
{
    public static RefactorDispatchGateDecision Allow { get; } = new(false, null);

    public static RefactorDispatchGateDecision Block(string reason) => new(true, reason);
}

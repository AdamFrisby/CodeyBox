using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Narrow read-side port for project-scoped refactor drain and lock state.
/// </summary>
public interface IRefactorProjectGateStatusProvider
{
    Task<IReadOnlyList<RefactorProjectGateStatus>> GetRefactorProjectGateStatusAsync(
        CancellationToken ct = default);

    Task<RefactorDispatchGateDecision> CheckRefactorDispatchGateAsync(
        WorkItem candidate,
        CancellationToken ct = default);
}

public sealed record RefactorDispatchGateDecision(bool IsBlocked, string? Reason)
{
    public static RefactorDispatchGateDecision Allow { get; } = new(false, null);

    public static RefactorDispatchGateDecision Block(string reason) => new(true, reason);
}

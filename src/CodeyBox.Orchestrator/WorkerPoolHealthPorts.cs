using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Narrow dispatcher-health surface consumed by <see cref="WorkerPoolHealthWatchdog"/>.
/// It intentionally exposes only pool status, runnable candidate identifiers,
/// and recovery kicks rather than dispatcher internals or full persisted rows.
/// </summary>
public interface IWorkerPoolHealthSource
{
    bool IsDispatchPaused { get; }

    Task<WorkerPoolStatus> GetStatusAsync(CancellationToken ct = default);

    Task<IReadOnlyList<WorkerPoolHealthCandidate>> ListRunnableCandidatesAsync(
        int scanLimit,
        CancellationToken ct);

    Task<int> TriggerDispatchRecoveryAsync(IEnumerable<WorkItemId> candidateIds, CancellationToken ct);
}

/// <summary>Runnable work item identity surfaced to the pool-health watchdog.</summary>
public sealed record WorkerPoolHealthCandidate(WorkItemId Id, WorkItemState State);

/// <summary>Read-side agent capacity check for watchdog routing probes.</summary>
public interface IAgentCapacitySnapshot
{
    bool HasCapacity(AgentKind agent);
}

public enum AgentRoutingReadinessState
{
    NotApplicable,
    Available,
    Unavailable,
}

public sealed record AgentRoutingReadiness(
    AgentRoutingReadinessState State,
    AgentKind? Agent = null,
    string? Reason = null)
{
    public static AgentRoutingReadiness NotApplicable(string? reason = null) =>
        new(AgentRoutingReadinessState.NotApplicable, Reason: reason);

    public static AgentRoutingReadiness Available(AgentKind agent, string? reason = null) =>
        new(AgentRoutingReadinessState.Available, agent, reason);

    public static AgentRoutingReadiness Unavailable(string? reason = null) =>
        new(AgentRoutingReadinessState.Unavailable, Reason: reason);
}

/// <summary>
/// Routing availability probe for health checks. Implementations should avoid
/// committing dispatch side effects such as durable slot reservation, but must
/// apply the same gates the dispatcher would apply so pool-health detection
/// cannot drift from real routing.
/// </summary>
public interface IAgentRoutingReadiness
{
    Task<AgentRoutingReadiness> CheckReadinessAsync(
        WorkItem item,
        Project? project,
        IAgentCapacitySnapshot capacity,
        CancellationToken ct);
}

/// <summary>Quota-parked work recovery hook used by the pool-health watchdog.</summary>
public interface IWorkerPoolQuotaRecovery
{
    Task RunWatchdogRecoverySweepAsync(CancellationToken ct);
}

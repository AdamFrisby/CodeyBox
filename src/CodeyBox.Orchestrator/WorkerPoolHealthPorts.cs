using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Narrow dispatcher-health surface consumed by <see cref="WorkerPoolHealthWatchdog"/>.
/// It intentionally exposes only the pool status, candidate scan, and recovery
/// kick needed by the watchdog rather than the full orchestrator service.
/// </summary>
public interface IWorkerPoolHealthSource
{
    bool IsDispatchPaused { get; }

    Task<WorkerPoolStatus> GetStatusAsync(CancellationToken ct = default);

    Task<IReadOnlyList<WorkItem>> ListPoolHealthCandidatesAsync(int scanLimit, CancellationToken ct);

    Task<int> TriggerDispatchRecoveryAsync(IEnumerable<WorkItemId> candidateIds, CancellationToken ct);
}

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
/// Read-side routing availability probe. Implementations must avoid dispatch
/// side effects such as slot reservation, sandbox provisioning, or quota API
/// refreshes; this is a health-check predicate, not the authoritative router.
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

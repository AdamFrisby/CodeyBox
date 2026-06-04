using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Watchdog-facing coordinator for dispatcher health. It owns the read-only
/// eligibility checks used by the pool watchdog so the watchdog does not need
/// to know project, budget, routing, availability, or deferred-pickup details.
/// </summary>
public sealed class WorkerPoolHealthCoordinator : IWorkerPoolHealthSource, IAgentCapacitySnapshot
{
    private readonly OrchestratorService _dispatcher;
    private readonly IWorkItemStore _store;
    private readonly ITaskQueue _queue;
    private readonly IProjectRepository? _projects;
    private readonly IQueueController? _queueController;
    private readonly IAgentRegistry? _agents;
    private readonly IAgentDispatchAvailability? _dispatchAvailability;
    private readonly IAgentRoutingReadiness? _routingReadiness;
    private readonly ILogger<WorkerPoolHealthCoordinator> _log;

    public WorkerPoolHealthCoordinator(
        OrchestratorService dispatcher,
        IWorkItemStore store,
        ITaskQueue queue,
        ILogger<WorkerPoolHealthCoordinator> log,
        IProjectRepository? projects = null,
        IQueueController? queueController = null,
        IAgentRegistry? agents = null,
        IAgentAvailabilityRegistry? availability = null,
        IAgentRoutingReadiness? routingReadiness = null,
        SmokeOptionsSnapshot? smokeOptions = null,
        IAgentDispatchAvailability? dispatchAvailability = null)
    {
        _dispatcher = dispatcher;
        _store = store;
        _queue = queue;
        _projects = projects;
        _queueController = queueController;
        _agents = agents;
        _dispatchAvailability = dispatchAvailability
            ?? AgentDispatchAvailability.CreateIfConfigured(availability, inVmSmokeGate: null, smokeOptions);
        _routingReadiness = routingReadiness;
        _log = log;
    }

    public bool IsDispatchPaused => _dispatcher.IsDispatchPaused;

    public Task<WorkerPoolStatus> GetStatusAsync(CancellationToken ct = default) =>
        _dispatcher.GetStatusAsync(ct);

    public bool HasCapacity(AgentKind agent) => _dispatcher.HasCapacity(agent);

    public async Task<IReadOnlyList<WorkerPoolHealthCandidate>> ListRunnableCandidatesAsync(
        int scanLimit,
        CancellationToken ct)
    {
        if (scanLimit <= 0)
            return [];

        if (_queueController is not null && _queueController.State == QueueState.Paused)
            return [];

        var skipIds = new HashSet<WorkItemId>(_dispatcher.ActiveWorkItemIdsForHealthCheck());
        Dictionary<WorkItemId, WorkItemState>? statesById = null;
        var result = new List<WorkerPoolHealthCandidate>(Math.Min(scanLimit, 16));
        var inspected = 0;

        async Task<bool> DependenciesSatisfiedAsync(WorkItem candidate)
        {
            if (candidate.DependsOn.Count == 0)
                return true;

            if (statesById is null)
            {
                var snapshot = new List<WorkItem>();
                await foreach (var i in _store.ListAsync(ct))
                    snapshot.Add(i);
                statesById = WorkItemDependencies.BuildStateMap(snapshot);
            }

            return WorkItemDependencies.AreSatisfied(candidate.DependsOn, statesById);
        }

        await foreach (var candidate in _store.ListDispatchEligibleByPriorityAsync(skipIds, ct))
        {
            if (inspected++ >= scanLimit)
                break;

            if (!await IsRunnableCandidateAsync(candidate, DependenciesSatisfiedAsync, ct))
                continue;

            result.Add(new WorkerPoolHealthCandidate(candidate.Id, candidate.State));
        }

        if (inspected >= scanLimit)
            return result;

        await foreach (var candidate in _store.ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct))
        {
            if (inspected++ >= scanLimit)
                break;

            if (skipIds.Contains(candidate.Id))
                continue;

            if (!await IsRunnableCandidateAsync(candidate, DependenciesSatisfiedAsync, ct))
                continue;

            result.Add(new WorkerPoolHealthCandidate(candidate.Id, candidate.State));
        }

        return result;
    }

    public async Task<int> TriggerDispatchRecoveryAsync(
        IEnumerable<WorkItemId> candidateIds,
        CancellationToken ct)
    {
        var enqueued = 0;
        var seen = new HashSet<WorkItemId>();
        foreach (var id in candidateIds)
        {
            if (!seen.Add(id))
                continue;

            _dispatcher.ClearDeferredForHealthRecovery(id);
            await _queue.EnqueueAsync(id, ct);
            enqueued++;
        }

        return enqueued;
    }

    private async Task<bool> IsRunnableCandidateAsync(
        WorkItem candidate,
        Func<WorkItem, Task<bool>> dependenciesSatisfied,
        CancellationToken ct)
    {
        if (!await dependenciesSatisfied(candidate))
            return false;

        Project? project = null;
        if (_projects is not null)
        {
            project = await _projects.GetAsync(candidate.ProjectId, ct);
            if (project is null)
                return false;
        }

        if (await IsProjectPausedAsync(candidate, ct))
            return false;

        if (project is not null && await IsBudgetBlockedAsync(candidate, project.Budget, ct))
            return false;

        return await HasEligibleAvailableAgentAsync(candidate, project, ct);
    }

    private async Task<bool> IsProjectPausedAsync(WorkItem item, CancellationToken ct)
    {
        if (_queueController is null)
            return false;

        var state = await _queueController.GetProjectStateAsync(item.ProjectId, ct);
        return state is { Paused: true };
    }

    private async Task<bool> IsBudgetBlockedAsync(
        WorkItem item,
        ProjectBudget budget,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (budget.MaxItemsPerHour > 0)
        {
            var count = await _store.CountStartedInWindowAsync(item.ProjectId, now.AddHours(-1), ct);
            if (count >= budget.MaxItemsPerHour)
            {
                LogBudgetBlocked(item, $"hourly limit: {count}/{budget.MaxItemsPerHour} items started in last hour");
                return true;
            }
        }

        if (budget.MaxItemsPerDay > 0)
        {
            var count = await _store.CountStartedInWindowAsync(item.ProjectId, now.AddHours(-24), ct);
            if (count >= budget.MaxItemsPerDay)
            {
                LogBudgetBlocked(item, $"daily limit: {count}/{budget.MaxItemsPerDay} items started in last 24h");
                return true;
            }
        }

        if (budget.MaxConcurrentForProject > 0)
        {
            var count = await _store.CountInFlightAsync(item.ProjectId, ct);
            if (count >= budget.MaxConcurrentForProject)
            {
                LogBudgetBlocked(item, $"concurrent limit: {count}/{budget.MaxConcurrentForProject} items in flight");
                return true;
            }
        }

        return false;
    }

    private void LogBudgetBlocked(WorkItem item, string reason)
    {
        _log.LogDebug(
            "Worker-pool health skip {Id}: project budget gate is active ({Reason})",
            item.Id,
            reason);
    }

    private async Task<bool> HasEligibleAvailableAgentAsync(
        WorkItem item,
        Project? project,
        CancellationToken ct)
    {
        if (_routingReadiness is not null)
        {
            var readiness = await _routingReadiness.CheckReadinessAsync(item, project, this, ct);
            if (readiness.State == AgentRoutingReadinessState.Available)
                return true;
            if (readiness.State == AgentRoutingReadinessState.Unavailable)
                return false;
        }

        var directAgent = item.Agent ?? project?.DefaultAgent;
        return directAgent is { } agent && IsDirectAgentAvailable(agent);
    }

    private bool IsDirectAgentAvailable(AgentKind agent)
    {
        if (_agents is not null && !_agents.Available.Contains(agent))
            return false;

        var availability = _dispatchAvailability?.GetAvailability(agent);
        if (availability is { Available: false })
            return false;

        return HasCapacity(agent);
    }
}

using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

internal sealed class QuotaRetryAdmissionPolicy
{
    private readonly AgentClassRouter? _router;
    private readonly IProjectRepository? _projects;
    private readonly ILogger _log;
    private readonly Dictionary<WorkItemId, IReadOnlySet<QuotaRetryAdmissionPoolKey>> _poolsByItem = [];
    private readonly Dictionary<WorkItemId, Project?> _projectsByItem = [];

    public QuotaRetryAdmissionPolicy(
        AgentClassRouter? router,
        IProjectRepository? projects,
        ILogger log)
    {
        _router = router;
        _projects = projects;
        _log = log;
    }

    public async Task<bool> BlocksLowerPriorityCandidateAsync(
        WorkItem blockedQuotaRetry,
        WorkItem lowerPriorityCandidate,
        CancellationToken ct)
    {
        var blockerPool = await ResolveAdmissionPoolAsync(blockedQuotaRetry, ct);
        if (blockerPool.Count == 0)
            return false;

        var candidatePool = await ResolveAdmissionPoolAsync(lowerPriorityCandidate, ct);
        if (candidatePool.Count == 0)
            return false;

        return blockerPool.Overlaps(candidatePool);
    }

    private async Task<IReadOnlySet<QuotaRetryAdmissionPoolKey>> ResolveAdmissionPoolAsync(
        WorkItem item,
        CancellationToken ct)
    {
        if (_poolsByItem.TryGetValue(item.Id, out var cached))
            return cached;

        var project = await ResolveProjectAsync(item, ct);
        var requiredCapability = QuotaRetryPhasePolicy.RequiredCapabilityForDispatchAdmission(item);
        if (_router is not null)
        {
            var routerPool = _router.GetQuotaRetryAdmissionPool(item, project, requiredCapability);
            if (routerPool.Count > 0)
            {
                _poolsByItem[item.Id] = routerPool;
                return routerPool;
            }
        }

        var directAgent = item.Agent ?? project?.DefaultAgent;
        IReadOnlySet<QuotaRetryAdmissionPoolKey> resolved = directAgent is { } agent
            ? new HashSet<QuotaRetryAdmissionPoolKey>
            {
                QuotaRetryAdmissionPoolKey.FromDirectAgent(
                    agent,
                    AgentInstanceIds.RouteKey(agent, item.AgentInstanceId),
                    item.ModelId),
            }
            : new HashSet<QuotaRetryAdmissionPoolKey>();

        _poolsByItem[item.Id] = resolved;
        return resolved;
    }

    private async Task<Project?> ResolveProjectAsync(WorkItem item, CancellationToken ct)
    {
        if (_projectsByItem.TryGetValue(item.Id, out var cached))
            return cached;

        Project? resolved = null;
        if (_projects is not null)
        {
            try
            {
                resolved = await _projects.GetAsync(item.ProjectId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Could not resolve project for work item {Id}; falling back to item-local quota admission pool",
                    item.Id);
            }
        }

        _projectsByItem[item.Id] = resolved;
        return resolved;
    }
}

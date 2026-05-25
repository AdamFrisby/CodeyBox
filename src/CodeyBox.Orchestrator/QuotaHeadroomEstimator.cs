using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public interface IQuotaHeadroomEstimator
{
    Task<QuotaHeadroomEstimate?> EstimateAsync(
        QuotaHeadroomRequest request,
        CancellationToken ct = default);
}

public sealed record QuotaHeadroomRequest(
    ProjectId ProjectId,
    AgentKind Agent,
    string? ModelId);

public sealed record QuotaHeadroomEstimate(
    double EstimatedIterPctCost,
    double AverageTokensPerIteration,
    int SampledItemCount,
    string Source);

public sealed class LazyQuotaHeadroomEstimator : IQuotaHeadroomEstimator
{
    private readonly Func<IQuotaHeadroomEstimator?> _resolver;

    public LazyQuotaHeadroomEstimator(Func<IQuotaHeadroomEstimator?> resolver)
    {
        _resolver = resolver;
    }

    public Task<QuotaHeadroomEstimate?> EstimateAsync(
        QuotaHeadroomRequest request,
        CancellationToken ct = default)
    {
        var estimator = _resolver();
        return estimator is null
            ? Task.FromResult<QuotaHeadroomEstimate?>(null)
            : estimator.EstimateAsync(request, ct);
    }
}

public sealed class CostHistoryQuotaHeadroomEstimator : IQuotaHeadroomEstimator
{
    private readonly IWorkItemCostStore _costs;
    private readonly QuotaRouterOptions _options;
    private readonly ILogger<CostHistoryQuotaHeadroomEstimator> _log;
    private readonly TimeProvider _time;

    public CostHistoryQuotaHeadroomEstimator(
        IWorkItemCostStore costs,
        QuotaRouterOptions options,
        ILogger<CostHistoryQuotaHeadroomEstimator> log,
        TimeProvider? timeProvider = null)
    {
        _costs = costs;
        _options = options;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<QuotaHeadroomEstimate?> EstimateAsync(
        QuotaHeadroomRequest request,
        CancellationToken ct = default)
    {
        if (!_options.HeadroomProjectionEnabled)
            return null;

        var tokensPerPct = ResolveTokensPerPct(request.Agent);
        if (tokensPerPct <= 0)
        {
            _log.LogWarning(
                "Quota headroom projection disabled for agent {Agent}: HeadroomTokensPerQuotaPct must be positive",
                request.Agent.Value);
            return null;
        }

        var now = _time.GetUtcNow();
        var from = now - _options.HeadroomHistoryWindow;
        var maxItems = _options.HeadroomHistoryItemCount;
        if (maxItems <= 0)
        {
            _log.LogWarning(
                "Quota headroom projection disabled for project {ProjectId}: HeadroomHistoryItemCount must be positive",
                request.ProjectId.Value);
            return null;
        }

        var hasModel = !string.IsNullOrWhiteSpace(request.ModelId);
        IReadOnlyList<WorkItemCost> scoped = hasModel
            ? await _costs.GetRecentByProjectAsync(
                request.ProjectId.Value,
                from,
                now,
                request.Agent.Value,
                request.ModelId,
                maxItems,
                ct)
            : [];
        var source = "agent+model";

        if (scoped.Count == 0)
        {
            scoped = await _costs.GetRecentByProjectAsync(
                request.ProjectId.Value,
                from,
                now,
                request.Agent.Value,
                modelId: null,
                maxItems,
                ct);
            source = "agent";
        }

        if (scoped.Count == 0)
        {
            scoped = await _costs.GetRecentByProjectAsync(
                request.ProjectId.Value,
                from,
                now,
                agentKind: null,
                modelId: null,
                maxItems,
                ct);
            source = "project";
        }

        var samples = BuildPerItemIterationSamples(scoped, maxItems);
        if (samples.Count == 0)
        {
            _log.LogDebug(
                "No quota headroom cost history for project {ProjectId} agent {Agent} model {Model}",
                request.ProjectId.Value,
                request.Agent.Value,
                request.ModelId ?? "(default)");
            return null;
        }

        var averageTokens = samples.Average();
        if (averageTokens <= 0)
        {
            _log.LogDebug(
                "Quota headroom cost history for project {ProjectId} agent {Agent} model {Model} has no positive token samples",
                request.ProjectId.Value,
                request.Agent.Value,
                request.ModelId ?? "(default)");
            return null;
        }

        var estimatedPct = averageTokens / tokensPerPct;
        if (estimatedPct <= 0)
        {
            _log.LogDebug(
                "Quota headroom estimate for project {ProjectId} agent {Agent} model {Model} was nonpositive",
                request.ProjectId.Value,
                request.Agent.Value,
                request.ModelId ?? "(default)");
            return null;
        }

        return new QuotaHeadroomEstimate(
            EstimatedIterPctCost: Math.Round(estimatedPct, 2, MidpointRounding.AwayFromZero),
            AverageTokensPerIteration: Math.Round(averageTokens, 2, MidpointRounding.AwayFromZero),
            SampledItemCount: samples.Count,
            Source: source);
    }

    private double ResolveTokensPerPct(AgentKind agent)
    {
        if (_options.HeadroomTokensPerQuotaPctByAgent.TryGetValue(agent.Value, out var byAgent)
            && byAgent > 0)
        {
            return byAgent;
        }

        return _options.HeadroomTokensPerQuotaPct;
    }

    private static IReadOnlyList<double> BuildPerItemIterationSamples(
        IReadOnlyList<WorkItemCost> rows,
        int maxItems)
    {
        if (rows.Count == 0 || maxItems <= 0)
            return [];

        return rows
            .GroupBy(r => r.WorkItemId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(r => r.EndedAt))
            .Take(maxItems)
            .Select(ItemTokensPerIteration)
            .Where(tokens => tokens > 0)
            .ToList();
    }

    private static double ItemTokensPerIteration(IEnumerable<WorkItemCost> itemRows)
    {
        var rows = itemRows.ToList();
        var maxExplicitIteration = rows
            .Where(r => r.Iteration.HasValue)
            .Select(r => r.Iteration!.Value)
            .DefaultIfEmpty(1)
            .Max();

        var totalTokens = rows.Sum(r => Math.Max(0, r.InputTokens) + Math.Max(0, r.OutputTokens));
        return totalTokens / Math.Max(1.0, maxExplicitIteration);
    }
}

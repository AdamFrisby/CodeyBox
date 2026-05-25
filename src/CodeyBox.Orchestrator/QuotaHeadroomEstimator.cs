using Microsoft.Extensions.Logging;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public interface IQuotaHeadroomEstimator
{
    Task<QuotaHeadroomEstimate?> EstimateAsync(
        ProjectId projectId,
        AgentMembership member,
        CancellationToken ct = default);
}

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
        ProjectId projectId,
        AgentMembership member,
        CancellationToken ct = default)
    {
        IQuotaHeadroomEstimator? estimator;
        try
        {
            estimator = _resolver();
        }
        catch
        {
            // Cost storage is best-effort; if it is not migrated yet, quota
            // routing should fall back to the ordinary availability gate.
            return Task.FromResult<QuotaHeadroomEstimate?>(null);
        }

        return estimator is null
            ? Task.FromResult<QuotaHeadroomEstimate?>(null)
            : estimator.EstimateAsync(projectId, member, ct);
    }
}

public sealed class CostHistoryQuotaHeadroomEstimator : IQuotaHeadroomEstimator
{
    private readonly IWorkItemCostStore _costs;
    private readonly QuotaRouterOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CostHistoryQuotaHeadroomEstimator> _log;

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
        ProjectId projectId,
        AgentMembership member,
        CancellationToken ct = default)
    {
        if (!_options.HeadroomProjectionEnabled)
            return null;

        var tokensPerPct = ResolveTokensPerPct(member.Agent);
        if (tokensPerPct <= 0)
            return null;

        var now = _time.GetUtcNow();
        IReadOnlyList<WorkItemCost> rows;
        try
        {
            rows = await _costs.GetByProjectAsync(
                projectId.Value,
                now - _options.HeadroomHistoryWindow,
                now,
                ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Failed to read quota headroom cost history for project {ProjectId}", projectId.Value);
            return null;
        }

        var hasModel = !string.IsNullOrWhiteSpace(member.ModelId);
        List<WorkItemCost> scoped = hasModel
            ? FilterRows(rows, member, matchModel: true).ToList()
            : [];
        var source = "agent+model";

        if (scoped.Count == 0)
        {
            scoped = FilterRows(rows, member, matchModel: false).ToList();
            source = "agent";
        }

        if (scoped.Count == 0)
        {
            scoped = rows.ToList();
            source = "project";
        }

        var samples = BuildPerItemIterationSamples(scoped, _options.HeadroomHistoryItemCount);
        if (samples.Count == 0)
            return null;

        var averageTokens = samples.Average();
        if (averageTokens <= 0)
            return null;

        var estimatedPct = averageTokens / tokensPerPct;
        if (estimatedPct <= 0)
            return null;

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

    private static IEnumerable<WorkItemCost> FilterRows(
        IEnumerable<WorkItemCost> rows,
        AgentMembership member,
        bool matchModel)
    {
        foreach (var row in rows)
        {
            if (!string.Equals(row.AgentKind, member.Agent.Value, StringComparison.OrdinalIgnoreCase))
                continue;

            if (matchModel
                && !string.IsNullOrWhiteSpace(member.ModelId)
                && !string.Equals(row.ModelId, member.ModelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return row;
        }
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

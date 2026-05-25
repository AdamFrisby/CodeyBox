using Microsoft.Extensions.Logging;
using CodeyBox.Core;
using System.Text.Json;

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
    string Source,
    bool TrustedForEnforcement = false);

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
        var scoped = hasModel
            ? FilterHeadroomRows(await _costs.GetRecentByProjectAsync(
                request.ProjectId.Value,
                from,
                now,
                request.Agent.Value,
                request.ModelId,
                maxItems,
                ct))
            : FilteredHeadroomRows.Empty;
        var source = "agent+model";

        if (scoped.Rows.Count == 0)
        {
            scoped = FilterHeadroomRows(await _costs.GetRecentByProjectAsync(
                request.ProjectId.Value,
                from,
                now,
                request.Agent.Value,
                modelId: null,
                maxItems,
                ct));
            source = "agent";
        }

        if (scoped.Rows.Count == 0)
        {
            scoped = FilterHeadroomRows(await _costs.GetRecentByProjectAsync(
                request.ProjectId.Value,
                from,
                now,
                agentKind: null,
                modelId: null,
                maxItems,
                ct));
            source = "project";
        }

        var samples = BuildPerItemIterationSamples(
            scoped.Rows,
            maxItems,
            _options.HeadroomMaxTokensPerIteration);
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
            Source: source,
            TrustedForEnforcement: scoped.TrustedForEnforcement);
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

    private FilteredHeadroomRows FilterHeadroomRows(IReadOnlyList<WorkItemCost> rows)
    {
        if (rows.Count == 0)
            return FilteredHeadroomRows.Empty;

        var maxTokensPerRow = _options.HeadroomMaxTokensPerCostRow;
        if (maxTokensPerRow <= 0)
        {
            _log.LogWarning(
                "Quota headroom projection disabled: HeadroomMaxTokensPerCostRow must be positive");
            return FilteredHeadroomRows.Empty;
        }

        var accepted = new List<WorkItemCost>();
        var allAcceptedRowsTrusted = true;
        foreach (var row in rows)
        {
            if (!IsValidTokenRow(row, maxTokensPerRow))
                continue;

            var trust = ClassifyHeadroomRow(row);
            if (trust == HeadroomRowTrust.Rejected)
                continue;

            accepted.Add(row);
            if (trust != HeadroomRowTrust.Trusted)
                allAcceptedRowsTrusted = false;
        }

        return accepted.Count == 0
            ? FilteredHeadroomRows.Empty
            : new FilteredHeadroomRows(accepted, allAcceptedRowsTrusted);
    }

    private static bool IsValidTokenRow(WorkItemCost row, int maxTokensPerRow)
    {
        if (row.InputTokens < 0 || row.OutputTokens < 0 || row.CachedInputTokens < 0)
            return false;

        var totalTokens = (long)row.InputTokens + row.OutputTokens;
        return totalTokens > 0 && totalTokens <= maxTokensPerRow;
    }

    internal static bool IsTrustedHeadroomRow(WorkItemCost row) =>
        ClassifyHeadroomRow(row) == HeadroomRowTrust.Trusted;

    private static HeadroomRowTrust ClassifyHeadroomRow(WorkItemCost row)
    {
        if (string.IsNullOrWhiteSpace(row.RawMetadataJson))
            return HeadroomRowTrust.Rejected;

        try
        {
            using var doc = JsonDocument.Parse(row.RawMetadataJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return HeadroomRowTrust.Rejected;

            if (root.TryGetProperty("quotaHeadroomTrusted", out var trusted)
                && trusted.ValueKind == JsonValueKind.True)
            {
                return HeadroomRowTrust.Trusted;
            }

            if (HasStdoutDerivedUsageSource(root, "usageSource")
                || HasStdoutDerivedUsageSource(root, "source"))
            {
                return HeadroomRowTrust.Untrusted;
            }

            // Older production PipelineRunner rows used the database default "{}".
            // Keep those rows visible for operator projections after upgrade, but
            // do not enforce quota decisions from unauthenticated history.
            return !root.EnumerateObject().Any()
                ? HeadroomRowTrust.Untrusted
                : HeadroomRowTrust.Rejected;
        }
        catch (JsonException)
        {
            return HeadroomRowTrust.Rejected;
        }
    }

    private static bool HasStdoutDerivedUsageSource(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var source)
        && source.ValueKind == JsonValueKind.String
        && (string.Equals(source.GetString(), "provider_metadata", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.GetString(), "agent_stream_analyser", StringComparison.OrdinalIgnoreCase));

    private enum HeadroomRowTrust
    {
        Rejected,
        Untrusted,
        Trusted,
    }

    private sealed record FilteredHeadroomRows(
        IReadOnlyList<WorkItemCost> Rows,
        bool TrustedForEnforcement)
    {
        public static FilteredHeadroomRows Empty { get; } = new([], false);
    }

    private static IReadOnlyList<double> BuildPerItemIterationSamples(
        IReadOnlyList<WorkItemCost> rows,
        int maxItems,
        int maxTokensPerIteration)
    {
        if (rows.Count == 0 || maxItems <= 0 || maxTokensPerIteration <= 0)
            return [];

        return rows
            .GroupBy(r => r.WorkItemId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Max(r => r.EndedAt))
            .Take(maxItems)
            .Select(ItemTokensPerIteration)
            .Where(tokens => tokens > 0)
            .Select(tokens => Math.Min(tokens, maxTokensPerIteration))
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

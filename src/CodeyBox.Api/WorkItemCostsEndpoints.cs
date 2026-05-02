using CodeyBox.Core;

namespace CodeyBox.Api;

internal static class WorkItemCostsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/workitems/{id}/costs", GetWorkItemCostsAsync);
        app.MapGet("/projects/{id}/costs", GetProjectCostsAsync);
    }

    private static async Task<IResult> GetWorkItemCostsAsync(
        string id,
        IWorkItemCostStore costs,
        IWorkItemStore store,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var g)) return Results.BadRequest(new { error = "invalid id" });
        var workItemId = new WorkItemId(g);

        var item = await store.GetAsync(workItemId, ct);
        if (item is null) return Results.NotFound();

        var rows = await costs.GetByWorkItemAsync(workItemId.ToString(), ct);
        return Results.Ok(BuildCostsDto(workItemId.Value.ToString("D"), rows));
    }

    private static async Task<IResult> GetProjectCostsAsync(
        string id,
        IWorkItemCostStore costs,
        string? from,
        string? to,
        CancellationToken ct)
    {
        var fromDate = TryParseIso(from) ?? DateTimeOffset.UtcNow.AddDays(-30);
        var toDate = TryParseIso(to) ?? DateTimeOffset.UtcNow;

        if (toDate <= fromDate)
            return Results.BadRequest(new { error = "'to' must be after 'from'" });

        var rows = await costs.GetByProjectAsync(id, fromDate, toDate, ct);
        return Results.Ok(BuildProjectCostsDto(id, fromDate, toDate, rows));
    }

    private static object BuildCostsDto(string workItemId, IReadOnlyList<WorkItemCost> rows)
    {
        var totalInput = rows.Sum(r => r.InputTokens);
        var totalCached = rows.Sum(r => r.CachedInputTokens);
        var totalOutput = rows.Sum(r => r.OutputTokens);
        var totalUsd = rows.Sum(r => r.EstimatedUsd);

        // byPhase breakdown
        var byPhase = rows
            .GroupBy(r => r.Phase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var byIter = g.Where(r => r.Iteration.HasValue).ToList();
                    if (byIter.Count > 0)
                    {
                        var iters = byIter
                            .GroupBy(r => r.Iteration)
                            .OrderBy(ig => ig.Key)
                            .Select(ig => new
                            {
                                iteration = ig.Key,
                                inputTokens = ig.Sum(r => r.InputTokens),
                                cachedInputTokens = ig.Sum(r => r.CachedInputTokens),
                                outputTokens = ig.Sum(r => r.OutputTokens),
                                estimatedUsd = ig.Sum(r => r.EstimatedUsd),
                            })
                            .ToList();
                        return (object)new
                        {
                            inputTokens = g.Sum(r => r.InputTokens),
                            cachedInputTokens = g.Sum(r => r.CachedInputTokens),
                            outputTokens = g.Sum(r => r.OutputTokens),
                            estimatedUsd = g.Sum(r => r.EstimatedUsd),
                            byIteration = iters,
                        };
                    }
                    return (object)new
                    {
                        inputTokens = g.Sum(r => r.InputTokens),
                        cachedInputTokens = g.Sum(r => r.CachedInputTokens),
                        outputTokens = g.Sum(r => r.OutputTokens),
                        estimatedUsd = g.Sum(r => r.EstimatedUsd),
                    };
                });

        // byAgent breakdown
        var byAgent = rows
            .GroupBy(r => new { r.AgentKind, r.ModelId })
            .Select(g => new
            {
                agent = g.Key.AgentKind,
                modelId = g.Key.ModelId,
                inputTokens = g.Sum(r => r.InputTokens),
                cachedInputTokens = g.Sum(r => r.CachedInputTokens),
                outputTokens = g.Sum(r => r.OutputTokens),
                estimatedUsd = g.Sum(r => r.EstimatedUsd),
            })
            .OrderByDescending(x => x.estimatedUsd)
            .ToList();

        return new
        {
            workItemId,
            totals = new
            {
                inputTokens = totalInput,
                cachedInputTokens = totalCached,
                outputTokens = totalOutput,
                estimatedUsd = totalUsd,
            },
            byPhase,
            byAgent,
        };
    }

    private static object BuildProjectCostsDto(
        string projectId,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyList<WorkItemCost> rows)
    {
        var totalInput = rows.Sum(r => r.InputTokens);
        var totalCached = rows.Sum(r => r.CachedInputTokens);
        var totalOutput = rows.Sum(r => r.OutputTokens);
        var totalUsd = rows.Sum(r => r.EstimatedUsd);

        var byAgent = rows
            .GroupBy(r => new { r.AgentKind, r.ModelId })
            .Select(g => new
            {
                agent = g.Key.AgentKind,
                modelId = g.Key.ModelId,
                inputTokens = g.Sum(r => r.InputTokens),
                cachedInputTokens = g.Sum(r => r.CachedInputTokens),
                outputTokens = g.Sum(r => r.OutputTokens),
                estimatedUsd = g.Sum(r => r.EstimatedUsd),
            })
            .OrderByDescending(x => x.estimatedUsd)
            .ToList();

        var byWorkItem = rows
            .GroupBy(r => r.WorkItemId)
            .Select(g => new
            {
                workItemId = g.Key,
                inputTokens = g.Sum(r => r.InputTokens),
                cachedInputTokens = g.Sum(r => r.CachedInputTokens),
                outputTokens = g.Sum(r => r.OutputTokens),
                estimatedUsd = g.Sum(r => r.EstimatedUsd),
            })
            .OrderByDescending(x => x.estimatedUsd)
            .ToList();

        return new
        {
            projectId,
            from = from.ToString("O"),
            to = to.ToString("O"),
            totals = new
            {
                inputTokens = totalInput,
                cachedInputTokens = totalCached,
                outputTokens = totalOutput,
                estimatedUsd = totalUsd,
            },
            byAgent,
            byWorkItem,
        };
    }

    private static DateTimeOffset? TryParseIso(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var dt) ? dt : null;
    }
}

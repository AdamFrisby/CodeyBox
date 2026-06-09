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
        IProjectRepository projects,
        string? from,
        string? to,
        CancellationToken ct)
    {
        ProjectId pid;
        try { pid = new ProjectId(id); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid project id" }); }

        var project = await projects.GetAsync(pid, ct);
        if (project is null) return Results.NotFound();

        var fromDate = TryParseIso(from) ?? DateTimeOffset.UtcNow.AddDays(-30);
        var toDate = TryParseIso(to) ?? DateTimeOffset.UtcNow;

        if (toDate <= fromDate)
            return Results.BadRequest(new { error = "'to' must be after 'from'" });

        var rows = await costs.GetByProjectAsync(id, fromDate, toDate, ct);
        return Results.Ok(BuildProjectCostsDto(id, fromDate, toDate, rows));
    }

    private static object BuildCostsDto(string workItemId, IReadOnlyList<WorkItemCost> rows)
    {
        var totalInput = rows.Sum(TokenUsageAccounting.TotalInputTokens);
        var totalCached = rows.Sum(r => (long)r.CachedInputTokens);
        var totalOutput = rows.Sum(r => (long)r.OutputTokens);
        var totalUsd = rows.Sum(r => r.EstimatedUsd);
        var totalElapsedMs = rows.Sum(ElapsedMs);

        // byPhase breakdown
        var byPhase = rows
            .GroupBy(r => r.Phase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var byIter = g.Where(r => r.Iteration.HasValue)
                        .GroupBy(r => r.Iteration)
                        .OrderBy(ig => ig.Key)
                        .Select(ig => new
                        {
                            iteration = ig.Key,
                            inputTokens = ig.Sum(TokenUsageAccounting.TotalInputTokens),
                            cachedInputTokens = ig.Sum(r => (long)r.CachedInputTokens),
                            outputTokens = ig.Sum(r => (long)r.OutputTokens),
                            estimatedUsd = ig.Sum(r => r.EstimatedUsd),
                            elapsedMs = ig.Sum(ElapsedMs),
                            invocationCount = ig.Count(),
                        })
                        .ToList();
                    return (object)new
                    {
                        inputTokens = g.Sum(TokenUsageAccounting.TotalInputTokens),
                        cachedInputTokens = g.Sum(r => (long)r.CachedInputTokens),
                        outputTokens = g.Sum(r => (long)r.OutputTokens),
                        estimatedUsd = g.Sum(r => r.EstimatedUsd),
                        elapsedMs = g.Sum(ElapsedMs),
                        invocationCount = g.Count(),
                        byIteration = byIter,
                    };
                });

        // byAgent breakdown
        var byAgent = rows
            .GroupBy(r => new { r.AgentKind, r.AgentInstanceId, r.ModelId })
            .Select(g => new
            {
                agent = g.Key.AgentKind,
                agentInstanceId = g.Key.AgentInstanceId,
                modelId = g.Key.ModelId,
                inputTokens = g.Sum(TokenUsageAccounting.TotalInputTokens),
                cachedInputTokens = g.Sum(r => (long)r.CachedInputTokens),
                outputTokens = g.Sum(r => (long)r.OutputTokens),
                estimatedUsd = g.Sum(r => r.EstimatedUsd),
                elapsedMs = g.Sum(ElapsedMs),
                invocationCount = g.Count(),
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
                elapsedMs = totalElapsedMs,
                invocationCount = rows.Count,
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
        var totalInput = rows.Sum(TokenUsageAccounting.TotalInputTokens);
        var totalCached = rows.Sum(r => (long)r.CachedInputTokens);
        var totalOutput = rows.Sum(r => (long)r.OutputTokens);
        var totalUsd = rows.Sum(r => r.EstimatedUsd);
        var totalElapsedMs = rows.Sum(ElapsedMs);

        var byAgent = rows
            .GroupBy(r => new { r.AgentKind, r.AgentInstanceId, r.ModelId })
            .Select(g => new
            {
                agent = g.Key.AgentKind,
                agentInstanceId = g.Key.AgentInstanceId,
                modelId = g.Key.ModelId,
                inputTokens = g.Sum(TokenUsageAccounting.TotalInputTokens),
                cachedInputTokens = g.Sum(r => (long)r.CachedInputTokens),
                outputTokens = g.Sum(r => (long)r.OutputTokens),
                estimatedUsd = g.Sum(r => r.EstimatedUsd),
                elapsedMs = g.Sum(ElapsedMs),
                invocationCount = g.Count(),
            })
            .OrderByDescending(x => x.estimatedUsd)
            .ToList();

        var byWorkItem = rows
            .GroupBy(r => r.WorkItemId)
            .OrderByDescending(g => g.Max(r => r.StartedAt))
            .Select(g => new
            {
                workItemId = g.Key,
                inputTokens = g.Sum(TokenUsageAccounting.TotalInputTokens),
                cachedInputTokens = g.Sum(r => (long)r.CachedInputTokens),
                outputTokens = g.Sum(r => (long)r.OutputTokens),
                estimatedUsd = g.Sum(r => r.EstimatedUsd),
                elapsedMs = g.Sum(ElapsedMs),
                invocationCount = g.Count(),
            })
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
                elapsedMs = totalElapsedMs,
                invocationCount = rows.Count,
            },
            byAgent,
            byWorkItem,
        };
    }

    private static long ElapsedMs(WorkItemCost row) =>
        (long)Math.Max(0, (row.EndedAt - row.StartedAt).TotalMilliseconds);

    private static DateTimeOffset? TryParseIso(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTimeOffset.TryParse(s,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var dt) ? dt : null;
    }
}

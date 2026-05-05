using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Api;

internal static class FleetEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/fleet/summary", GetFleetSummaryAsync);
    }

    private static async Task<IResult> GetFleetSummaryAsync(
        IWorkItemStore workItems,
        IProjectRepository projects,
        ILoggerFactory loggerFactory,
        IServiceProvider sp,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("CodeyBox.Api.FleetEndpoints");
        var allProjects = await projects.ListAsync(ct);

        // Two SQL aggregation passes — no work item bodies loaded into memory.
        var stateCounts = await workItems.GetFleetStateCountsAsync(ct);
        var recentOutcomes = await workItems.GetFleetRecentOutcomesAsync(5, ct);

        var countsByProject = stateCounts.ToLookup(r => r.ProjectId);
        var outcomesByProject = recentOutcomes.ToLookup(r => r.ProjectId);

        var costStore = sp.GetService<IWorkItemCostStore>();
        var now = DateTimeOffset.UtcNow;
        var costFrom = now.AddDays(-30);

        var summaries = new List<object>(allProjects.Count);
        foreach (var project in allProjects)
        {
            var projectId = project.Id.Value;
            var projectCounts = countsByProject[projectId].ToList();

            var queuedCount = projectCounts
                .Where(r => r.State == (int)WorkItemState.Queued)
                .Sum(r => r.Count);

            var inFlightCount = projectCounts
                .Where(r => IsInFlight(r.State))
                .Sum(r => r.Count);

            // Most recent in-flight state: pick the (project_id, state) row with the highest MAX(updated_at).
            // ISO-8601 strings in the same format sort lexicographically by time.
            var currentPhase = projectCounts
                .Where(r => IsInFlight(r.State))
                .OrderByDescending(r => r.MaxUpdatedAt, StringComparer.Ordinal)
                .Select(r => ((WorkItemState)r.State).ToString())
                .FirstOrDefault();

            var projectOutcomes = outcomesByProject[projectId]
                .Select(r => ((WorkItemState)r.State).ToString())
                .ToList();

            double? monthlySpendUsd = null;
            string budgetThresholdState = "unknown";
            if (costStore is not null)
            {
                try
                {
                    var costs = await costStore.GetByProjectAsync(projectId, costFrom, now, ct);
                    monthlySpendUsd = costs.Sum(c => c.EstimatedUsd);
                    budgetThresholdState = "ok";
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to load cost data for project {ProjectId}", projectId);
                }
            }

            summaries.Add(new
            {
                projectId,
                displayName = project.DisplayName,
                queuedCount,
                inFlightCount,
                currentPhase,
                recentOutcomes = projectOutcomes,
                isPaused = false,
                pausedReason = (string?)null,
                monthlySpendUsd,
                monthlyBudgetUsd = (double?)null,
                budgetThresholdState,
            });
        }

        return Results.Ok(summaries);
    }

    private static bool IsInFlight(int state) =>
        state is not ((int)WorkItemState.Queued
            or (int)WorkItemState.Done
            or (int)WorkItemState.Failed
            or (int)WorkItemState.Cancelled
            or (int)WorkItemState.AuditFailed);
}

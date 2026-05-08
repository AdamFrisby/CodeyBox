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

        // Three single-pass SQL queries — no per-project N+1.
        var stateCounts = await workItems.GetFleetStateCountsAsync(ct);
        var recentOutcomes = await workItems.GetFleetRecentOutcomesAsync(5, ct);
        var pauseStates = await workItems.GetFleetPauseStatesAsync(ct);

        var countsByProject = stateCounts.ToLookup(r => r.ProjectId);
        var outcomesByProject = recentOutcomes.ToLookup(r => r.ProjectId);

        var costStore = sp.GetService<IWorkItemCostStore>();
        var now = DateTimeOffset.UtcNow;
        var costFrom = now.AddDays(-30);

        // Single bulk cost query — avoids N+1 when cost-reporting has landed.
        Dictionary<string, double>? costByProject = null;
        if (costStore is not null)
        {
            try
            {
                var costSummary = await costStore.GetFleetCostSummaryAsync(costFrom, now, ct);
                costByProject = costSummary.ToDictionary(c => c.ProjectId, c => c.TotalUsd);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load fleet cost summary");
            }
        }

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

            var hasRecentFailures = projectOutcomes.Count(o => o is "Failed" or "AuditFailed" or "MergeConflictResolutionFailed") >= 3;

            double? monthlySpendUsd = null;
            string budgetThresholdState = "unknown";
            if (costByProject is not null)
            {
                monthlySpendUsd = costByProject.TryGetValue(projectId, out var spend) ? spend : 0.0;
                budgetThresholdState = "ok";
            }

            var isPaused = pauseStates.TryGetValue(projectId, out var paused) && paused;

            summaries.Add(new
            {
                projectId,
                displayName = project.DisplayName,
                queuedCount,
                inFlightCount,
                currentPhase,
                recentOutcomes = projectOutcomes,
                isPaused,
                hasRecentFailures,
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
            or (int)WorkItemState.AuditFailed
            or (int)WorkItemState.MergeConflictResolutionFailed);
}

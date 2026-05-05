using CodeyBox.Core;
using CodeyBox.Orchestrator;

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
        IServiceProvider sp,
        CancellationToken ct)
    {
        var allProjects = await projects.ListAsync(ct);

        // One pass over work_items for all aggregations.
        var allItems = new List<WorkItem>();
        await foreach (var item in workItems.ListAsync(ct))
            allItems.Add(item);

        var costStore = sp.GetService<IWorkItemCostStore>();
        var now = DateTimeOffset.UtcNow;
        var costFrom = now.AddDays(-30);

        var summaries = new List<object>(allProjects.Count);
        foreach (var project in allProjects)
        {
            var projectId = project.Id.Value;
            var projectItems = allItems.Where(i => i.ProjectId.Value == projectId).ToList();

            var queuedCount = projectItems.Count(i => i.State == WorkItemState.Queued);
            var inFlightCount = projectItems.Count(i => IsInFlight(i.State));

            var currentPhase = projectItems
                .Where(i => IsInFlight(i.State))
                .OrderByDescending(i => i.UpdatedAt)
                .Select(i => i.State.ToString())
                .FirstOrDefault();

            var recentOutcomes = projectItems
                .Where(i => IsTerminal(i.State))
                .OrderByDescending(i => i.UpdatedAt)
                .Take(5)
                .Select(i => i.State.ToString())
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
                catch
                {
                    // non-critical — fall through with null spend
                }
            }

            summaries.Add(new
            {
                projectId,
                displayName = project.DisplayName,
                queuedCount,
                inFlightCount,
                currentPhase,
                recentOutcomes,
                isPaused = false,     // project_queue_state not yet landed
                pausedReason = (string?)null,
                monthlySpendUsd,
                monthlyBudgetUsd = (double?)null,   // budget-alerts work item not yet landed
                budgetThresholdState,
            });
        }

        return Results.Ok(summaries);
    }

    private static bool IsInFlight(WorkItemState state) =>
        state is not (WorkItemState.Queued
            or WorkItemState.Done
            or WorkItemState.Failed
            or WorkItemState.Cancelled
            or WorkItemState.AuditFailed);

    private static bool IsTerminal(WorkItemState state) =>
        state is WorkItemState.Done
            or WorkItemState.Failed
            or WorkItemState.Cancelled
            or WorkItemState.AuditFailed;
}

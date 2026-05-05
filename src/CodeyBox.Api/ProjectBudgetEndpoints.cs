using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class ProjectBudgetEndpoints
{
    public static void Map(WebApplication app)
    {
        var projects = app.MapGroup("/projects");
        projects.MapGet("/{id}/budget", GetBudgetAsync);
        projects.MapPost("/{id}/queue/pause", PauseProjectQueueAsync);
        projects.MapPost("/{id}/queue/resume", ResumeProjectQueueAsync);
    }

    /// <summary>
    /// Returns the monthly cost budget status for a project: current spend,
    /// budget ceiling, percentage, threshold state, and the 30-day window.
    /// </summary>
    private static async Task<IResult> GetBudgetAsync(
        string id,
        IProjectRepository projects,
        IWorkItemCostStore costStore,
        IQueueController queueController,
        CancellationToken ct)
    {
        ProjectId pid;
        try { pid = new ProjectId(id); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid project id" }); }
        var project = await projects.GetAsync(pid, ct);
        if (project is null) return Results.NotFound();

        var budget = project.Budget;
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddDays(-30);

        decimal spendUsd = 0;
        if (budget.MonthlyCostBudgetUsd > 0)
        {
            try
            {
                spendUsd = await costStore.SumEstimatedUsdAsync(project.Id.Value, windowStart, now, ct);
            }
            catch (Exception)
            {
                // Table may not exist yet; treat as zero spend.
            }
        }

        var pct = budget.MonthlyCostBudgetUsd > 0
            ? (double)(spendUsd / budget.MonthlyCostBudgetUsd * 100m)
            : 0.0;

        string thresholdState = "ok";
        if (budget.MonthlyCostBudgetUsd > 0)
        {
            if (budget.CostHardCapPct > 0 && pct >= budget.CostHardCapPct)
                thresholdState = "exceeded";
            else if (budget.CostWarningThresholdPct > 0 && pct >= budget.CostWarningThresholdPct)
                thresholdState = "warning";
        }

        var projState = await queueController.GetProjectStateAsync(pid, ct);

        return Results.Ok(new
        {
            projectId = project.Id.Value,
            monthlyBudgetUsd = budget.MonthlyCostBudgetUsd,
            currentSpendUsd = spendUsd,
            pct,
            warningThresholdPct = budget.CostWarningThresholdPct,
            hardCapPct = budget.CostHardCapPct,
            thresholdState,
            windowStart,
            windowEnd = now,
            projectQueue = new
            {
                paused = projState?.Paused ?? false,
                pausedAt = projState?.PausedAt,
                pausedReason = projState?.PausedReason,
            },
        });
    }

    private static async Task<IResult> PauseProjectQueueAsync(
        string id,
        PauseProjectQueueRequest body,
        IProjectRepository projects,
        IQueueController queueController,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        ProjectId pid;
        try { pid = new ProjectId(id); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid project id" }); }
        var project = await projects.GetAsync(pid, ct);
        if (project is null) return Results.NotFound();

        if (string.IsNullOrWhiteSpace(body.Reason))
            return Results.BadRequest(new { error = "reason is required" });
        if (body.Reason.Any(char.IsControl))
            return Results.BadRequest(new { error = "reason must not contain control characters" });
        if (body.Reason.Length > 500)
            return Results.BadRequest(new { error = "reason must be <= 500 chars" });

        await queueController.PauseProjectAsync(pid, body.Reason, ct);

        _ = webhooks.PublishAsync(new WebhookEvent
        {
            Event = "project.queue_paused",
            Project = project,
            Details = new { projectId = pid.Value, pausedAt = DateTimeOffset.UtcNow, reason = body.Reason, pausedBy = "api" },
        }, CancellationToken.None);

        var projState = await queueController.GetProjectStateAsync(pid, ct);
        return Results.Ok(new
        {
            projectId = pid.Value,
            paused = projState?.Paused ?? true,
            pausedAt = projState?.PausedAt,
            pausedReason = projState?.PausedReason,
        });
    }

    private static async Task<IResult> ResumeProjectQueueAsync(
        string id,
        IProjectRepository projects,
        IQueueController queueController,
        IWebhookDispatcher webhooks,
        CancellationToken ct)
    {
        ProjectId pid;
        try { pid = new ProjectId(id); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "invalid project id" }); }
        var project = await projects.GetAsync(pid, ct);
        if (project is null) return Results.NotFound();

        await queueController.ResumeProjectAsync(pid, ct);

        _ = webhooks.PublishAsync(new WebhookEvent
        {
            Event = "project.queue_resumed",
            Project = project,
            Details = new { projectId = pid.Value, resumedAt = DateTimeOffset.UtcNow },
        }, CancellationToken.None);

        return Results.Ok(new
        {
            projectId = pid.Value,
            paused = false,
            pausedAt = (DateTimeOffset?)null,
            pausedReason = (string?)null,
        });
    }
}

public sealed record PauseProjectQueueRequest(string Reason = "");

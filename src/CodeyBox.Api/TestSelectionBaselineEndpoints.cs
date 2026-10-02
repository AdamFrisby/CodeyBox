using CodeyBox.Core;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Mvc;

namespace CodeyBox.Api;

/// <summary>
/// Read-only baseline-production status: per project, the newest stored
/// baseline (commit, age, size) plus the last production outcome (duration
/// or error) and the scheduler state (pending commit, running flag), so
/// coverage-shadow calibration progress is observable without polling logs.
/// </summary>
internal static class TestSelectionBaselineEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/audit/test-selection/baseline", GetBaselineStatusAsync);
    }

    private static async Task<IResult> GetBaselineStatusAsync(
        [FromQuery] string? projectId,
        [FromServices] ITestSelectionBaselineStore store,
        [FromServices] TestSelectionBaselineScheduler scheduler,
        [FromServices] IProjectRepository projects,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            ProjectId parsed;
            try
            {
                parsed = new ProjectId(projectId.Trim());
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid projectId",
                    detail: ex.Message);
            }

            var project = await projects.GetAsync(parsed, ct).ConfigureAwait(false);
            if (project is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Unknown project",
                    detail: $"No project '{parsed.Value}' is configured.");
            }

            return Results.Ok(await BuildStatusAsync(store, scheduler, parsed.Value, now, ct).ConfigureAwait(false));
        }

        var all = await projects.ListAsync(ct).ConfigureAwait(false);
        var statuses = new List<object>(all.Count);
        foreach (var project in all.OrderBy(p => p.Id.Value, StringComparer.Ordinal))
        {
            statuses.Add(await BuildStatusAsync(store, scheduler, project.Id.Value, now, ct).ConfigureAwait(false));
        }

        return Results.Ok(statuses);
    }

    private static async Task<object> BuildStatusAsync(
        ITestSelectionBaselineStore store,
        TestSelectionBaselineScheduler scheduler,
        string projectId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var stored = await store.ListAsync(projectId, ct).ConfigureAwait(false);
        TestSelectionBaselineStored? latest = null;
        foreach (var candidate in stored)
        {
            if (latest is null || candidate.ProducedAtUtc > latest.ProducedAtUtc)
                latest = candidate;
        }

        var failure = await store.GetLastFailureAsync(projectId, ct).ConfigureAwait(false);
        return new
        {
            projectId,
            latestCommit = latest?.Commit,
            producedAtUtc = latest?.ProducedAtUtc,
            ageHours = latest is null ? (double?)null : (now - latest.ProducedAtUtc).TotalHours,
            sizeBytes = latest?.SizeBytes,
            testCount = latest?.TestCount,
            retainedCount = stored.Count,
            lastDurationMs = failure is null ? (long?)null : (long)failure.Duration.TotalMilliseconds,
            lastError = failure?.Error,
            pendingCommit = scheduler.GetPendingCommit(projectId),
            isRunning = scheduler.IsRunning(projectId),
        };
    }
}

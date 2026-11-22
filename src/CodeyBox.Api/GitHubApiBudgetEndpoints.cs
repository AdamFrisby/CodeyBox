using CodeyBox.Upstream.GitHub;

namespace CodeyBox.Api;

internal static class GitHubApiBudgetEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/github-api-budget", GetBudgetAsync);
    }

    /// <summary>
    /// GET /admin/github-api-budget — current shared GitHub REST budget per
    /// credential: remaining requests, window reset, active rate-limit blocks,
    /// and the top consuming endpoints. Budget keys are truncated token
    /// hashes, never tokens.
    /// </summary>
    private static IResult GetBudgetAsync(GitHubApiBudgetTracker tracker)
    {
        var budgets = tracker.Snapshot();
        return Results.Ok(new
        {
            credentials = budgets.Select(b => new
            {
                b.BudgetKey,
                b.RequestsObserved,
                remaining = b.Remaining,
                resetAt = b.ResetAt,
                blockedUntil = b.BlockedUntil,
                blockedReason = b.BlockedReason,
                topConsumers = b.TopConsumers.Select(kv => new { path = kv.Key, requests = kv.Value }),
            }),
        });
    }
}

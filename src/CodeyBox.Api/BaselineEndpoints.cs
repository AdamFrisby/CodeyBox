using CodeyBox.Core;
using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

/// <summary>
/// Operator surface for the B1 content-hashed baseline image system. Exposes
/// the most recent <see cref="BaselineImageReaper"/> sweep so operators can
/// see — at a glance — which baselines exist, which ones are still pinned by
/// at least one in-flight work item, which are orphaned but inside the grace
/// window, and how many work items reference each. Two endpoints:
/// <list type="bullet">
///   <item><c>GET /baselines</c> — full report from the latest sweep,
///   plus a per-baseline list of referencing work items.</item>
///   <item><c>GET /admin/baseline-images</c> — terse summary for fleet
///   dashboards (counts only, no work-item list).</item>
/// </list>
/// </summary>
internal static class BaselineEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/baselines", GetBaselinesAsync);
        app.MapGet("/admin/baseline-images", GetBaselineSummary);
    }

    /// <summary>
    /// Returns the latest baseline image sweep with referencing work items.
    /// Returned shape:
    /// <code>
    /// {
    ///   "baselines": [
    ///     {
    ///       "name": "cb-baseline-abc123",
    ///       "isLive": true,
    ///       "firstObservedOrphanAt": null,
    ///       "ageInGraceMinutes": null,
    ///       "workItems": [{"id":"…","title":"…","state":3}]
    ///     }
    ///   ],
    ///   "sweepEntries": 12
    /// }
    /// </code>
    /// </summary>
    private static async Task<IResult> GetBaselinesAsync(
        BaselineImageReaper reaper,
        IWorkItemStore store,
        CancellationToken ct)
    {
        var report = reaper.GetLatestReport();
        var entries = new List<object>(report.Count);
        foreach (var entry in report)
        {
            var workItems = await store.ListWorkItemsForBaselineAsync(entry.Name, ct);
            entries.Add(new
            {
                name = entry.Name,
                isLive = entry.IsLive,
                firstObservedOrphanAt = entry.FirstObservedOrphanAt,
                ageInGraceMinutes = entry.AgeInGrace?.TotalMinutes is double m
                    ? (double?)Math.Round(m, 1)
                    : null,
                workItems = workItems.Select(w => new
                {
                    id = w.Id.ToString(),
                    title = w.Title,
                    state = (int)w.State,
                }).ToArray(),
            });
        }
        return Results.Ok(new
        {
            baselines = entries,
            sweepEntries = report.Count,
        });
    }

    /// <summary>
    /// Compact summary intended for fleet dashboards. Counts only — no work-
    /// item identifiers — so it stays cheap to render frequently.
    /// </summary>
    private static IResult GetBaselineSummary(BaselineImageReaper reaper)
    {
        var report = reaper.GetLatestReport();
        return Results.Ok(new
        {
            total = report.Count,
            live = report.Count(r => r.IsLive),
            orphanedInGrace = report.Count(r => !r.IsLive),
            entries = report.Select(r => new
            {
                name = r.Name,
                isLive = r.IsLive,
                ageInGraceMinutes = r.AgeInGrace?.TotalMinutes is double m
                    ? (double?)Math.Round(m, 1)
                    : null,
            }).ToArray(),
        });
    }
}

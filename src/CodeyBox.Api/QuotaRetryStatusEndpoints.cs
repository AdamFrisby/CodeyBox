using CodeyBox.Core;

namespace CodeyBox.Api;

internal static class QuotaRetryStatusEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/quota-retry-status", GetQuotaRetryStatusAsync);
    }

    private static async Task<IResult> GetQuotaRetryStatusAsync(
        IWorkItemStore workItems,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var buckets = new Dictionary<(WorkItemState State, int? HoursSinceDeadline), int>();

        await foreach (var item in workItems.ListByStateAsync(WorkItemState.WaitingForQuotaReset, ct))
            AddBucket(item);

        await foreach (var item in workItems.ListByStateAsync(WorkItemState.Failed, ct))
        {
            if (item.FailureKind == "quota")
                AddBucket(item);
        }

        var rows = buckets
            .OrderBy(kv => kv.Key.State.ToString(), StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.HoursSinceDeadline ?? int.MinValue)
            .Select(kv => new
            {
                state = kv.Key.State.ToString(),
                hoursSinceNextQuotaRetryAtDeadline = kv.Key.HoursSinceDeadline,
                count = kv.Value,
            })
            .ToArray();

        return Results.Ok(new
        {
            generatedAt = now,
            totalParked = rows.Sum(r => r.count),
            buckets = rows,
        });

        void AddBucket(WorkItem item)
        {
            var hoursSinceDeadline = item.NextQuotaRetryAt is { } nextRetryAt
                ? (int)Math.Floor((now - nextRetryAt).TotalHours)
                : (int?)null;
            var key = (item.State, hoursSinceDeadline);
            buckets[key] = buckets.TryGetValue(key, out var count) ? count + 1 : 1;
        }
    }
}

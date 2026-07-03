using CodeyBox.Core;
using Microsoft.AspNetCore.Mvc;

namespace CodeyBox.Api;

internal static class SandboxResourceUsageEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/sandbox-resource-usage", GetAggregateAsync);
    }

    private static async Task<IResult> GetAggregateAsync(
        ISandboxResourceUsageStore store,
        [FromQuery(Name = "n")] int? n,
        [FromQuery] DateTimeOffset? since,
        CancellationToken ct)
    {
        var limit = Math.Clamp(n ?? 100, 1, 1000);
        var records = await store.ListRecentAsync(limit, since?.ToUniversalTime(), ct);

        var peakRam = records.Select(r => r.PeakRamMb).WhereNotNull().Order().ToArray();
        var cpu = records.Select(r => r.AvgCpuPercent).WhereNotNull().Order().ToArray();
        var netRx = records.Select(r => r.NetRxMb).WhereNotNull().Order().ToArray();
        var netTx = records.Select(r => r.NetTxMb).WhereNotNull().Order().ToArray();
        var netTotal = records
            .Select(r => r.NetRxMb.HasValue || r.NetTxMb.HasValue
                ? (double?)((r.NetRxMb ?? 0) + (r.NetTxMb ?? 0))
                : null)
            .WhereNotNull()
            .Order()
            .ToArray();

        return Results.Ok(new
        {
            recordCount = records.Count,
            requestedLimit = limit,
            peakRamMb = new
            {
                p50 = Percentile(peakRam, 0.50),
                p95 = Percentile(peakRam, 0.95),
            },
            avgCpuPct = new
            {
                avg = Average(cpu),
                p95 = Percentile(cpu, 0.95),
            },
            netRxMb = new
            {
                total = Sum(netRx),
                p50 = Percentile(netRx, 0.50),
                p95 = Percentile(netRx, 0.95),
            },
            netTxMb = new
            {
                total = Sum(netTx),
                p50 = Percentile(netTx, 0.50),
                p95 = Percentile(netTx, 0.95),
            },
            netTotalMb = new
            {
                total = Sum(netTotal),
                p50 = Percentile(netTotal, 0.50),
                p95 = Percentile(netTotal, 0.95),
            },
            newestCapturedAt = records.Count == 0 ? (DateTimeOffset?)null : records.Max(r => r.CapturedAt),
            oldestCapturedAt = records.Count == 0 ? (DateTimeOffset?)null : records.Min(r => r.CapturedAt),
        });
    }

    private static double? Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return null;
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }

    private static double? Average(double[] values) =>
        values.Length == 0 ? null : values.Average();

    private static double? Sum(double[] values) =>
        values.Length == 0 ? null : values.Sum();

    private static IEnumerable<double> WhereNotNull(this IEnumerable<double?> values)
    {
        foreach (var value in values)
        {
            if (value.HasValue)
                yield return value.Value;
        }
    }
}

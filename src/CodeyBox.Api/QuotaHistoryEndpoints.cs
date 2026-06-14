using CodeyBox.Core;
using Microsoft.AspNetCore.Mvc;

namespace CodeyBox.Api;

/// <summary>
/// REST surface for the per-agent quota time-series captured by the
/// statistics plugin. The endpoint resolves <see cref="IQuotaTimeSeriesStore"/>
/// from DI; if no implementation is registered (the statistics plugin is not
/// loaded or has not yet initialised), it returns 503 with a clear body
/// rather than 404 — the route exists, the data backend just isn't online.
///
/// <para>Filter parameters are deliberately a strict subset of
/// <see cref="QuotaTimeSeriesFilter"/>: extending the contract later is
/// additive (new optional query strings) and stays compatible with operators
/// scripting against the v1 surface.</para>
/// </summary>
internal static class QuotaHistoryEndpoints
{
    private const int DefaultLimit = 1000;
    private const int MaxLimit = 50_000;

    public static void Map(WebApplication app)
    {
        app.MapGet("/quota/history", GetHistoryAsync);
    }

    private static async Task<IResult> GetHistoryAsync(
        [FromQuery] string? agent,
        [FromQuery(Name = "window")] string? window,
        [FromQuery(Name = "model")] string? model,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int? limit,
        [FromQuery] bool? raw,
        [FromServices] IQuotaTimeSeriesStore? store,
        CancellationToken ct)
    {
        if (store is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Quota time-series unavailable",
                detail: "The statistics plugin is not loaded. Add 'codeybox.statistics' to CodeyBox:Plugins:Allowlist and point CodeyBox:Plugins:PackageDirectories at the plugin's install directory.");
        }

        var filter = new QuotaTimeSeriesFilter
        {
            Agent = string.IsNullOrWhiteSpace(agent) ? null : agent.Trim(),
            WindowName = string.IsNullOrWhiteSpace(window) ? null : window.Trim(),
            ModelId = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            FromUtc = from?.ToUniversalTime(),
            ToUtc = to?.ToUniversalTime(),
            Limit = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit),
        };

        if (filter.FromUtc.HasValue && filter.ToUtc.HasValue && filter.FromUtc >= filter.ToUtc)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid time range",
                detail: "'from' must be strictly less than 'to'.");
        }

        if (raw == true)
        {
            var rawRows = await store.QueryRawAsync(filter, ct);
            return Results.Ok(new
            {
                count = rawRows.Count,
                rows = rawRows,
            });
        }

        var rows = await store.QueryAsync(filter, ct);
        return Results.Ok(new
        {
            count = rows.Count,
            rows,
        });
    }
}

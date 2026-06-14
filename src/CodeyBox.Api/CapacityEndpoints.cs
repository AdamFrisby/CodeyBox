using CodeyBox.Core;
using Microsoft.AspNetCore.Mvc;

namespace CodeyBox.Api;

/// <summary>
/// REST surface for the subscription capacity analyser. Joins the captured
/// quota-snapshot time-series against recorded token usage to estimate how
/// many tokens / requests a full subscription window can hold.
///
/// <para>The endpoint resolves <see cref="ICapacityCalculator"/> from DI; if
/// no implementation is registered (the statistics plugin is not loaded), it
/// returns 503 with a clear body rather than 404 — the route exists, the
/// data backend just isn't online.</para>
///
/// <para>Filter parameters are a strict subset of <see cref="CapacityFilter"/>
/// — extending the contract later is additive (new optional query strings)
/// and stays compatible with operators scripting against the v1 surface.</para>
/// </summary>
internal static class CapacityEndpoints
{
    private const int MaxHorizonHours = 24 * 60; // 60 days; matches the calculator's own clamp

    public static void Map(WebApplication app)
    {
        app.MapGet("/stats/capacity", GetCapacityAsync);
    }

    private static async Task<IResult> GetCapacityAsync(
        [FromQuery] string? agent,
        [FromQuery(Name = "window")] string? window,
        [FromQuery(Name = "model")] string? model,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery(Name = "minDeltaPct")] double? minDeltaPct,
        [FromQuery(Name = "includeIntervals")] bool? includeIntervals,
        [FromServices] ICapacityCalculator? calculator,
        CancellationToken ct)
    {
        if (calculator is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Capacity analysis unavailable",
                detail: "The statistics plugin is not loaded. Add 'codeybox.statistics' to CodeyBox:Plugins:Allowlist and point CodeyBox:Plugins:PackageDirectories at the plugin's install directory.");
        }

        if (from.HasValue && to.HasValue && from >= to)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid time range",
                detail: "'from' must be strictly less than 'to'.");
        }

        if (from.HasValue && to.HasValue && (to.Value - from.Value).TotalHours > MaxHorizonHours)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Time range too large",
                detail: $"'to - from' must not exceed {MaxHorizonHours} hours ({MaxHorizonHours / 24} days).");
        }

        var filter = new CapacityFilter
        {
            Agent = string.IsNullOrWhiteSpace(agent) ? null : agent.Trim(),
            WindowName = string.IsNullOrWhiteSpace(window) ? null : window.Trim(),
            ModelId = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
            FromUtc = from?.ToUniversalTime(),
            ToUtc = to?.ToUniversalTime(),
            MinDeltaPct = minDeltaPct is { } md && md >= 0 ? md : 0.25,
            IncludeIntervals = includeIntervals ?? true,
        };

        var report = await calculator.ComputeAsync(filter, ct);
        return Results.Ok(report);
    }
}

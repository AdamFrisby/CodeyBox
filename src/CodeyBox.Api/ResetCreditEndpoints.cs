using CodeyBox.Core;
using Microsoft.AspNetCore.Mvc;

namespace CodeyBox.Api;

/// <summary>
/// REST surface for the banked reset-credit expiry tracker. Derives, from the
/// sampled <c>rate_limit_reset_credits.available_count</c> time-series, when
/// each banked quota-reset credit was granted and when it expires — so an
/// operator can spend a credit before the provider silently expires it.
///
/// <para>The endpoint resolves <see cref="IResetCreditExpiryEstimator"/> from
/// DI; if no implementation is registered (the statistics plugin is not
/// loaded) it returns 503 with a clear body rather than 404 — the route
/// exists, the data backend just isn't online. Mirrors
/// <c>/quota/history</c> and <c>/stats/capacity</c>.</para>
/// </summary>
internal static class ResetCreditEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/quota/reset-credits", GetResetCreditsAsync);
    }

    private static async Task<IResult> GetResetCreditsAsync(
        [FromQuery] string? agent,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromServices] IResetCreditExpiryEstimator? estimator,
        CancellationToken ct)
    {
        if (estimator is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Reset-credit expiry unavailable",
                detail: "The statistics plugin is not loaded. Add 'codeybox.statistics' to CodeyBox:Plugins:Allowlist and point CodeyBox:Plugins:PackageDirectories at the plugin's install directory.");
        }

        if (from.HasValue && to.HasValue && from >= to)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid time range",
                detail: "'from' must be strictly less than 'to'.");
        }

        var query = new ResetCreditExpiryQuery
        {
            Agent = string.IsNullOrWhiteSpace(agent) ? null : agent.Trim(),
            FromUtc = from?.ToUniversalTime(),
            ToUtc = to?.ToUniversalTime(),
        };

        var report = await estimator.EstimateAsync(query, ct);
        return Results.Ok(report);
    }
}

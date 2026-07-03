using CodeyBox.Core;
using Microsoft.AspNetCore.Mvc;

namespace CodeyBox.Api;

/// <summary>
/// REST surface for the reset-optimality advisor. Composes the live quota
/// snapshot (1/5) and the derived banked-credit expiry (2/5) into a
/// report-only <see cref="ResetSpendAdvice"/>: whether to spend a banked
/// quota-reset credit now, why, and — when advised — the window to spend in.
///
/// <para>Report-only: the endpoint neither notifies nor triggers a reset. It
/// resolves <see cref="IResetOptimalityAdvisor"/> from DI; if no implementation
/// is registered (the statistics plugin is not loaded) it returns 503 with a
/// clear body rather than 404 — the route exists, the data backend just isn't
/// online. Mirrors <c>/quota/reset-credits</c> and <c>/quota/history</c>.</para>
/// </summary>
internal static class ResetAdviceEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/quota/reset-advice", GetResetAdviceAsync);
    }

    private static async Task<IResult> GetResetAdviceAsync(
        [FromQuery] string? agent,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromServices] IResetOptimalityAdvisor? advisor,
        CancellationToken ct)
    {
        if (advisor is null)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Reset advice unavailable",
                detail: "The statistics plugin is not loaded. Add 'codeybox.statistics' to CodeyBox:Plugins:Allowlist and point CodeyBox:Plugins:PackageDirectories at the plugin's install directory.");
        }

        if (from.HasValue && to.HasValue && from >= to)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid time range",
                detail: "'from' must be strictly less than 'to'.");
        }

        var request = new ResetAdviceRequest
        {
            Agent = string.IsNullOrWhiteSpace(agent) ? null : agent.Trim(),
            FromUtc = from?.ToUniversalTime(),
            ToUtc = to?.ToUniversalTime(),
        };

        var advice = await advisor.AdviseAsync(request, ct);
        return Results.Ok(advice);
    }
}

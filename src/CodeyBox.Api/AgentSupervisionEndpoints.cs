using CodeyBox.Orchestrator;

namespace CodeyBox.Api;

internal static class AgentSupervisionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/agent-supervision");
        group.MapGet("/sessions", ListSessionsAsync);
        group.MapPost("/sessions/{sessionId}/injections", InjectAsync);
    }

    private static async Task<IResult> ListSessionsAsync(
        IAgentSupervisionService supervision,
        CancellationToken ct)
    {
        var sessions = await supervision.ListSessionsAsync(ct);
        return Results.Ok(new
        {
            enabled = supervision.Enabled,
            sessions,
        });
    }

    private static async Task<IResult> InjectAsync(
        string sessionId,
        AgentSupervisionInjectionRequest request,
        IAgentSupervisionService supervision,
        HttpContext ctx,
        CancellationToken ct)
    {
        var actor = string.IsNullOrWhiteSpace(request.Actor)
            ? $"http:{ctx.Connection.RemoteIpAddress}"
            : request.Actor;
        var result = await supervision.EnqueueInjectionAsync(
            sessionId,
            request with { Actor = actor },
            ct);

        if (result.Accepted)
            return Results.Accepted(value: result);

        return result.Status switch
        {
            "disabled" => Results.Json(result, statusCode: StatusCodes.Status403Forbidden),
            "not_found" => Results.NotFound(result),
            "closed" => Results.Conflict(result),
            "queue_full" => Results.Json(result, statusCode: StatusCodes.Status429TooManyRequests),
            _ => Results.BadRequest(result),
        };
    }
}

using System.Security.Cryptography;
using System.Text;
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
        HttpContext ctx,
        CancellationToken ct,
        int? skip = null,
        int? take = null,
        bool? includeOutputTail = null,
        int? outputTailMaxChars = null,
        int? recentCommandsLimit = null)
    {
        var query = new AgentSupervisionListQuery(
            Skip: skip,
            Take: take,
            IncludeOutputTail: includeOutputTail ?? true,
            OutputTailMaxChars: outputTailMaxChars,
            RecentCommandsLimit: recentCommandsLimit);
        var page = await supervision.ListSessionsAsync(query, ct);
        return Results.Ok(new
        {
            enabled = page.Enabled,
            total = page.Total,
            skip = page.Skip,
            take = page.Take,
            sessions = page.Sessions,
        });
    }

    private static async Task<IResult> InjectAsync(
        string sessionId,
        AgentSupervisionInjectionRequest request,
        IAgentSupervisionService supervision,
        HttpContext ctx,
        CancellationToken ct)
    {
        // Server-derived authoritative actor. The orchestrator's bearer-token
        // auth layer does not bind a per-user identity to the request, so the
        // client-supplied actor is treated as a display label only —
        // appended to the authoritative principal so the audit trail
        // identifies the actual caller (bearer-token fingerprint + remote IP)
        // and not whoever the request body claimed to be.
        var clientLabel = string.IsNullOrWhiteSpace(request.Actor) ? null : request.Actor!.Trim();
        if (clientLabel is not null && clientLabel.Length > 80)
            clientLabel = clientLabel[..80];
        var authoritative = ResolveAuthoritativeActor(ctx);
        var actor = clientLabel is null
            ? authoritative
            : $"{authoritative} ({clientLabel})";

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

    private static string ResolveAuthoritativeActor(HttpContext ctx)
    {
        // Prefer an authenticated user identity (if a future auth scheme is
        // registered) over the bearer-token fingerprint fallback.
        var name = ctx.User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(name))
            return $"user:{name}";

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var fingerprint = FingerprintAuth(ctx);
        return fingerprint is null
            ? $"anon:{ip}"
            : $"apikey:{fingerprint}@{ip}";
    }

    private static string? FingerprintAuth(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var values))
            return null;
        var raw = values.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var token = raw[prefix.Length..].Trim();
        if (token.Length == 0)
            return null;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        // Short, non-reversible fingerprint that still distinguishes
        // operators using different API keys.
        return Convert.ToHexString(bytes.AsSpan(0, 6)).ToLowerInvariant();
    }
}

using CodeyBox.Majordomo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace CodeyBox.Api.Majordomo;

/// <summary>
/// Registration for the majordomo MCP surface: the server publishes exactly
/// <see cref="MajordomoTools.All"/> — no more, no fewer — over stateless
/// streamable HTTP, gated to the configured majordomo API-client identity.
/// </summary>
internal static class MajordomoMcpRegistration
{
    /// <summary>The route the majordomo MCP server listens on.</summary>
    public const string RoutePattern = "/mcp/majordomo";

    public static void AddMajordomoMcp(this IServiceCollection services)
    {
        services.AddSingleton<MajordomoTurnLedger>();
        services.AddSingleton<MajordomoReadBackend>();
        services.AddSingleton<MajordomoMutateBackend>();
        services.AddSingleton<MajordomoExecutor>();
        services.AddHttpContextAccessor();

        // Fail composition if any vocabulary descriptor lacks a handler —
        // publication of an unwired tool must surface here, not per call.
        MajordomoExecutor.VerifyVocabularyWiring();

        // Stateless streamable HTTP: no server-side session state, so the
        // per-identity turn ledger is the only mutation-budget accounting and
        // no session can outlive the credential that opened it.
        services.AddMcpServer()
            .WithHttpTransport(transport => transport.Stateless = true)
            .WithTools(MajordomoTools.All
                .Select(descriptor => (McpServerTool)new MajordomoMcpTool(descriptor))
                .ToList());
    }

    /// <summary>
    /// Maps the MCP endpoint and restricts it to the majordomo API-client
    /// identity. The API-key middleware has already authenticated the request;
    /// this filter additionally requires the caller to be the named majordomo
    /// client — the operator key and other named clients are refused — so the
    /// majordomo's calls are attributable and its credential is revocable on
    /// its own.
    /// </summary>
    public static void MapMajordomoMcp(this WebApplication app)
    {
        app.MapMcp(RoutePattern).AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices
                .GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>()
                .CurrentValue;

            if (!ApiKeyAuth.TryGetPrincipal(context.HttpContext, out var principal) || principal is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status401Unauthorized,
                    title: "unauthenticated");
            }

            var allowed = ApiKeyAuth.IsAuthenticationDisabled(principal)
                || string.Equals(principal.Name, options.ClientName, StringComparison.Ordinal);
            if (!allowed)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "forbidden",
                    detail: $"the majordomo endpoint requires the '{options.ClientName}' API client identity");
            }

            return await next(context);
        });
    }
}

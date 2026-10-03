using CodeyBox.Majordomo;
using CodeyBox.Orchestrator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        services.AddSingleton<IMajordomoProposalStore>(sp =>
        {
            var path = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.StateDatabasePath;
            return new SqliteMajordomoProposalStore(
                path,
                sp.GetRequiredService<SqliteDatabaseWriteGateFactory>(),
                sp.GetService<ILogger<SqliteMajordomoProposalStore>>());
        });
        services.AddSingleton<IMajordomoConversationStore>(sp =>
        {
            var path = sp.GetRequiredService<IOptions<CodeyBoxOptions>>().Value.StateDatabasePath;
            return new SqliteMajordomoConversationStore(
                path,
                sp.GetRequiredService<SqliteDatabaseWriteGateFactory>(),
                sp.GetService<ILogger<SqliteMajordomoConversationStore>>());
        });
        services.AddSingleton<MajordomoProposalService>(sp =>
            new MajordomoProposalService(
                sp.GetRequiredService<IMajordomoProposalStore>(),
                sp.GetRequiredService<MajordomoMutateBackend>(),
                sp.GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<MajordomoAutonomySwitch>();
        services.AddSingleton<MajordomoExecutor>(sp => new MajordomoExecutor(
            sp.GetRequiredService<MajordomoReadBackend>(),
            sp.GetRequiredService<MajordomoMutateBackend>(),
            sp.GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>(),
            sp.GetRequiredService<MajordomoTurnLedger>(),
            sp.GetRequiredService<MajordomoProposalService>(),
            sp.GetRequiredService<IHttpContextAccessor>(),
            sp.GetRequiredService<MajordomoAutonomySwitch>(),
            sp.GetRequiredService<IMajordomoConversationStore>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ILogger<MajordomoExecutor>>()));
        services.AddSingleton<MajordomoWakeupCoordinator>(sp =>
            new MajordomoWakeupCoordinator(
                sp.GetRequiredService<IMajordomoConversationStore>(),
                () => sp.GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>().CurrentValue.ToWakeupOptions(),
                () => sp.GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>().CurrentValue.ToPolicy(),
                () => sp.GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>().CurrentValue.ToHistoryOptions(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddHostedService(sp => new MajordomoWakeupService(
            sp.GetRequiredService<MajordomoWakeupCoordinator>(),
            sp.GetRequiredService<IOptionsMonitor<MajordomoServerOptions>>(),
            sp.GetRequiredService<MajordomoReadBackend>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<MajordomoWakeupService>()));
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

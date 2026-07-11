using System.Security.Cryptography;
using System.Text;

namespace CodeyBox.Api;

/// <summary>
/// Tiny bearer-token middleware. The expected token is read once at startup
/// from <c>CODEYBOX_API_KEY</c>. The /healthz endpoint is left unprotected
/// so liveness probes don't need credentials.
///
/// Authentication is REQUIRED unless the operator explicitly sets
/// <c>CodeyBox__DangerouslyDisableAuth=true</c>. Disabling auth is only
/// appropriate on a localhost dev box; the orchestrator can spawn sandboxes,
/// merge to git remotes, and enqueue arbitrary work — anyone who can reach
/// the API can exfiltrate via a malicious prompt.
///
/// <para>The disabled-vs-key check is resolved lazily through DI rather than
/// at <see cref="WebApplicationBuilder"/>-construction time so that test
/// hosts (<c>WebApplicationFactory</c>) can layer their in-memory config in
/// before the check runs. <see cref="ApiKeyAuthValidator"/> still forces
/// fail-fast at host start by resolving the state in <see cref="IHostedService.StartAsync"/>.</para>
/// </summary>
internal static class ApiKeyAuth
{
    public const string EnvVarName = "CODEYBOX_API_KEY";
    public const string DisableConfigKey = "CodeyBox:DangerouslyDisableAuth";

    public static void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var disabled = configuration.GetValue<bool>(DisableConfigKey);

            if (disabled)
                return new ApiKeyState(Token: null, Disabled: true);

            var key = Environment.GetEnvironmentVariable(EnvVarName);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    $"{EnvVarName} must be set, or set {DisableConfigKey}=true to opt out of auth (dev only).");
            if (key.Length < 32)
                throw new InvalidOperationException(
                    $"{EnvVarName} must be at least 32 characters of high-entropy random data.");

            return new ApiKeyState(Token: key, Disabled: false);
        });
        builder.Services.AddHostedService<ApiKeyAuthValidator>();
    }

    public static IApplicationBuilder UseApiKeyAuth(
        this IApplicationBuilder app,
        string[] anonymousPrefixes,
        string[]? anonymousExactPaths = null)
    {
        // Materialise the exact-path list up-front so the middleware loop is
        // a hot allocation-free path. Null is treated as empty.
        var exactPaths = anonymousExactPaths ?? Array.Empty<string>();
        return app.Use(async (ctx, next) =>
        {
            var state = ctx.RequestServices.GetRequiredService<ApiKeyState>();
            if (state.Disabled)
            {
                await next();
                return;
            }

            // Anonymous prefixes (e.g. "/healthz") are exempt — covers the
            // prefix itself AND any descendant routes (StartsWithSegments
            // matches "/healthz", "/healthz/", "/healthz/anything").
            foreach (var prefix in anonymousPrefixes)
            {
                if (ctx.Request.Path.StartsWithSegments(prefix, StringComparison.Ordinal))
                {
                    await next();
                    return;
                }
            }

            // Exact-path exemptions (e.g. "/metrics" for the Prometheus scrape
            // endpoint) — match the path verbatim only. "/metrics" does NOT
            // exempt "/metrics/foo" or "/metricsX" so an inadvertent route
            // collision can't piggy-back on the bypass.
            for (var i = 0; i < exactPaths.Length; i++)
            {
                if (ctx.Request.Path.Equals(exactPaths[i], StringComparison.Ordinal))
                {
                    await next();
                    return;
                }
            }

            if (!IsAuthorized(ctx, state))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
                await ctx.Response.WriteAsync("unauthorized");
                return;
            }

            await next();
        });
    }

    public static bool IsAuthorized(HttpContext ctx, ApiKeyState state)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(state);

        return state.Disabled
            || (TryExtractBearer(ctx, out var presented) && ConstantTimeEquals(presented, state.Token!));
    }

    private static bool TryExtractBearer(HttpContext ctx, out string token)
    {
        token = string.Empty;
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var values)) return false;
        var raw = values.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.Ordinal)) return false;
        token = raw[prefix.Length..].Trim();
        return token.Length > 0;
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}

internal sealed record ApiKeyState(string? Token, bool Disabled);

/// <summary>
/// Forces fail-fast at host start by resolving <see cref="ApiKeyState"/> in
/// <see cref="StartAsync"/>. If the configuration is invalid (no key, no
/// opt-out), the DI factory throws and <c>WebApplication.RunAsync</c>
/// surfaces the failure before any request is served.
/// </summary>
internal sealed class ApiKeyAuthValidator : IHostedService
{
    private readonly IServiceProvider _services;
    public ApiKeyAuthValidator(IServiceProvider services) => _services = services;

    public Task StartAsync(CancellationToken ct)
    {
        // Resolution executes the factory registered in ApiKeyAuth.Configure
        // and propagates any InvalidOperationException to the host.
        _ = _services.GetRequiredService<ApiKeyState>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

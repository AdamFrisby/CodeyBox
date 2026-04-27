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
/// </summary>
internal static class ApiKeyAuth
{
    public const string EnvVarName = "CODEYBOX_API_KEY";
    public const string DisableConfigKey = "CodeyBox:DangerouslyDisableAuth";

    public static void Configure(WebApplicationBuilder builder)
    {
        var disabled = builder.Configuration.GetValue<bool>(DisableConfigKey);
        var key = Environment.GetEnvironmentVariable(EnvVarName);

        if (disabled)
        {
            builder.Services.AddSingleton(new ApiKeyState(Token: null, Disabled: true));
            return;
        }

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"{EnvVarName} must be set, or set {DisableConfigKey}=true to opt out of auth (dev only).");
        if (key.Length < 32)
            throw new InvalidOperationException(
                $"{EnvVarName} must be at least 32 characters of high-entropy random data.");

        builder.Services.AddSingleton(new ApiKeyState(Token: key, Disabled: false));
    }

    public static IApplicationBuilder UseApiKeyAuth(this IApplicationBuilder app, string[] anonymousPrefixes)
    {
        return app.Use(async (ctx, next) =>
        {
            var state = ctx.RequestServices.GetRequiredService<ApiKeyState>();
            if (state.Disabled)
            {
                await next();
                return;
            }

            // Anonymous prefixes (e.g. "/healthz") are exempt.
            foreach (var prefix in anonymousPrefixes)
            {
                if (ctx.Request.Path.StartsWithSegments(prefix, StringComparison.Ordinal))
                {
                    await next();
                    return;
                }
            }

            if (!TryExtractBearer(ctx, out var presented) || !ConstantTimeEquals(presented, state.Token!))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
                await ctx.Response.WriteAsync("unauthorized");
                return;
            }

            await next();
        });
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

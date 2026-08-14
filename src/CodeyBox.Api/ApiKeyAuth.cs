using System.Security.Cryptography;
using System.Text;
using CodeyBox.Core;

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
                return new ApiKeyState(Token: null, Disabled: true, Clients: []);

            var key = Environment.GetEnvironmentVariable(EnvVarName);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException(
                    $"{EnvVarName} must be set, or set {DisableConfigKey}=true to opt out of auth (dev only).");
            if (key.Length < 32)
                throw new InvalidOperationException(
                    $"{EnvVarName} must be at least 32 characters of high-entropy random data.");

            var clients = configuration.GetSection("CodeyBox:ApiClients")
                .Get<List<ApiClientOptions>>() ?? [];
            var resolved = new List<ResolvedApiClient>(clients.Count);
            foreach (var client in clients)
            {
                if (string.IsNullOrWhiteSpace(client.Name)
                    || string.IsNullOrWhiteSpace(client.TokenEnvVar)
                    || client.Principal is null)
                    throw new InvalidOperationException(
                        "Each CodeyBox:ApiClients entry requires Name, TokenEnvVar, and Principal.");
                var token = Environment.GetEnvironmentVariable(client.TokenEnvVar);
                if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
                    throw new InvalidOperationException(
                        $"{client.TokenEnvVar} must contain at least 32 characters of high-entropy random data.");
                ValidateInitiator(client.Principal);
                resolved.Add(new ResolvedApiClient(
                    client.Name, token, client.Principal, client.CanDelegateInitiator));
            }

            return new ApiKeyState(Token: key, Disabled: false, Clients: resolved);
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
                ctx.Items[PrincipalItemKey] = new ApiClientPrincipal(
                    "authentication-disabled", OperatorInitiator, CanDelegateInitiator: false);
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

            if (!TryAuthenticate(ctx, state, out var principal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                ctx.Response.Headers["WWW-Authenticate"] = "Bearer";
                await ctx.Response.WriteAsync("unauthorized");
                return;
            }

            ctx.Items[PrincipalItemKey] = principal;
            await next();
        });
    }

    public static bool IsAuthorized(HttpContext ctx, ApiKeyState state)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(state);

        return state.Disabled || TryAuthenticate(ctx, state, out _);
    }

    internal static InitiatorResolution ResolveInitiator(
        HttpContext context,
        WorkInitiator? delegated)
    {
        if (!context.Items.TryGetValue(PrincipalItemKey, out var value)
            || value is not ApiClientPrincipal principal)
            return new(null, Results.Unauthorized());

        if (delegated is null)
            return new(principal.FixedInitiator, null);
        if (!principal.CanDelegateInitiator)
            return new(null, Results.Json(
                new { error = "this API client may not delegate an initiator" },
                statusCode: StatusCodes.Status403Forbidden));

        try { ValidateInitiator(delegated); }
        catch (ArgumentException ex)
        {
            return new(null, Results.BadRequest(new { error = ex.Message }));
        }
        return new(delegated, null);
    }

    private static bool TryAuthenticate(
        HttpContext context,
        ApiKeyState state,
        out ApiClientPrincipal principal)
    {
        principal = default!;
        if (!TryExtractBearer(context, out var presented))
            return false;
        if (state.Token is not null && ConstantTimeEquals(presented, state.Token))
        {
            principal = new ApiClientPrincipal(
                "legacy-operator", OperatorInitiator, CanDelegateInitiator: false);
            return true;
        }
        foreach (var client in state.Clients)
        {
            if (!ConstantTimeEquals(presented, client.Token))
                continue;
            principal = new ApiClientPrincipal(
                client.Name, client.FixedInitiator, client.CanDelegateInitiator);
            return true;
        }
        return false;
    }

    private static void ValidateInitiator(WorkInitiator initiator)
    {
        ValidateIdentityPart(initiator.Issuer, nameof(initiator.Issuer), 200);
        ValidateIdentityPart(initiator.Subject, nameof(initiator.Subject), 200);
        ValidateIdentityPart(initiator.DisplayName, nameof(initiator.DisplayName), 200);
        if (initiator.ProviderIdentities.Count > 16)
            throw new ArgumentException("initiator.providerIdentities may contain at most 16 entries");
        foreach (var identity in initiator.ProviderIdentities)
        {
            ValidateIdentityPart(identity.Provider, "initiator.provider", 50);
            ValidateIdentityPart(identity.AccountId, "initiator.accountId", 200);
            ValidateIdentityPart(identity.Login, "initiator.login", 200);
        }
    }

    private static void ValidateIdentityPart(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new ArgumentException($"{name} must be between 1 and {maximumLength} characters");
        if (value.Any(char.IsControl))
            throw new ArgumentException($"{name} must not contain control characters");
    }

    internal const string PrincipalItemKey = "CodeyBox.ApiClientPrincipal";
    private static readonly WorkInitiator OperatorInitiator = new()
    {
        Issuer = "codeybox",
        Subject = "operator",
        DisplayName = "CodeyBox operator",
    };

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

internal sealed record ApiKeyState(
    string? Token,
    bool Disabled,
    IReadOnlyList<ResolvedApiClient> Clients);

internal sealed record ResolvedApiClient(
    string Name,
    string Token,
    WorkInitiator FixedInitiator,
    bool CanDelegateInitiator);

internal sealed record ApiClientPrincipal(
    string Name,
    WorkInitiator FixedInitiator,
    bool CanDelegateInitiator);

internal sealed record InitiatorResolution(WorkInitiator? Value, IResult? Error);

public sealed class ApiClientOptions
{
    public string Name { get; set; } = string.Empty;
    public string TokenEnvVar { get; set; } = string.Empty;
    public WorkInitiator? Principal { get; set; }
    public bool CanDelegateInitiator { get; set; }
}

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

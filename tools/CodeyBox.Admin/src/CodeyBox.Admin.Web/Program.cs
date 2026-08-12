using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using CodeyBox.Admin.Web.Services;
using CodeyBox.Admin.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseUrl = builder.Configuration.GetValue<string>("CodeyBoxAdmin:ApiBaseUrl")
    ?? "http://localhost:5050";
var requireAuth = builder.Configuration.GetValue<bool>("CodeyBoxAdmin:RequireAuth", false);
var cloudflareTeamDomain = builder.Configuration["CodeyBoxAdmin:Authentication:CloudflareAccess:TeamDomain"]?.TrimEnd('/');
var cloudflareAudience = builder.Configuration["CodeyBoxAdmin:Authentication:CloudflareAccess:Audience"];
var cloudflareEnabled = !string.IsNullOrWhiteSpace(cloudflareTeamDomain)
    && !string.IsNullOrWhiteSpace(cloudflareAudience);
var googleClientId = builder.Configuration["CodeyBoxAdmin:Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["CodeyBoxAdmin:Authentication:Google:ClientSecret"];
var googleEnabled = !string.IsNullOrWhiteSpace(googleClientId)
    && !string.IsNullOrWhiteSpace(googleClientSecret);
var allowedEmailDomains = builder.Configuration
    .GetSection("CodeyBoxAdmin:Authentication:AllowedEmailDomains")
    .Get<string[]>() ?? [];
if (!builder.Environment.IsDevelopment() && requireAuth)
{
    if (!cloudflareEnabled && !googleEnabled)
        throw new InvalidOperationException(
            "Production authentication requires Cloudflare Access or Google OAuth.");
    if (allowedEmailDomains.Length == 0)
        throw new InvalidOperationException(
            "Production authentication requires at least one allowed email domain.");
}
var dataProtectionKeysPath = builder.Configuration["CodeyBoxAdmin:DataProtectionKeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    builder.Services.AddDataProtection()
        .SetApplicationName("CodeyBox.Admin")
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

// Always register auth so AuthorizeRouteView works regardless of RequireAuth setting.
var authentication = builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = "CodeyBoxAdmin";
        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddPolicyScheme("CodeyBoxAdmin", "CodeyBox admin authentication", options =>
    {
        options.ForwardDefaultSelector = context =>
            cloudflareEnabled && context.Request.Headers.ContainsKey("Cf-Access-Jwt-Assertion")
                ? "CloudflareAccess"
                : CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(opts =>
    {
        opts.LoginPath = "/login";
        opts.AccessDeniedPath = "/login";
        // Strict prevents the auth cookie from being sent on any cross-site request.
        opts.Cookie.SameSite = SameSiteMode.Strict;
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
    });

if (cloudflareEnabled)
{
    authentication.AddJwtBearer("CloudflareAccess", options =>
    {
        options.Authority = $"https://{cloudflareTeamDomain}";
        options.Audience = cloudflareAudience;
        options.RequireHttpsMetadata = true;
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                context.Token = context.Request.Headers["Cf-Access-Jwt-Assertion"].FirstOrDefault();
                return Task.CompletedTask;
            }
        };
    });
}

if (googleEnabled)
{
    authentication.AddGoogle(GoogleDefaults.AuthenticationScheme, options =>
    {
        options.ClientId = googleClientId!;
        options.ClientSecret = googleClientSecret!;
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.SaveTokens = false;
        options.CorrelationCookie.SameSite = SameSiteMode.Lax;
    });
}

if (requireAuth)
{
    // Fallback policy forces authentication on every page that doesn't opt out with [AllowAnonymous].
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(context =>
            {
                // A configured domain list is an origin-side backstop to Cloudflare/Google policy.
                // Local emergency credentials remain usable when explicitly configured.
                if (allowedEmailDomains.Length == 0)
                    return true;
                var email = context.User.FindFirstValue(ClaimTypes.Email)
                    ?? context.User.FindFirstValue("email");
                return email is not null && allowedEmailDomains.Any(domain =>
                    email.EndsWith($"@{domain.TrimStart('@')}", StringComparison.OrdinalIgnoreCase));
            })
            .Build());
}
else
{
    builder.Services.AddAuthorization();
}

// Typed HTTP client for the CodeyBox orchestrator API.
// The API bearer token is read from the CODEYBOX_API_KEY env var — never written to config files.
var orchestratorApiKey = Environment.GetEnvironmentVariable("CODEYBOX_API_KEY");
builder.Services.AddHttpClient<CodeyBoxApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    if (!string.IsNullOrEmpty(orchestratorApiKey))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", orchestratorApiKey);
});
builder.Services.AddScoped<ICodeyBoxApiClient>(sp => sp.GetRequiredService<CodeyBoxApiClient>());

// Live stdout hub settings — used by WorkItemDetail to connect to the
// orchestrator's SignalR hub for streaming agent output. The hub URL is
// derived from the orchestrator base URL; the API key authenticates the
// server-side .NET HubConnection (never sent to the browser).
var hubUrl = new Uri(new Uri(apiBaseUrl), "/hubs/agent-stdout").ToString();
builder.Services.AddSingleton(new OrchestratorHubSettings(hubUrl, orchestratorApiKey));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error");

app.MapStaticAssets();
app.UseAntiforgery();

// Auth middleware must run before Blazor components so the user principal is available.
app.UseAuthentication();
app.UseAuthorization();

// Defensive response headers on every response.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    // Blazor Server requires 'unsafe-inline' for its bootstrap script and wss:/ws: for SignalR.
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self' wss: ws:; frame-ancestors 'none'";
    ctx.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

// Cookie-based login — credentials read exclusively from env vars (CODEYBOX_ADMIN_USERNAME /
// CODEYBOX_ADMIN_PASSWORD), never from config files or code.
// Login.razor includes <AntiforgeryToken />, so antiforgery is enforced without DisableAntiforgery().
if (app.Environment.IsDevelopment())
{
    app.MapPost("/account/login", async (HttpContext ctx) =>
    {
        var form = await ctx.Request.ReadFormAsync();
        var username = form["username"].ToString().Trim();
        var password = form["password"].ToString();
        var returnUrl = form["returnUrl"].ToString();

        var expectedUsername = Environment.GetEnvironmentVariable("CODEYBOX_ADMIN_USERNAME") ?? "admin";
        var expectedPassword = Environment.GetEnvironmentVariable("CODEYBOX_ADMIN_PASSWORD") ?? "";

    // Timing-safe comparison prevents oracle attacks on credential length/prefix.
        var usernameOk = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(expectedUsername));
        var passwordOk = !string.IsNullOrEmpty(expectedPassword) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(expectedPassword));

        if (!usernameOk || !passwordOk)
            return Results.Redirect("/login?error=1");

        var claims = new[] { new Claim(ClaimTypes.Name, username) };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        var redirect = !string.IsNullOrEmpty(returnUrl) &&
            returnUrl.StartsWith('/') &&
            !returnUrl.StartsWith("//") &&
            Uri.TryCreate(returnUrl, UriKind.Relative, out _)
            ? returnUrl
            : "/";
        return Results.Redirect(redirect);
    }).AllowAnonymous();
}

app.MapGet("/account/google-login", (string? returnUrl) =>
{
    if (!googleEnabled)
        return Results.NotFound();
    var redirect = !string.IsNullOrEmpty(returnUrl)
        && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//")
        ? returnUrl
        : "/";
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = redirect },
        [GoogleDefaults.AuthenticationScheme]);
}).AllowAnonymous();

// Logout — clears the session cookie and returns to login page.
app.MapPost("/account/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// No-JS reorder: form POST from ▲/▼ buttons in Index.razor.
// The forms include <AntiforgeryToken /> so the UseAntiforgery() middleware validates the token.
// When RequireAuth=true, RequireAuthorization() is chained to block unauthenticated callers;
// the SameSite=Strict auth cookie also prevents cross-site request forgery.
var moveLogger = app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("CodeyBox.Admin.Move");
var moveEndpoint = app.MapPost("/admin/move/{id}/{direction}",
    async (string id, string direction, CodeyBoxApiClient apiClient) =>
    {
        if (direction is not "up" and not "down")
            return Results.BadRequest(new { error = "direction must be 'up' or 'down'" });

        try
        {
            var items = await apiClient.GetWorkItemsAsync();
            var queued = items.Where(i => i.IsQueued).OrderBy(i => i.QueuePosition).ToList();
            var idx = queued.FindIndex(i => i.Id == id);
            if (direction == "up" && idx > 0)
                (queued[idx - 1], queued[idx]) = (queued[idx], queued[idx - 1]);
            else if (direction == "down" && idx >= 0 && idx < queued.Count - 1)
                (queued[idx], queued[idx + 1]) = (queued[idx + 1], queued[idx]);
            if (idx >= 0)
                await apiClient.ReorderWorkItemsAsync(queued.Select(i => i.Id).ToList());
        }
        catch (Exception ex)
        {
            moveLogger.LogWarning(ex, "No-JS reorder for item {ItemId} direction={Direction} failed", id, direction);
        }
        return Results.Redirect("/");
    });

if (requireAuth)
    moveEndpoint.RequireAuthorization();

app.MapRazorComponents<CodeyBox.Admin.Web.Components.App>()
   .AddInteractiveServerRenderMode();

app.Run();

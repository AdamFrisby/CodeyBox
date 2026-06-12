using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using CodeyBox.Admin.Web.Services;
using CodeyBox.Admin.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseUrl = builder.Configuration.GetValue<string>("CodeyBoxAdmin:ApiBaseUrl")
    ?? "http://localhost:5050";
var requireAuth = builder.Configuration.GetValue<bool>("CodeyBoxAdmin:RequireAuth", false);

// Always register auth so AuthorizeRouteView works regardless of RequireAuth setting.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opts =>
    {
        opts.LoginPath = "/login";
        opts.AccessDeniedPath = "/login";
        // Strict prevents the auth cookie from being sent on any cross-site request.
        opts.Cookie.SameSite = SameSiteMode.Strict;
        opts.Cookie.HttpOnly = true;
        opts.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

if (requireAuth)
{
    // Fallback policy forces authentication on every page that doesn't opt out with [AllowAnonymous].
    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
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

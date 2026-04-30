using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using CodeyBox.Admin.Web.Services;

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
builder.Services.AddHttpClient<CodeyBoxApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseUrl);
    var apiKey = Environment.GetEnvironmentVariable("CODEYBOX_API_KEY");
    if (!string.IsNullOrEmpty(apiKey))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
});
builder.Services.AddScoped<ICodeyBoxApiClient>(sp => sp.GetRequiredService<CodeyBoxApiClient>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/error");

app.UseStaticFiles();
app.UseAntiforgery();

// Auth middleware must run before Blazor components so the user principal is available.
app.UseAuthentication();
app.UseAuthorization();

// No-JS reorder: form POST from ▲/▼ buttons in Index.razor.
// DisableAntiforgery because the form submits via a full HTTP round-trip; the internal admin
// network trust model (same as the rest of the dashboard) is the CSRF boundary here.
app.MapPost("/admin/move/{id}/{direction}", async (string id, string direction, CodeyBoxApiClient apiClient) =>
{
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
    catch { /* best-effort; redirect either way */ }
    return Results.Redirect("/");
}).DisableAntiforgery();

app.MapRazorComponents<CodeyBox.Admin.Web.Components.App>()
   .AddInteractiveServerRenderMode();

app.Run();

using Microsoft.AspNetCore.Authentication.Cookies;
using CodeyBox.Admin.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseUrl = builder.Configuration.GetValue<string>("CodeyBoxAdmin:ApiBaseUrl")
    ?? "http://localhost:5050";
var requireAuth = builder.Configuration.GetValue<bool>("CodeyBoxAdmin:RequireAuth", false);

if (requireAuth)
{
    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(opts =>
        {
            opts.LoginPath = "/login";
            opts.AccessDeniedPath = "/login";
        });
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

if (requireAuth)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.MapRazorComponents<CodeyBox.Admin.Web.Components.App>()
   .AddInteractiveServerRenderMode();

app.Run();

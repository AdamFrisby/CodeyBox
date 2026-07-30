using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using CodeyBox.Upstream.GitHub;

namespace CodeyBox.Api;

internal static class GitHubAppConnectEndpoints
{
    private static readonly JsonSerializerOptions GitHubJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/github-app/status", (GitHubAppStore store) =>
            Results.Ok(store.List().Select(item => new
            {
                item.Slug, item.Account, installed = item.InstallationId > 0,
            })));
        app.MapPost("/github-app/connect", (
            HttpContext context,
            IConfiguration configuration,
            GitHubAppConnectState connections) =>
        {
            var baseUrl = ResolveBaseUrl(context, configuration);
            if (!connections.TryBegin(baseUrl, out var state))
                return Results.Problem("Too many GitHub App connections are pending.", statusCode: 429);
            return Results.Ok(new { url = $"{baseUrl}/github-app/start?state={state}" });
        });
        app.MapGet("/github-app/start", (string? state, GitHubAppConnectState connections) =>
        {
            if (!connections.TryGet(state, consume: false, out var pending))
                return Results.NotFound();
            var manifest = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["name"] = $"CodeyBox ({Environment.MachineName})",
                ["url"] = pending.BaseUrl,
                ["redirect_url"] = $"{pending.BaseUrl}/github-app/callback",
                ["setup_url"] = $"{pending.BaseUrl}/github-app/callback?state={state}",
                ["public"] = false,
                ["default_permissions"] = new Dictionary<string, string>
                {
                    ["contents"] = "write",
                    ["metadata"] = "read",
                    ["pull_requests"] = "write",
                },
                ["default_events"] = Array.Empty<string>(),
            });
            var html = $$"""
                <!doctype html><html><body><p>Redirecting to GitHub…</p>
                <form id="f" action="https://github.com/settings/apps/new?state={{state}}" method="post">
                <input type="hidden" name="manifest" value="{{WebUtility.HtmlEncode(manifest)}}">
                <noscript><button type="submit">Continue</button></noscript></form>
                <script>document.getElementById('f').submit()</script></body></html>
                """;
            return Results.Content(html, "text/html");
        });
        app.MapGet("/github-app/callback", HandleCallbackAsync);
    }

    private static async Task<IResult> HandleCallbackAsync(
        string? code,
        string? state,
        string? installation_id,
        GitHubAppStore store,
        GitHubAppConnectState connections,
        IHttpClientFactory clients,
        CancellationToken cancellationToken)
    {
        if (long.TryParse(
                installation_id,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var installationId)
            && installationId > 0)
        {
            if (!connections.TryGet(state, consume: true, out var installation)
                || installation.AppId is null)
                return Page("The GitHub App installation link is invalid or expired.");
            var pendingInstall = store.Get(installation.AppId.Value);
            if (pendingInstall is null)
                return Page("No pending App installation was found.");
            var installed = store.CompleteInstall(pendingInstall.AppId, installationId);
            return Page($"GitHub App {WebUtility.HtmlEncode(installed.Slug)} is connected. Close this tab.");
        }
        if (!connections.TryGet(state, consume: false, out var pending)
            || string.IsNullOrWhiteSpace(code))
            return Page("The GitHub App setup link is invalid or expired.");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/app-manifests/{Uri.EscapeDataString(code)}/conversions");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.UserAgent.ParseAdd("CodeyBox");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await clients.CreateClient("github-upstream")
            .SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return Page($"GitHub rejected the App manifest conversion ({(int)response.StatusCode}).");
        var conversion = await response.Content.ReadFromJsonAsync<AppManifestConversion>(
            GitHubJson, cancellationToken);
        if (conversion is not { Id: > 0, Slug.Length: > 0, Pem.Length: > 0 })
            return Page("GitHub returned incomplete App details.");
        store.SaveCreated(
            conversion.Id, conversion.Slug, conversion.Owner?.Login ?? string.Empty, conversion.Pem);
        connections.BindApp(state!, pending, conversion.Id);
        var installUrl = $"https://github.com/apps/{conversion.Slug}/installations/new";
        return Results.Content($$"""
            <!doctype html><html><body><p>App created. Redirecting to installation…</p>
            <script>location.href={{JsonSerializer.Serialize(installUrl)}}</script>
            <a href="{{installUrl}}">Continue</a></body></html>
            """, "text/html");
    }

    private static string ResolveBaseUrl(HttpContext context, IConfiguration configuration)
    {
        var configured = configuration["CodeyBox:PublicBaseUrl"]
            ?? Environment.GetEnvironmentVariable("CODEYBOX_PUBLIC_BASE_URL");
        var candidate = string.IsNullOrWhiteSpace(configured)
            ? $"{context.Request.Scheme}://{context.Request.Host}"
            : configured;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                "CodeyBox:PublicBaseUrl must be an HTTPS origin (HTTP is allowed only for loopback).");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static IResult Page(string message) =>
        Results.Content($"<!doctype html><html><body><p>{WebUtility.HtmlEncode(message)}</p></body></html>", "text/html");

    private sealed record AppManifestConversion(long Id, string Slug, string Pem, AppOwner? Owner);
    private sealed record AppOwner(string? Login);
}

internal sealed record PendingGitHubAppConnect(
    string BaseUrl,
    DateTimeOffset ExpiresAt,
    long? AppId);

internal sealed class GitHubAppConnectState(TimeProvider timeProvider)
{
    private const int MaxPendingConnections = 64;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, PendingGitHubAppConnect> _pending = new();

    public bool TryBegin(string baseUrl, out string state)
    {
        RemoveExpired();
        state = string.Empty;
        if (_pending.Count >= MaxPendingConnections) return false;
        state = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        return _pending.TryAdd(
            state,
            new PendingGitHubAppConnect(baseUrl, timeProvider.GetUtcNow().Add(Lifetime), AppId: null));
    }

    public bool TryGet(
        string? state,
        bool consume,
        out PendingGitHubAppConnect pending)
    {
        pending = default!;
        if (state is null) return false;
        var found = consume
            ? _pending.TryRemove(state, out pending!)
            : _pending.TryGetValue(state, out pending!);
        if (!found || pending.ExpiresAt >= timeProvider.GetUtcNow()) return found;
        _pending.TryRemove(state, out _);
        return false;
    }

    public void BindApp(string state, PendingGitHubAppConnect pending, long appId)
    {
        if (!_pending.TryUpdate(
                state,
                pending with { AppId = appId, ExpiresAt = timeProvider.GetUtcNow().Add(Lifetime) },
                pending))
            throw new InvalidOperationException("The GitHub App connection expired during setup.");
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in _pending)
            if (item.Value.ExpiresAt < now)
                _pending.TryRemove(item.Key, out _);
    }
}

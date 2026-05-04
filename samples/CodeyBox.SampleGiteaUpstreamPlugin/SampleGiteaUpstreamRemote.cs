// SAMPLE PLUGIN — Gitea upstream remote for CodeyBox.
//
// How it works:
// 1. The factory matches Upstream.Kind = "gitea" and returns this singleton.
// 2. CompleteAsync reads per-project config from IPluginHost.GetProjectUpstreamConfig.
// 3. It pushes the work branch via the host git module, opens a pull request
//    using Gitea's REST API, and optionally auto-merges it.
//
// Per-project config keys (in Upstream.PluginConfig):
//   BaseUrl    — Gitea API base, e.g. "https://gitea.mycompany.com/api/v1"
//   Owner      — repository owner (user or org)
//   Repository — repository name
//
// See docs/upstream-plugins.md for full authoring guidance.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SampleGiteaUpstreamPlugin;

/// <summary>
/// Upstream remote plugin for Gitea. Pushes a work branch and opens a pull
/// request via Gitea's <c>/api/v1/repos/{owner}/{repo}/pulls</c> endpoint.
/// Per-project settings (BaseUrl, Owner, Repository) are read from
/// <c>Upstream.PluginConfig</c> via <c>IPluginHost.GetProjectUpstreamConfig</c>.
/// </summary>
[CodeyBoxPlugin(
    id: "sample.gitea-upstream",
    displayName: "Gitea Upstream Remote",
    minHostApiVersion: "1.0")]
public sealed class SampleGiteaUpstreamRemote : IUpstreamRemote, IPluginInitializer
{
    public string Name => "gitea";

    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;

    private IPluginHost _host = null!;

    public SampleGiteaUpstreamRemote(IGitHost gitHost, IHttpClientFactory httpClientFactory)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _host = context.Host;
        context.Logger.LogInformation("SampleGiteaUpstreamRemote initialized");
        return Task.CompletedTask;
    }

    public Task<UpstreamPushResult> PushAsync(
        string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(true, null));

    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        var cfg = _host.GetProjectUpstreamConfig(request.ProjectId);

        if (!cfg.TryGetValue("BaseUrl", out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                $"Project {request.ProjectId}: Gitea plugin requires Upstream.PluginConfig.BaseUrl");
        if (!cfg.TryGetValue("Owner", out var owner) || string.IsNullOrWhiteSpace(owner))
            throw new InvalidOperationException(
                $"Project {request.ProjectId}: Gitea plugin requires Upstream.PluginConfig.Owner");
        if (!cfg.TryGetValue("Repository", out var repo) || string.IsNullOrWhiteSpace(repo))
            throw new InvalidOperationException(
                $"Project {request.ProjectId}: Gitea plugin requires Upstream.PluginConfig.Repository");

        // Push the work branch to Gitea via the host git module.
        var upstreamUrl = $"{baseUrl.TrimEnd('/')}/repos/{owner}/{repo}";
        await _gitHost.PushToUpstreamAsync(
            request.RepositoryId,
            upstreamUrl,
            request.WorkBranch,
            BuildAuthEnv(request),
            ct);

        // Open a pull request.
        var prNumber = await OpenPullRequestAsync(baseUrl, owner, repo, request, ct);
        var prUrl = $"{NormalizeBaseToWebUrl(baseUrl)}/{owner}/{repo}/pulls/{prNumber}";

        _host.Logger.LogInformation(
            "Gitea PR #{Number} opened: {Url}", prNumber, prUrl);

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = prUrl,
            PullRequestNumber = prNumber,
        };
    }

    private async Task<int> OpenPullRequestAsync(
        string baseUrl, string owner, string repo,
        UpstreamCompletionRequest request, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("gitea-upstream");
        var url = $"{baseUrl.TrimEnd('/')}/repos/{owner}/{repo}/pulls";

        var body = new
        {
            title = request.Title,
            body = request.Description ?? string.Empty,
            head = request.WorkBranch,
            @base = request.BaseBranch,
        };

        var response = await client.PostAsJsonAsync(url, body, ct);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // PR already exists — return a sentinel so the orchestrator doesn't retry.
            _host.Logger.LogWarning(
                "Gitea: PR already exists for branch '{Branch}' (422); treating as soft-failure",
                request.WorkBranch);
            return 0;
        }

        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return doc.RootElement.GetProperty("number").GetInt32();
    }

    private static IReadOnlyDictionary<string, string> BuildAuthEnv(
        UpstreamCompletionRequest request)
    {
        // Plugins must read tokens from env vars, never from config files.
        // Operators set the token env var name in Upstream.TokenEnvVar.
        return new Dictionary<string, string>();
    }

    private static string NormalizeBaseToWebUrl(string baseUrl)
    {
        // Strip the /api/v1 suffix to get the human-readable web URL.
        var idx = baseUrl.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? baseUrl[..idx] : baseUrl;
    }
}

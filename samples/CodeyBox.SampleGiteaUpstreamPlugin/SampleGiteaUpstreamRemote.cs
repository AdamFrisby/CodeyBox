// SAMPLE PLUGIN — Gitea upstream remote for CodeyBox.
//
// How it works:
// 1. The factory matches Upstream.Kind = "gitea" and returns this singleton.
// 2. CompleteAsync reads per-project config from IUpstreamPluginHost.GetProjectUpstreamConfig.
// 3. It pushes the work branch via the host git module, opens a pull request
//    using Gitea's REST API, and optionally auto-merges it.
//
// Per-project config keys (in Upstream.PluginConfig):
//   BaseUrl    — Gitea API base (https required), e.g. "https://gitea.mycompany.com/api/v1"
//   Owner      — repository owner (user or org)
//   Repository — repository name
//
// Authentication:
//   Set Upstream.TokenEnvVar to the env var name holding the Gitea token.
//   The orchestrator forwards it via UpstreamCompletionRequest.TokenEnvVar;
//   this plugin reads the token with Environment.GetEnvironmentVariable(request.TokenEnvVar).
//
// See docs/upstream-plugins.md for full authoring guidance.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.SampleGiteaUpstreamPlugin;

/// <summary>
/// Upstream remote plugin for Gitea. Pushes a work branch, opens a pull
/// request via Gitea's <c>/api/v1/repos/{owner}/{repo}/pulls</c> endpoint,
/// and optionally auto-merges it.
/// Per-project settings (BaseUrl, Owner, Repository) are read from
/// <c>Upstream.PluginConfig</c> via <c>IUpstreamPluginHost.GetProjectUpstreamConfig</c>.
/// The auth token is read from the environment variable named in
/// <c>UpstreamCompletionRequest.TokenEnvVar</c>.
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
    private IUpstreamPluginHost _upstreamHost = null!;

    public SampleGiteaUpstreamRemote(IGitHost gitHost, IHttpClientFactory httpClientFactory)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _host = context.Host;
        _upstreamHost = (IUpstreamPluginHost)_host;
        context.Logger.LogInformation("SampleGiteaUpstreamRemote initialized");
        return Task.CompletedTask;
    }

    // PushAsync is called for push-only orchestrator flows. Per-project config
    // (BaseUrl, Owner, Repository) is not available without a project ID, so
    // standalone push is not supported by this sample. Production plugins that
    // need standalone push support should cache config at InitializeAsync time.
    public Task<UpstreamPushResult> PushAsync(
        string repositoryId, string branch, CancellationToken ct = default)
        => Task.FromResult(new UpstreamPushResult(
            false, "push-only not supported by this plugin; use CompleteAsync"));

    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        var cfg = _upstreamHost.GetProjectUpstreamConfig(request.ProjectId);

        if (!cfg.TryGetValue("BaseUrl", out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException(
                $"Project {request.ProjectId}: Gitea plugin requires Upstream.PluginConfig.BaseUrl");
        if (!cfg.TryGetValue("Owner", out var owner) || string.IsNullOrWhiteSpace(owner))
            throw new InvalidOperationException(
                $"Project {request.ProjectId}: Gitea plugin requires Upstream.PluginConfig.Owner");
        if (!cfg.TryGetValue("Repository", out var repo) || string.IsNullOrWhiteSpace(repo))
            throw new InvalidOperationException(
                $"Project {request.ProjectId}: Gitea plugin requires Upstream.PluginConfig.Repository");

        ValidateBaseUrl(baseUrl, request.ProjectId);

        // Resolve token from the env var named by the orchestrator — never from PluginConfig.
        var token = ResolveToken(request);

        // Percent-encode owner/repo so values like "team/admin" don't inject extra path segments.
        var encodedOwner = Uri.EscapeDataString(owner);
        var encodedRepo = Uri.EscapeDataString(repo);

        // Push the work branch to Gitea via the host git module.
        var upstreamUrl = $"{baseUrl.TrimEnd('/')}/repos/{encodedOwner}/{encodedRepo}";
        await _gitHost.PushToUpstreamAsync(
            request.RepositoryId,
            upstreamUrl,
            request.WorkBranch,
            BuildAuthEnv(token),
            ct);

        // Open a pull request.
        var prNumber = await OpenPullRequestAsync(
            baseUrl, encodedOwner, encodedRepo, token, request, ct);

        // prNumber == 0 means the PR already existed (Gitea 422 soft-failure).
        string? prUrl = prNumber > 0
            ? $"{NormalizeBaseToWebUrl(baseUrl)}/{owner}/{repo}/pulls/{prNumber}"
            : null;

        if (prNumber > 0)
            _host.Logger.LogInformation(
                "Gitea PR #{Number} opened: {Url}", prNumber, prUrl);

        if (!request.AutoMerge || prNumber == 0)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = prUrl,
                PullRequestNumber = prNumber > 0 ? prNumber : null,
            };
        }

        // Auto-merge via Gitea merge endpoint.
        var (mergedSha, mergeNotes) = await MergePullRequestAsync(
            baseUrl, encodedOwner, encodedRepo, prNumber, request.MergeMethod, token, ct);

        if (mergedSha is not null)
            _host.Logger.LogInformation(
                "Gitea PR #{Number} auto-merged: {Sha}", prNumber, mergedSha);

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = prUrl,
            PullRequestNumber = prNumber,
            MergedSha = mergedSha,
            Notes = mergeNotes,
        };
    }

    private static readonly HashSet<string> _ssrfBlockedHosts =
        new(StringComparer.OrdinalIgnoreCase) { "localhost", "metadata.google.internal" };

    // Validates scheme, syntax, and blocks loopback/private/link-local addresses
    // so operators cannot use PluginConfig to point the orchestrator at internal
    // network addresses via BaseUrl.
    private static void ValidateBaseUrl(string baseUrl, ProjectId projectId)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Project {projectId}: Gitea plugin BaseUrl '{baseUrl}' is not a valid URI");
        if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Project {projectId}: Gitea plugin BaseUrl must use https:// (got '{uri.Scheme}://')");

        // Block known loopback/internal hostnames.
        if (_ssrfBlockedHosts.Contains(uri.Host))
            throw new InvalidOperationException(
                $"Project {projectId}: Gitea plugin BaseUrl must not point to a loopback or internal address");

        // Block IP addresses in loopback, private RFC-1918, or link-local ranges.
        if (IPAddress.TryParse(uri.Host, out var ip) && IsNonRoutableAddress(ip))
            throw new InvalidOperationException(
                $"Project {projectId}: Gitea plugin BaseUrl must not point to a private or loopback address");
    }

    private static bool IsNonRoutableAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 (link-local / cloud metadata)
            return bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // fc00::/7 (ULA — covers fd00:ec2::/32 and similar), fe80::/10 (link-local)
            return (bytes[0] & 0xfe) == 0xfc
                || (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80);
        }

        return false;
    }

    private static string? ResolveToken(UpstreamCompletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TokenEnvVar))
            return null;
        return Environment.GetEnvironmentVariable(request.TokenEnvVar);
    }

    private static IReadOnlyDictionary<string, string> BuildAuthEnv(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new Dictionary<string, string>();
        // Supply token as HTTP password for HTTPS git pushes.
        return new Dictionary<string, string>
        {
            ["GIT_USERNAME"] = "git",
            ["GIT_PASSWORD"] = token,
        };
    }

    private async Task<int> OpenPullRequestAsync(
        string baseUrl, string encodedOwner, string encodedRepo,
        string? token, UpstreamCompletionRequest request, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/repos/{encodedOwner}/{encodedRepo}/pulls";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("token", token);

        req.Content = JsonContent.Create(new
        {
            title = request.Title,
            body = request.Description ?? string.Empty,
            head = request.WorkBranch,
            @base = request.BaseBranch,
        });

        var client = _httpClientFactory.CreateClient("gitea-upstream");
        using var response = await client.SendAsync(req, ct);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
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

    private async Task<(string? Sha, string? Notes)> MergePullRequestAsync(
        string baseUrl, string encodedOwner, string encodedRepo,
        int prNumber, string mergeMethod, string? token, CancellationToken ct)
    {
        var url = $"{baseUrl.TrimEnd('/')}/repos/{encodedOwner}/{encodedRepo}/pulls/{prNumber}/merge";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(token))
            req.Headers.Authorization = new AuthenticationHeaderValue("token", token);

        // Gitea merge action values: "merge", "squash", "rebase".
        req.Content = JsonContent.Create(new { Do = mergeMethod });

        var client = _httpClientFactory.CreateClient("gitea-upstream");
        using var response = await client.SendAsync(req, ct);

        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.Conflict)
        {
            _host.Logger.LogWarning(
                "Gitea POST /pulls/{N}/merge returned {Status}; leaving PR open",
                prNumber, (int)response.StatusCode);
            return (null, "Auto-merge blocked (PR not mergeable or branch protection); PR left open");
        }

        response.EnsureSuccessStatusCode();

        // Attempt to parse merge SHA from the response body; absent on some Gitea versions.
        string? sha = null;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrEmpty(body))
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("sha", out var shaProp))
                    sha = shaProp.GetString();
            }
        }
        catch (JsonException)
        {
            // Body absent or not JSON — sha stays null, which is acceptable.
        }

        return (sha, null);
    }

    private static string NormalizeBaseToWebUrl(string baseUrl)
    {
        // Strip the /api/v1 suffix to get the human-readable web URL.
        var idx = baseUrl.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? baseUrl[..idx] : baseUrl;
    }
}

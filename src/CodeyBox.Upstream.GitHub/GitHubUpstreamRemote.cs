using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using CodeyBox.Core;
using CodeyBox.Git;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Upstream.GitHub;

/// <summary>
/// GitHub upstream remote. Phase 4 pushes the work branch to GitHub, opens a
/// pull request, and optionally auto-merges it — leaving an audit trail on the
/// forge rather than a silent base-branch update.
///
/// PAT security model (unchanged from the old push-only path):
///   - URL is bare https://github.com/owner/repo.git (no embedded token).
///   - GIT_ASKPASS points to a per-call script that reads the token from env.
///   - Token is set only as env var, never on argv or in config files.
///   - Token is scrubbed from any error message before it leaves this class.
///   - HTTP requests carry Authorization: token <PAT> as a request header;
///     the header is added per-request so the shared HttpClient is not mutated.
/// </summary>
public sealed class GitHubUpstreamRemote : IUpstreamRemote
{
    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWebhookDispatcher _webhooks;
    private readonly ILogger<GitHubUpstreamRemote> _log;
    private readonly GitHubUpstreamOptions _opts;

    public GitHubUpstreamRemote(
        IGitHost gitHost,
        IHttpClientFactory httpClientFactory,
        IWebhookDispatcher webhooks,
        ILogger<GitHubUpstreamRemote> log,
        GitHubUpstreamOptions opts)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
        _webhooks = webhooks;
        _log = log;
        _opts = opts;
        if (string.IsNullOrEmpty(_opts.Token))
            throw new ArgumentException("GitHub PAT must be provided", nameof(opts));
    }

    public string Name => "github";

    /// <summary>
    /// Legacy push path — kept for interface completeness. CompleteAsync is
    /// the primary path called by the orchestrator.
    /// </summary>
    public async Task<UpstreamPushResult> PushAsync(string repositoryId, string branch, CancellationToken ct = default)
    {
        var url = RepoUrl();
        using var askpass = GitCredentialHelper.CreateAskPassFor(_opts.Token, "x-access-token");
        try
        {
            await _gitHost.PushToUpstreamAsync(repositoryId, url, branch, askpass.Environment, ct);
            return new UpstreamPushResult(true, null);
        }
        catch (Exception ex)
        {
            var scrubbed = Scrub(ex.Message);
            return new UpstreamPushResult(false, scrubbed);
        }
    }

    /// <summary>
    /// Full GitHub completion flow:
    ///   1. Push work branch to GitHub.
    ///   2. Open a PR (workBranch → baseBranch).
    ///   3. If AutoMerge=true, merge the PR via the GitHub API.
    ///
    /// Transient failures (network, unexpected HTTP errors) throw so the
    /// orchestrator can retry. Soft errors (422 PR already exists, 405 PR not
    /// mergeable) are logged and return a partial outcome without throwing.
    /// </summary>
    public async Task<UpstreamCompletionOutcome> CompleteAsync(UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        // Step 1: push work branch
        var repoUrl = RepoUrl();
        using var askpass = GitCredentialHelper.CreateAskPassFor(_opts.Token, "x-access-token");
        try
        {
            await _gitHost.PushToUpstreamAsync(request.RepositoryId, repoUrl, request.WorkBranch, askpass.Environment, ct);
        }
        catch (Exception ex)
        {
            var scrubbed = Scrub(ex.Message);
            throw new InvalidOperationException($"Failed to push work branch '{request.WorkBranch}': {scrubbed}", ex);
        }

        // Step 2: open PR
        var prTitle = BuildPrTitle(request.Title, request.WorkBranch);
        var pr = await CreatePullRequestAsync(request, prTitle, ct);
        if (pr is null)
        {
            // 422 — branch already has an open PR or the request was otherwise
            // unprocessable; leave it open for a human to sort out.
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                Notes = "PR creation skipped (422 — branch may already have an open PR)",
            };
        }

        _log.LogInformation("GitHub PR opened: {Url}", pr.HtmlUrl);

        await _webhooks.DispatchAsync("work_item.pull_request_opened", new PullRequestOpenedPayload
        {
            WorkBranch = request.WorkBranch,
            BaseBranch = request.BaseBranch,
            PullRequestNumber = pr.Number,
            PullRequestUrl = pr.HtmlUrl ?? string.Empty,
        }, ct);

        if (!_opts.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = pr.HtmlUrl,
                PullRequestNumber = pr.Number,
            };
        }

        // Step 3: auto-merge
        var mergedSha = await MergePullRequestAsync(pr.Number, ct);
        if (mergedSha is not null)
            _log.LogInformation("GitHub PR #{N} auto-merged: {Sha}", pr.Number, mergedSha);

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = pr.HtmlUrl,
            PullRequestNumber = pr.Number,
            MergedSha = mergedSha,
        };
    }

    // -------------------------------------------------------------------------

    private async Task<GitHubPrResponse?> CreatePullRequestAsync(
        UpstreamCompletionRequest request, string prTitle, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls";
        var body = new GitHubCreatePrRequest(prTitle, request.Description ?? string.Empty, request.WorkBranch, request.BaseBranch);

        using var req = BuildRequest(HttpMethod.Post, url);
        req.Content = JsonContent.Create(body);

        using var response = await SendAsync(req, ct);

        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            _log.LogWarning(
                "GitHub POST /pulls returned 422 for {Owner}/{Repo} head={WorkBranch} base={BaseBranch}; skipping PR creation",
                _opts.Owner, _opts.Repository, request.WorkBranch, request.BaseBranch);
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GitHubPrResponse>(ct);
    }

    private async Task<string?> MergePullRequestAsync(int prNumber, CancellationToken ct)
    {
        var url = $"https://api.github.com/repos/{_opts.Owner}/{_opts.Repository}/pulls/{prNumber}/merge";
        var body = new GitHubMergeRequest(_opts.MergeMethod);

        using var req = BuildRequest(HttpMethod.Put, url);
        req.Content = JsonContent.Create(body);

        using var response = await SendAsync(req, ct);

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            _log.LogWarning(
                "GitHub PUT /pulls/{N}/merge returned 405 (PR not mergeable, e.g. branch protection); leaving PR open",
                prNumber);
            return null;
        }

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GitHubMergeResponse>(ct);
        return result?.Sha;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("token", _opts.Token);
        return req;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        => _httpClientFactory.CreateClient("github-upstream").SendAsync(req, ct);

    private string RepoUrl() => $"https://github.com/{_opts.Owner}/{_opts.Repository}.git";

    private string Scrub(string message) =>
        message.Replace(_opts.Token, "***", StringComparison.Ordinal);

    private string BuildPrTitle(string title, string workBranch)
    {
        if (string.IsNullOrEmpty(_opts.PullRequestTitleTemplate))
            return title;
        return _opts.PullRequestTitleTemplate
            .Replace("{title}", title, StringComparison.Ordinal)
            .Replace("{branch}", workBranch, StringComparison.Ordinal);
    }
}

public sealed record GitHubUpstreamOptions
{
    public required string Owner { get; init; }
    public required string Repository { get; init; }
    /// <summary>GitHub PAT or fine-grained token. Never logged, never on argv.</summary>
    public required string Token { get; init; }
    public string MergeMethod { get; init; } = "merge";
    public bool AutoMerge { get; init; }
    public string? PullRequestTitleTemplate { get; init; }
}

// Internal DTOs — only used for GitHub REST serialisation, never exposed.

internal sealed record GitHubCreatePrRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("base")] string Base);

internal sealed record GitHubMergeRequest(
    [property: JsonPropertyName("merge_method")] string MergeMethod);

internal sealed record GitHubPrResponse(
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

internal sealed record GitHubMergeResponse(
    [property: JsonPropertyName("sha")] string? Sha);

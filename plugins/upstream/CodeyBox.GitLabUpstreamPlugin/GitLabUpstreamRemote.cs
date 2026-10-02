using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GitLabUpstreamPlugin;

/// <summary>
/// Upstream remote for GitLab (gitlab.com and self-hosted) implementing the
/// full <see cref="IUpstreamRemote"/> contract against GitLab REST API v4.
/// One project, one plugin. Off unless an operator enables it: the assembly
/// must be allowlisted and a project must set <c>Upstream.Kind = "gitlab"</c>.
///
/// <para>Core lifecycle (in this file): pushes the work branch through the
/// host git module, opens a merge request with
/// <c>POST /projects/:id/merge_requests</c>, optionally auto-merges it, merges
/// upstream branches for release sync host-side (GitLab exposes no
/// branch-to-branch merge API), fetches base branches, and lists/reads open
/// merge requests. Extended surfaces (reviews, checks, comments, webhooks,
/// metadata) live in <c>GitLabUpstreamRemote.Extended.cs</c>.</para>
///
/// <para>Configuration: per-project <c>Upstream.PluginConfig</c> keys
/// <c>BaseUrl</c>, <c>Project</c> (optional <c>PerPage</c>,
/// <c>MaxListPages</c>), read via
/// <c>IUpstreamPluginHost.GetProjectUpstreamConfig</c>. Calls without a
/// project context (sweeps, reads, merges) fall back to the plugin-scoped
/// <c>CodeyBox:Plugins:codeybox.gitlab-upstream</c> section. Credentials
/// never come from configuration: the token is read from the environment
/// variable named by <c>Upstream.TokenEnvVar</c> (forwarded on the completion
/// request) and is held only by this remote — sandboxes never see it.</para>
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.gitlab-upstream",
    displayName: "GitLab Upstream Remote",
    minHostApiVersion: "1.0")]
public sealed partial class GitLabUpstreamRemote : IUpstreamRemote, IPluginInitializer
{
    public const string HttpClientName = "gitlab-upstream";

    private const int DefaultPerPage = 50;
    private const int DefaultMaxListPages = 10;
    private const int MaxTitleLength = 512;
    private const int MaxBodyLength = 100_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;

    private IPluginHost _host = null!;
    private IUpstreamPluginHost _upstreamHost = null!;

    public GitLabUpstreamRemote(IGitHost gitHost, IHttpClientFactory httpClientFactory)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "gitlab";

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _host = context.Host;
        _upstreamHost = _host as IUpstreamPluginHost
            ?? throw new InvalidOperationException(
                "GitLab upstream remote requires a host exposing IUpstreamPluginHost.");
        context.Logger.LogInformation("GitLabUpstreamRemote initialized");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pushes <paramref name="branch"/> to the scoped GitLab project via the
    /// host git module. Returns a failure result (rather than throwing) on
    /// transport errors, matching the <c>git-generic</c> push contract.
    /// </summary>
    public async Task<UpstreamPushResult> PushAsync(
        string repositoryId, string branch, CancellationToken ct = default)
    {
        GitLabEndpointConfig endpoint;
        try
        {
            endpoint = ResolveScopedConfig()
                ?? throw new InvalidOperationException(
                    "GitLab upstream push requires plugin-scoped BaseUrl/Project " +
                    "under CodeyBox:Plugins:codeybox.gitlab-upstream.");
            Validation.ValidateBranchName(branch, nameof(branch));
        }
        catch (Exception ex)
        {
            return new UpstreamPushResult(false, ex.Message);
        }

        var token = ResolveToken(null);
        using var auth = GitLabGitAuthScope.Create(token);
        try
        {
            var gitUrl = await GetGitUrlAsync(endpoint, token, ct);
            await _gitHost.PushToUpstreamAsync(
                repositoryId,
                gitUrl,
                branch,
                auth.Environment,
                UpstreamPushReconcileStrategy.Rebase,
                ct);
            return new UpstreamPushResult(true, null);
        }
        catch (Exception ex)
        {
            return new UpstreamPushResult(false, Scrub(ex.Message, token));
        }
    }

    /// <summary>
    /// Full GitLab completion flow: push work branch, open a merge request
    /// (or reuse <see cref="UpstreamCompletionRequest.ExistingPullRequestNumber"/>
    /// from a prior race-recovery attempt), optionally auto-merge it.
    /// Transient forge failures throw so the orchestrator retries; soft
    /// outcomes (MR already exists, merge blocked) return partial results.
    /// </summary>
    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        Validation.ValidateBranchName(request.WorkBranch, nameof(request.WorkBranch));
        Validation.ValidateBranchName(request.BaseBranch, nameof(request.BaseBranch));

        var config = ResolveProjectConfig(request.ProjectId);
        var token = ResolveToken(request.TokenEnvVar);
        if (token is null)
            _host.Logger.LogWarning(
                "GitLab project {Project}: no token resolved; attempting anonymous access",
                request.ProjectId);

        // Fail fast on an invalid merge method before pushing anything.
        var mergeBody = ToMergeBody(request.MergeMethod);

        // Numeric project ids have no path form for git/web URLs; resolve
        // the path once up front (free for path-based projects).
        var webPath = await GetProjectWebPathAsync(config, token, ct);
        var gitUrl = $"{config.WebBaseUrl}/{webPath}.git";

        using var auth = GitLabGitAuthScope.Create(token);
        try
        {
            await _gitHost.PushToUpstreamAsync(
                request.RepositoryId,
                gitUrl,
                request.WorkBranch,
                auth.Environment,
                ToReconcileStrategy(request.MergeMethod),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GitLabUpstreamException(
                $"Failed to push work branch '{SanitizeForLog(request.WorkBranch)}': {Scrub(ex.Message, token)}",
                ex);
        }

        int mrIid;
        string? mrUrl;
        if (request.ExistingPullRequestNumber is { } existing)
        {
            var reused = await GetMergeRequestAsync(config, token, existing, ct)
                ?? throw new GitLabUpstreamException(
                    $"GitLab MR !{existing} from a prior attempt is no longer available.");
            mrIid = (int)reused.Iid;
            mrUrl = reused.WebUrl ?? MergeRequestUrl(config, webPath, reused.Iid);
        }
        else
        {
            var created = await CreateMergeRequestAsync(config, token, request, ct);
            if (created is null)
            {
                return new UpstreamCompletionOutcome
                {
                    BranchPushed = true,
                    Notes = "MR creation skipped (branch already has an open MR); leaving it for a human",
                };
            }

            mrIid = (int)created.Iid;
            mrUrl = created.WebUrl ?? MergeRequestUrl(config, webPath, created.Iid);
            _host.Logger.LogInformation("GitLab MR !{Number} opened: {Url}", mrIid, mrUrl);
        }

        if (!request.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = mrUrl,
                PullRequestNumber = mrIid,
            };
        }

        var (mergedSha, mergeNotes, raced) = await MergeRequestAsync(
            config, token, mrIid, mergeBody, ct);
        if (mergedSha is not null)
            _host.Logger.LogInformation("GitLab MR !{Number} auto-merged: {Sha}", mrIid, mergedSha);

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = mrUrl,
            PullRequestNumber = mrIid,
            MergedSha = mergedSha,
            Notes = mergeNotes,
            AutoMergeRaced = raced,
        };
    }

    /// <summary>
    /// Merges <paramref name="sourceBranch"/> into <paramref name="targetBranch"/>
    /// on the GitLab instance via a host-side temp clone (GitLab exposes no
    /// server-side branch-to-branch merge; the release flow only reaches this
    /// provider through the contract, which carries no project context, so the
    /// plugin-scoped project is used). Returns false on merge conflict,
    /// throws on infrastructure failures.
    /// </summary>
    public async Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        Validation.ValidateBranchName(targetBranch, nameof(targetBranch));
        Validation.ValidateBranchName(sourceBranch, nameof(sourceBranch));
        var config = ResolveScopedConfig()
            ?? throw new InvalidOperationException(
                "GitLab upstream merge requires plugin-scoped BaseUrl/Project " +
                "under CodeyBox:Plugins:codeybox.gitlab-upstream.");
        var token = ResolveToken(null);

        var stagingRoot = Path.Combine(Path.GetTempPath(), "codeybox-gitlab-sync-" + Guid.NewGuid().ToString("N")[..8]);
        var cloneDir = Path.Combine(stagingRoot, "repo");
        var gitUrl = $"{config.WebBaseUrl}/{await GetProjectWebPathAsync(config, token, ct)}.git";
        try
        {
            Directory.CreateDirectory(cloneDir);
            using var auth = GitLabGitAuthScope.Create(token);

            var clone = await GitLabGitRunner.RunAsync(
                stagingRoot, auth.Environment, ct,
                "clone", $"--branch={targetBranch}", "--single-branch", "--", gitUrl, cloneDir);
            if (clone.ExitCode != 0)
                throw new GitLabUpstreamException(
                    $"GitLab git clone of '{SanitizeForLog(targetBranch)}' failed: {Scrub(clone.Stderr, token)}");

            var fetch = await GitLabGitRunner.RunAsync(
                cloneDir, auth.Environment, ct, "fetch", "origin", "--", sourceBranch);
            if (fetch.ExitCode != 0)
                throw new GitLabUpstreamException(
                    $"GitLab git fetch of '{SanitizeForLog(sourceBranch)}' failed: {Scrub(fetch.Stderr, token)}");

            var merge = await GitLabGitRunner.RunAsync(
                cloneDir, auth.Environment, ct, "merge", "FETCH_HEAD", "--no-edit", "--no-ff");
            if (merge.ExitCode != 0)
            {
                await GitLabGitRunner.RunAsync(cloneDir, auth.Environment, ct, "merge", "--abort");
                return false;
            }

            var push = await GitLabGitRunner.RunAsync(
                cloneDir, auth.Environment, ct, "push", "origin", "--", targetBranch);
            if (push.ExitCode != 0)
                throw new GitLabUpstreamException(
                    $"GitLab git push of '{SanitizeForLog(targetBranch)}' failed: {Scrub(push.Stderr, token)}");

            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }

    /// <summary>
    /// Fetches <paramref name="baseBranch"/> from the plugin-scoped GitLab
    /// project into the host bare repo. Returns null when the plugin is
    /// not scoped to a project (unsupported, non-fatal) or the branch is
    /// not advertised upstream.
    /// </summary>
    public async Task<string?> FetchBaseBranchAsync(
        string repositoryId, string baseBranch, CancellationToken ct = default)
    {
        Validation.ValidateBranchName(baseBranch, nameof(baseBranch));
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);
        using var auth = GitLabGitAuthScope.Create(token);
        var gitUrl = await GetGitUrlAsync(config, token, ct);
        try
        {
            return await _gitHost.FetchUpstreamBranchAsync(
                repositoryId, gitUrl, baseBranch, auth.Environment, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GitLabUpstreamException(
                $"Failed to fetch base branch '{SanitizeForLog(baseBranch)}' from GitLab: {Scrub(ex.Message, token)}",
                ex);
        }
    }

    // ------------------------------------------------------------------
    // Merge request core: create / read / merge / list
    // ------------------------------------------------------------------

    private async Task<GitLabMergeRequest?> CreateMergeRequestAsync(
        GitLabEndpointConfig config, string? token, UpstreamCompletionRequest request, CancellationToken ct)
    {
        using var response = await SendGitLabAsync(
            config, HttpMethod.Post, config.ProjectPath("merge_requests"), token, ct,
            new
            {
                title = Truncate(request.Title, MaxTitleLength),
                description = Truncate(request.Description ?? string.Empty, MaxBodyLength),
                source_branch = request.WorkBranch,
                target_branch = request.BaseBranch,
            });

        // GitLab answers 409 with an "already exists" body when the source
        // branch already has an open MR: a soft outcome, not infrastructure.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var detail = await ReadErrorDetailAsync(response, ct);
            if (detail.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                _host.Logger.LogWarning(
                    "GitLab: MR already exists for branch '{Branch}' (409); treating as soft outcome",
                    SanitizeForLog(request.WorkBranch));
                return null;
            }

            throw new GitLabUpstreamException(
                $"GitLab rejected MR creation (409){Scrub(detail, token)}.",
                HttpStatusCode.Conflict);
        }

        await EnsureSuccessAsync(response, "create merge request", ct);
        return await ReadMergeRequestAsync(response, ct);
    }

    private async Task<GitLabMergeRequest?> GetMergeRequestAsync(
        GitLabEndpointConfig config, string? token, long iid, CancellationToken ct)
    {
        using var response = await SendGitLabAsync(
            config, HttpMethod.Get, config.ProjectPath($"merge_requests/{iid}"), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "read merge request", ct);
        return await ReadMergeRequestAsync(response, ct);
    }

    private async Task<(string? Sha, string? Notes, bool Raced)> MergeRequestAsync(
        GitLabEndpointConfig config, string? token, long iid, object mergeBody, CancellationToken ct)
    {
        using var response = await SendGitLabAsync(
            config, HttpMethod.Put, config.ProjectPath($"merge_requests/{iid}/merge"), token, ct,
            mergeBody);

        // 405: the MR is not in a mergeable state right now (pipeline still
        // running, base moved under us). The orchestrator treats this as a
        // retryable race: re-fetch base, re-run merge, retry.
        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            _host.Logger.LogWarning(
                "GitLab PUT /merge_requests/{Number}/merge returned 405; leaving MR open for race recovery",
                iid);
            return (null, "Auto-merge raced upstream base motion (405); MR left open", true);
        }

        // 409/422: conflict, approvals missing, pipeline failed, discussions
        // unresolved — blocked, not broken. Leave the MR open for a human.
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            var detail = await ReadErrorDetailAsync(response, ct);
            _host.Logger.LogWarning(
                "GitLab PUT /merge_requests/{Number}/merge returned {Status}; leaving MR open",
                iid, (int)response.StatusCode);
            return (null, $"Auto-merge blocked ({(int)response.StatusCode}{Scrub(detail, token)}); MR left open", false);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new GitLabUpstreamException($"GitLab MR !{iid} was not found for merging.");

        await EnsureSuccessAsync(response, "merge merge request", ct);

        var refreshed = await GetMergeRequestAsync(config, token, iid, ct);
        if (refreshed is not null && string.Equals(refreshed.State, "merged", StringComparison.OrdinalIgnoreCase))
            return (refreshed.EffectiveMergeSha, null, false);
        return (null, "Merge call succeeded but the MR does not report merged; leaving it for a human", false);
    }

    private static object ToMergeBody(string mergeMethod) =>
        mergeMethod.ToLowerInvariant() switch
        {
            // GitLab squash lives on the merge call, not the project: pass it
            // explicitly rather than relying on project defaults.
            "squash" => (object)new { squash = true },
            "merge" => new { squash = false },
            "rebase" => new { squash = false },
            _ => throw new InvalidOperationException(
                $"Upstream MergeMethod '{mergeMethod}' is invalid for GitLab; valid values: merge, squash, rebase"),
        };

    // ------------------------------------------------------------------
    // Configuration
    // ------------------------------------------------------------------

    internal sealed record GitLabEndpointConfig(
        string ApiBaseUrl, string WebBaseUrl, string ProjectId, int PerPage, int MaxListPages)
    {
        public bool ProjectIsNumeric => ProjectId.Length > 0 && ProjectId.All(char.IsDigit);

        public string ProjectPath(string relative) =>
            $"projects/{ProjectId}/{relative}";
    }

    private GitLabEndpointConfig ResolveProjectConfig(ProjectId projectId)
    {
        var project = _upstreamHost.GetProjectUpstreamConfig(projectId);
        var scoped = _host.ScopedConfig;
        string? Get(string key) =>
            project.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : scoped[key];

        if (!TryBuildConfig(Get("BaseUrl"), Get("Project"), Get("PerPage"), Get("MaxListPages"),
                out var config, out var error))
            throw new InvalidOperationException($"Project {projectId}: GitLab upstream {error}");
        return config;
    }

    private GitLabEndpointConfig? ResolveScopedConfig()
    {
        var scoped = _host.ScopedConfig;
        if (!TryBuildConfig(scoped["BaseUrl"], scoped["Project"],
                scoped["PerPage"], scoped["MaxListPages"], out var config, out _))
            return null;
        return config;
    }

    private static bool TryBuildConfig(
        string? baseUrl, string? project, string? perPage, string? maxPages,
        out GitLabEndpointConfig config, out string error)
    {
        config = null!;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(project))
        {
            error = "requires BaseUrl and Project (Upstream.PluginConfig, falling back to plugin-scoped config)";
            return false;
        }

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var apiUri)
            || (apiUri.Scheme != Uri.UriSchemeHttp && apiUri.Scheme != Uri.UriSchemeHttps))
        {
            error = $"BaseUrl '{baseUrl}' must be an absolute http(s) URL";
            return false;
        }
        if (!string.IsNullOrEmpty(apiUri.UserInfo))
        {
            error = "BaseUrl must not embed credentials";
            return false;
        }

        // Accept the instance root or the API root; normalize to the API base.
        var apiBase = apiUri.ToString().TrimEnd('/');
        if (!apiBase.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase))
            apiBase += "/api/v4";
        var webBase = apiBase.EndsWith("/api/v4", StringComparison.OrdinalIgnoreCase)
            ? apiBase[..^"/api/v4".Length]
            : apiBase;

        var trimmedProject = project.Trim().Trim('/');
        string encodedProject;
        if (trimmedProject.All(char.IsDigit) && trimmedProject.Length > 0)
        {
            encodedProject = trimmedProject;
        }
        else
        {
            if (trimmedProject.Any(c => char.IsWhiteSpace(c) || (char.IsControl(c) && c != '\t')))
            {
                error = "Project must be a numeric id or a path like 'group/subgroup/project' without whitespace";
                return false;
            }
            if (trimmedProject.Contains("..", StringComparison.Ordinal)
                || trimmedProject.Contains('?')
                || trimmedProject.Contains('#')
                || trimmedProject.Contains('%'))
            {
                error = "Project path contains characters outside the forge's allowed set";
                return false;
            }
            encodedProject = Uri.EscapeDataString(trimmedProject);
        }

        if (!TryBoundedInt(perPage, DefaultPerPage, 1, 100, out var perPageValue))
        {
            error = "PerPage must be an integer 1..100";
            return false;
        }
        if (!TryBoundedInt(maxPages, DefaultMaxListPages, 1, 50, out var maxPagesValue))
        {
            error = "MaxListPages must be an integer 1..50";
            return false;
        }

        config = new GitLabEndpointConfig(apiBase, webBase, encodedProject, perPageValue, maxPagesValue);
        return true;
    }

    private static bool TryBoundedInt(string? raw, int @default, int min, int max, out int value)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = @default;
            return true;
        }
        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out value)
            || value < min || value > max)
            return false;
        return true;
    }

    private string? ResolveToken(string? requestTokenEnvVar)
    {
        var name = !string.IsNullOrWhiteSpace(requestTokenEnvVar)
            ? requestTokenEnvVar
            : _host.ScopedConfig["TokenEnvVar"];
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Environment.GetEnvironmentVariable(name);
    }

    /// <summary>
    /// The path form of the project for git and web URLs. Path-based
    /// projects decode it locally (no HTTP); numeric ids resolve it through
    /// <c>GET /projects/:id</c> so clone and MR URLs stay correct.
    /// </summary>
    private async Task<string> GetProjectWebPathAsync(
        GitLabEndpointConfig config, string? token, CancellationToken ct)
    {
        if (!config.ProjectIsNumeric)
            return Uri.UnescapeDataString(config.ProjectId);
        using var response = await SendGitLabAsync(
            config, HttpMethod.Get, config.ProjectPath(string.Empty).TrimEnd('/'), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new GitLabUpstreamException(
                $"GitLab project '{config.ProjectId}' was not found; check the Project setting.");
        await EnsureSuccessAsync(response, "read project", ct);
        var project = await DeserializeAsync<GitLabProject>(response, ct);
        if (string.IsNullOrWhiteSpace(project?.PathWithNamespace))
            throw new GitLabUpstreamException("GitLab returned a project without a usable path.");
        return project.PathWithNamespace;
    }

    private async Task<string> GetGitUrlAsync(
        GitLabEndpointConfig config, string? token, CancellationToken ct) =>
        $"{config.WebBaseUrl}/{await GetProjectWebPathAsync(config, token, ct)}.git";

    private static string MergeRequestUrl(GitLabEndpointConfig config, string webPath, long iid) =>
        $"{config.WebBaseUrl}/{webPath}/-/merge_requests/{iid}";

    // ------------------------------------------------------------------
    // HTTP plumbing: auth, failure classification, pagination
    // ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendGitLabAsync(
        GitLabEndpointConfig config, HttpMethod method, string relativePath, string? token, CancellationToken ct, object? body = null)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        // Absolute URL from validated config: the sink carries its own
        // destination rather than relying on ambient client state.
        var url = $"{config.ApiBaseUrl}/{relativePath}";
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("CodeyBox-GitLabUpstream/1.0");
        if (!string.IsNullOrEmpty(token))
            request.Headers.Add("PRIVATE-TOKEN", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            throw new GitLabUpstreamException(
                $"GitLab instance unreachable: {Scrub(ex.Message, token)}", ex);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new GitLabUpstreamException(
                "GitLab rejected the request as unauthorised (401): check the token and its scopes.",
                HttpStatusCode.Unauthorized);
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new GitLabUpstreamException(
                "GitLab forbade the request (403): the token lacks permission for this operation.",
                HttpStatusCode.Forbidden);
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = ParseRetryAfter(response);
            response.Dispose();
            throw new GitLabUpstreamException(
                $"GitLab rate limit exceeded{(retryAfter is null ? string.Empty : $"; retry after {retryAfter}s")}.",
                HttpStatusCode.TooManyRequests, retryAfter);
        }
        if ((int)response.StatusCode >= 500)
        {
            response.Dispose();
            throw new GitLabUpstreamException(
                $"GitLab instance failed with {(int)response.StatusCode} {response.StatusCode}.",
                response.StatusCode);
        }

        return response;
    }

    private static int? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero)
            return (int)delta.TotalSeconds;
        if (!response.Headers.TryGetValues("Retry-After", out var values))
            return null;
        foreach (var value in values)
        {
            if (int.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
                return seconds;
            if (DateTimeOffset.TryParse(value.Trim(), out var date))
            {
                var delta2 = date - DateTimeOffset.UtcNow;
                return delta2.TotalSeconds > 0 ? (int)delta2.TotalSeconds : 0;
            }
        }
        return null;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await ReadErrorDetailAsync(response, ct);
        throw new GitLabUpstreamException(
            $"GitLab {operation} failed with {(int)response.StatusCode} {response.StatusCode}{Scrub(detail, null)}.",
            response.StatusCode);
    }

    private static async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var message))
                {
                    if (message.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(message.GetString()))
                        return $": {Truncate(message.GetString()!, 300)}";
                    if (message.ValueKind == JsonValueKind.Array)
                    {
                        var first = message.EnumerateArray()
                            .FirstOrDefault(e => e.ValueKind == JsonValueKind.String);
                        if (first.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(first.GetString()))
                            return $": {Truncate(first.GetString()!, 300)}";
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the length-only detail below.
            }
            return $" (response body {body.Length} chars, not machine-readable)";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(
        GitLabEndpointConfig config, string? token, string pathAndQuery, CancellationToken ct)
    {
        var items = new List<T>();
        string? nextPage = "1";
        var pages = 0;
        while (nextPage is not null && pages < config.MaxListPages)
        {
            pages++;
            var separator = pathAndQuery.Contains('?') ? "&" : "?";
            using var response = await SendGitLabAsync(
                config, HttpMethod.Get,
                $"{pathAndQuery}{separator}per_page={config.PerPage}&page={nextPage}",
                token, ct);
            await EnsureSuccessAsync(response, "list paged results", ct);
            var pageItems = await DeserializeAsync<IReadOnlyList<T>>(response, ct) ?? [];
            items.AddRange(pageItems);
            nextPage = NextPage(response);
            // No next page, or a short page with no continuation header,
            // means the forge has nothing more. The MaxListPages cap bounds
            // the total instead, so a large repository yields a bounded
            // answer, never an unbounded buffer.
            if (nextPage is null && pageItems.Count < config.PerPage)
                break;
            if (nextPage is null)
                break;
        }
        return items;
    }

    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Next-Page", out var values))
            return null;
        foreach (var value in values)
        {
            var trimmed = value.Trim();
            if (!string.IsNullOrEmpty(trimmed))
                return trimmed;
        }
        return null;
    }

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new GitLabUpstreamException(
                $"GitLab returned a response this provider cannot parse: {ex.Message}", ex);
        }
    }

    private static async Task<GitLabMergeRequest> ReadMergeRequestAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var mr = await DeserializeAsync<GitLabMergeRequest>(response, ct);
        if (mr is null || mr.Iid <= 0)
            throw new GitLabUpstreamException("GitLab returned a merge request without a usable iid.");
        return mr;
    }

    // ------------------------------------------------------------------
    // Small pure helpers
    // ------------------------------------------------------------------

    private static UpstreamPushReconcileStrategy ToReconcileStrategy(string mergeMethod) =>
        mergeMethod.Equals("rebase", StringComparison.OrdinalIgnoreCase)
            ? UpstreamPushReconcileStrategy.Rebase
            : UpstreamPushReconcileStrategy.Merge;

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(empty)";
        var scrubbed = new string(value.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return scrubbed.Length > 256 ? scrubbed[..256] + "…" : scrubbed;
    }

    private static string Scrub(string message, string? token) =>
        string.IsNullOrEmpty(token) ? message : message.Replace(token, "[redacted]", StringComparison.Ordinal);

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}

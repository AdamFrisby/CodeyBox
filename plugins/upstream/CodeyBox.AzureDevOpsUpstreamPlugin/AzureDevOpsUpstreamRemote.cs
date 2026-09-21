using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.AzureDevOpsUpstreamPlugin;

// Azure DevOps Repos upstream remote for CodeyBox.
//
// How it works:
// 1. Operators set Upstream.Kind = "azure-devops" and put the repository
//    coordinates in Upstream.PluginConfig (Organization, Project, Repository).
// 2. CompleteAsync pushes the work branch via the host git module, opens a
//    pull request through the Azure DevOps git REST API, and optionally
//    completes (merges) it.
// 3. The PAT is read from the env var named by Upstream.TokenEnvVar —
//    forwarded as UpstreamCompletionRequest.TokenEnvVar — and is only ever
//    attached to host-side git/HTTP calls. It never reaches a sandbox.
//
// Per-project config keys (Upstream.PluginConfig):
//   Organization — Azure DevOps organisation (cloud) or collection (server)
//   Project      — team project name or GUID
//   Repository   — repository name or GUID
//   InstanceUrl  — instance base URL (default https://dev.azure.com)
//   ApiVersion   — REST api-version (default 7.1)
//
// Operational knobs (same keys, per-project or plugin ScopedConfig section):
//   HttpTimeoutSeconds, PageSize, MaxPages, MaxOpenPullRequests,
//   MaxRetries, RetryBaseDelayMilliseconds, DeleteSourceBranchOnMerge
//
// Failure semantics mirror the contract: forge unreachable / unauthorised /
// rate-limited / rejecting is infrastructure — CompleteAsync throws so the
// orchestrator retries. A soft outcome (PR already exists, merge conflicts)
// returns a partial UpstreamCompletionOutcome without throwing.
//
// See README.md for the support matrix, version requirements and gaps.

/// <summary>
/// Upstream remote plugin for Azure DevOps Repos. Pushes a work branch, opens
/// a pull request via the git REST API, optionally completes it, and reports
/// the extended surfaces Azure DevOps genuinely provides: reviewer votes,
/// build validations + commit statuses, PR threads, service-hook
/// subscriptions and repository/policy metadata. Concepts with no Azure
/// DevOps equivalent (forge releases, work-item tracking) stay on the
/// contract defaults (unsupported) rather than being force-translated.
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.azure-devops-upstream",
    displayName: "Azure DevOps Upstream Remote",
    minHostApiVersion: "1.0")]
public sealed class AzureDevOpsUpstreamRemote : IUpstreamRemote, IPluginInitializer
{
    /// <summary>Matches <c>Upstream.Kind = "azure-devops"</c> in project config.</summary>
    public string Name => "azure-devops";

    // Azure DevOps policy-type GUIDs consulted for review/check metadata.
    private static readonly string ReviewerPolicyTypeId = "fa4e907d-c16b-4a4c-9dfa-4916e5d171ab";
    private static readonly string BuildPolicyTypeId = "0609b279-4645-4f57-8c96-0c9a1518102f";

    // The only webhook scope this forge natively models: service-hook
    // subscriptions live under a project (optionally narrowed to one
    // repository via publisher inputs). Anything else returns null
    // (unsupported) rather than a coerced translation.
    private static readonly string ServiceHookScope = "project";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;

    private IPluginHost _host = null!;
    private IUpstreamPluginHost _upstreamHost = null!;
    private bool _initialized;

    public AzureDevOpsUpstreamRemote(IGitHost gitHost, IHttpClientFactory httpClientFactory)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _host = context.Host;
        _upstreamHost = context.Host as IUpstreamPluginHost
            ?? throw new InvalidOperationException(
                "Azure DevOps plugin requires a host implementing IUpstreamPluginHost.");
        _initialized = true;
        _host.Logger.LogInformation("AzureDevOpsUpstreamRemote initialized");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Core lifecycle
    // ------------------------------------------------------------------

    public async Task<UpstreamPushResult> PushAsync(
        string repositoryId, string branch, CancellationToken ct = default)
    {
        // Push-only flows carry no project id, so coordinates and the token
        // env-var name come from the operator ScopedConfig section.
        var opts = OptionsFor(null);
        if (!opts.HasCoordinates())
            return new UpstreamPushResult(false,
                "Azure DevOps plugin is not configured (Organization/Project/Repository missing); use CompleteAsync");
        if (!IsValidBranch(branch))
            return new UpstreamPushResult(false, $"Invalid branch name: '{SanitizeForLog(branch)}'");

        var token = ReadScopedToken();
        if (string.IsNullOrEmpty(token))
            return new UpstreamPushResult(false,
                "Azure DevOps plugin requires a PAT (ScopedConfig TokenEnvVar names the env var holding it)");

        using var askpass = GitAskPass.Create(token);
        try
        {
            await _gitHost.PushToUpstreamAsync(
                repositoryId, GitUrl(opts), branch, askpass.Environment,
                UpstreamPushReconcileStrategy.Rebase, ct);
            return new UpstreamPushResult(true, null);
        }
        catch (Exception ex)
        {
            return new UpstreamPushResult(false, Scrub(ex.Message, token));
        }
    }

    /// <summary>
    /// Full Azure DevOps completion flow: push work branch, open a pull
    /// request (or reuse <c>ExistingPullRequestNumber</c> on race recovery),
    /// optionally complete it. Transient forge failures throw for
    /// orchestrator retry; 409-exists and unmergeable-complete return partial
    /// outcomes without throwing.
    /// </summary>
    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        EnsureInitialized();
        var opts = OptionsFor(request.ProjectId);
        opts.RequireCoordinates(request.ProjectId);
        ValidateBranch(request.WorkBranch, nameof(request.WorkBranch));
        ValidateBranch(request.BaseBranch, nameof(request.BaseBranch));
        if (string.IsNullOrWhiteSpace(request.Title))
            throw new ArgumentException("PR title must not be empty.", nameof(request));

        var token = ReadToken(request);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                $"Project {request.ProjectId}: Azure DevOps plugin requires Upstream.TokenEnvVar " +
                "to name an env var holding the PAT");

        using var askpass = GitAskPass.Create(token);
        try
        {
            await _gitHost.PushToUpstreamAsync(
                request.RepositoryId, GitUrl(opts), request.WorkBranch,
                askpass.Environment, ToReconcileStrategy(request.MergeMethod), ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to push work branch '{SanitizeForLog(request.WorkBranch)}': {Scrub(ex.Message, token)}",
                ex);
        }

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");

        int prId;
        if (request.ExistingPullRequestNumber is { } existing)
        {
            prId = existing;
        }
        else
        {
            var created = await CreatePullRequestAsync(client, opts, token, request, ct);
            if (created is null)
            {
                return new UpstreamCompletionOutcome
                {
                    BranchPushed = true,
                    Notes = "PR creation skipped (409 — a pull request already exists for these branches)",
                };
            }
            prId = created.Value;
            _host.Logger.LogInformation(
                "Azure DevOps PR #{Id} opened: {Url}", prId, PullRequestUrl(opts, prId));
        }

        var prUrl = PullRequestUrl(opts, prId);
        if (!request.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = prUrl,
                PullRequestNumber = prId,
            };
        }

        var (mergedSha, notes, raced) = await CompletePullRequestAsync(
            client, opts, token, prId, request, ct);
        if (mergedSha is not null)
            _host.Logger.LogInformation(
                "Azure DevOps PR #{Id} completed: {Sha}", prId, mergedSha);

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = prUrl,
            PullRequestNumber = prId,
            MergedSha = mergedSha,
            Notes = notes,
            AutoMergeRaced = raced,
        };
    }

    /// <summary>
    /// Merges <paramref name="sourceBranch"/> into <paramref name="targetBranch"/>
    /// with a host-side git merge+push (Azure DevOps exposes no direct
    /// branch-merge API). Returns false on merge conflict, true on success or
    /// when the target is already up-to-date. Throws on infrastructure failures.
    /// Coordinates come from the operator ScopedConfig section: this path
    /// carries no project id.
    /// </summary>
    public async Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        EnsureInitialized();
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        ValidateBranch(targetBranch, nameof(targetBranch));
        ValidateBranch(sourceBranch, nameof(sourceBranch));

        var token = ReadScopedToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "Azure DevOps plugin requires a PAT (ScopedConfig TokenEnvVar names the env var holding it)");

        var url = GitUrl(opts);
        var tmpDir = Path.Combine(Path.GetTempPath(), "codeybox-ado-sync-" + Guid.NewGuid().ToString("N")[..8]);
        using var askpass = GitAskPass.Create(token);
        try
        {
            Directory.CreateDirectory(tmpDir);
            var clone = await RunGitAsync(tmpDir, ct, askpass.Environment,
                "clone", "--branch", targetBranch, "--single-branch", "--", url, tmpDir);
            if (clone.ExitCode != 0)
                throw new InvalidOperationException($"git clone failed: {Scrub(clone.Stderr, token)}");

            var fetch = await RunGitAsync(tmpDir, ct, askpass.Environment,
                "fetch", "origin", sourceBranch);
            if (fetch.ExitCode != 0)
                throw new InvalidOperationException($"git fetch failed: {Scrub(fetch.Stderr, token)}");

            var merge = await RunGitAsync(tmpDir, ct, askpass.Environment,
                "merge", "FETCH_HEAD", "--no-edit", "--no-ff");
            if (merge.ExitCode != 0)
            {
                await RunGitAsync(tmpDir, ct, askpass.Environment, "merge", "--abort");
                return false;
            }

            var push = await RunGitAsync(tmpDir, ct, askpass.Environment,
                "push", "origin", targetBranch);
            if (push.ExitCode != 0)
                throw new InvalidOperationException($"git push failed: {Scrub(push.Stderr, token)}");
            return true;
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Fetches <paramref name="baseBranch"/> from Azure DevOps into the host
    /// bare repo, overwriting the local ref. Returns the new sha, or null
    /// when the upstream does not advertise the branch.
    /// </summary>
    public async Task<string?> FetchBaseBranchAsync(
        string repositoryId, string baseBranch, CancellationToken ct = default)
    {
        EnsureInitialized();
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        ValidateBranch(baseBranch, nameof(baseBranch));

        var token = ReadScopedToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "Azure DevOps plugin requires a PAT (ScopedConfig TokenEnvVar names the env var holding it)");

        using var askpass = GitAskPass.Create(token);
        return await _gitHost.FetchUpstreamBranchAsync(
            repositoryId, GitUrl(opts), baseBranch, askpass.Environment, ct);
    }

    // ------------------------------------------------------------------
    // Pull request reads
    // ------------------------------------------------------------------

    /// <summary>
    /// Lists active pull requests whose source branch starts with
    /// <paramref name="branchPrefix"/>. Follows the forge continuation token
    /// until exhausted (bounded by MaxPages/MaxOpenPullRequests) so a large
    /// repository never yields a partial answer that reads as complete.
    /// PRs whose merge status is still unknown are skipped for the next tick.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(
        string branchPrefix, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (string.IsNullOrEmpty(branchPrefix))
            throw new ArgumentException("branchPrefix must be non-empty", nameof(branchPrefix));
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var listed = new List<UpstreamPullRequest>();
        string? continuation = null;
        for (var page = 1; page <= opts.MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var url = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}/pullrequests" +
                $"?searchCriteria.status=active&$top={opts.PageSize}&api-version={opts.ApiVersion}";
            if (!string.IsNullOrEmpty(continuation))
                url += $"&continuationToken={Uri.EscapeDataString(continuation)}";

            using var response = await SendGetAsync(client, url, "GET /pullrequests", token, opts, ct);
            var pageResult = await ReadJsonAsync<AdoPullRequestList>(response, ct);
            var items = pageResult?.Value ?? [];
            foreach (var pr in items)
            {
                if (listed.Count >= opts.MaxOpenPullRequests)
                    break;
                var sourceBranch = StripRefsHeads(pr.SourceRefName);
                if (string.IsNullOrEmpty(sourceBranch)
                    || !sourceBranch.StartsWith(branchPrefix, StringComparison.Ordinal))
                    continue;
                // mergeStatus null/notSet means the forge has not computed
                // mergeability yet — skip so the sweeper reconsiders next tick.
                if (string.IsNullOrEmpty(pr.MergeStatus)
                    || pr.MergeStatus.Equals("notSet", StringComparison.OrdinalIgnoreCase))
                    continue;
                listed.Add(new UpstreamPullRequest
                {
                    Number = pr.PullRequestId,
                    Url = PullRequestUrl(opts, pr.PullRequestId),
                    HeadBranch = sourceBranch,
                    HeadSha = pr.LastMergeSourceCommit?.CommitId ?? string.Empty,
                    BaseBranch = StripRefsHeads(pr.TargetRefName),
                    HasMergeConflict = pr.MergeStatus.Equals("conflicts", StringComparison.OrdinalIgnoreCase),
                });
            }
            continuation = ContinuationToken(response);
            // The forge continuation token is authoritative: a page may be
            // short yet followed by more results, so only its absence ends
            // the walk (bounded above by MaxPages).
            if (string.IsNullOrEmpty(continuation))
                break;
        }
        return listed;
    }

    /// <summary>
    /// Reads a pull request by id. Returns null on 404 (unavailable).
    /// </summary>
    public async Task<UpstreamPullRequestState?> GetPullRequestAsync(
        int number, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var url = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}/pullrequests/{number}?api-version={opts.ApiVersion}";
        using var response = await SendGetAsync(client, url, $"GET /pullrequests/{number}", token, opts, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowIfForgeError(response, $"GET /pullrequests/{number}", token);
        var pr = await ReadJsonAsync<AdoPullRequest>(response, ct);
        if (pr is null)
            return null;
        var status = pr.Status?.ToLowerInvariant() switch
        {
            "active" => PullRequestStatus.Open,
            "completed" => PullRequestStatus.Merged,
            _ => PullRequestStatus.Closed,
        };
        return new UpstreamPullRequestState(
            pr.PullRequestId,
            PullRequestUrl(opts, pr.PullRequestId),
            status,
            status == PullRequestStatus.Merged ? pr.LastMergeCommit?.CommitId : null);
    }

    // ------------------------------------------------------------------
    // Extended surfaces: reviews, checks, comments
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads reviewer votes (required reviewers plus the minimum-approver
    /// policy quorum where configured). A non-null empty result means the
    /// forge supports reviews and there are none — never "cannot tell".
    /// </summary>
    public async Task<UpstreamReviewState?> GetReviewStateAsync(
        int number, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var url = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}/pullrequests/{number}/reviewers?api-version={opts.ApiVersion}";
        using var response = await SendGetAsync(client, url, $"GET /pullrequests/{number}/reviewers", token, opts, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowIfForgeError(response, "GET /pullrequests/reviewers", token);
        var list = await ReadJsonAsync<AdoReviewerList>(response, ct);
        var reviewers = list?.Value ?? [];

        var reviews = reviewers
            .Select(r => new UpstreamReview
            {
                Reviewer = !string.IsNullOrWhiteSpace(r.DisplayName)
                    ? r.DisplayName
                    : !string.IsNullOrWhiteSpace(r.UniqueName) ? r.UniqueName : $"reviewer-{r.Id}",
                Verdict = MapVote(r.Vote),
                SubmittedAt = null,
            })
            .ToList();

        var required = reviewers
            .Where(r => r.IsRequired && r.Vote <= 0)
            .Select(r => !string.IsNullOrWhiteSpace(r.DisplayName)
                ? r.DisplayName!
                : !string.IsNullOrWhiteSpace(r.UniqueName) ? r.UniqueName! : $"reviewer-{r.Id}")
            .ToList();

        var quorum = await ReadMinimumApproverCountAsync(client, opts, token, number, ct);
        var met = reviewers.All(r => r.Vote >= 0)
            && reviewers.Where(r => r.IsRequired).All(r => r.Vote > 0);

        return new UpstreamReviewState
        {
            Reviews = reviews,
            RequiredReviewers = required,
            RequiredApprovalCount = quorum,
            RequirementsMet = met,
        };
    }

    /// <summary>
    /// Reads build validations (by source sha) plus commit statuses for
    /// <paramref name="headSha"/>. RequiredChecksPassed is true only when
    /// every reported check passed, was neutral or was skipped. A non-null
    /// empty result means the forge requires nothing for this sha.
    /// </summary>
    public async Task<UpstreamCheckSummary?> GetCheckResultsAsync(
        string headSha, CancellationToken ct = default)
    {
        EnsureInitialized();
        ValidateSha(headSha);
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var checks = new List<UpstreamCheckResult>();

        var statuses = await GetCommitStatusesAsync(client, opts, token, headSha, ct);
        if (statuses is null)
            return null;
        checks.AddRange(statuses);

        var repoId = await ResolveRepositoryIdAsync(client, opts, token, ct);
        if (repoId is not null)
            checks.AddRange(await GetBuildsForShaAsync(client, opts, token, repoId, headSha, ct));

        return new UpstreamCheckSummary
        {
            Checks = checks,
            RequiredChecksPassed = checks.All(c =>
                c.State is UpstreamCheckState.Passing or UpstreamCheckState.Neutral or UpstreamCheckState.Skipped),
        };
    }

    /// <summary>
    /// Lists PR threads flattened oldest-first; code-anchored threads carry
    /// FilePath/Line. Returns null when the PR is unavailable.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamComment>?> ListCommentsAsync(
        int number, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var threads = await GetThreadsAsync(
            _httpClientFactory.CreateClient("azure-devops-upstream"), opts, token, number, ct);
        if (threads is null)
            return null;
        var comments = new List<UpstreamComment>();
        foreach (var thread in threads)
        {
            if (thread.Status?.Equals("deleted", StringComparison.OrdinalIgnoreCase) == true)
                continue;
            foreach (var comment in thread.Comments ?? [])
            {
                if (string.IsNullOrEmpty(comment.Content))
                    continue;
                comments.Add(MapThreadComment(thread, comment));
            }
        }
        return comments;
    }

    /// <summary>
    /// Posts a top-level or file-anchored thread, or appends to the thread
    /// named by <c>ReplyToId</c>. Returns null when the PR is unavailable.
    /// </summary>
    public async Task<UpstreamComment?> PostCommentAsync(
        int number, NewUpstreamComment comment, CancellationToken ct = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(comment);
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var baseUrl = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}/pullrequests/{number}";

        if (!string.IsNullOrEmpty(comment.ReplyToId))
        {
            var threadId = ParseThreadId(comment.ReplyToId);
            var url = $"{baseUrl}/threads/{threadId}/comments?api-version={opts.ApiVersion}";
            using var replyReq = PostJson(url, token,
                new { content = comment.Body, commentType = 1 });
            using var replyResp = await SendAsync(client, replyReq, "POST /threads/comments", token, opts, ct);
            if (replyResp.StatusCode == HttpStatusCode.NotFound)
                return null;
            ThrowIfForgeError(replyResp, "POST /threads/comments", token);
            var posted = await ReadJsonAsync<AdoComment>(replyResp, ct);
            return posted is null ? null : MapThreadComment(null, posted, threadId);
        }

        object threadContext = comment.FilePath is not null
            ? new
            {
                filePath = "/" + comment.FilePath.TrimStart('/'),
                rightFileStart = new { line = comment.Line ?? 1, offset = 1 },
                rightFileEnd = new { line = comment.Line ?? 1, offset = 1 },
            }
            : new { };
        var threadUrl = $"{baseUrl}/threads?api-version={opts.ApiVersion}";
        using var threadReq = PostJson(threadUrl, token,
            new { comments = new[] { new { content = comment.Body, commentType = 1 } }, status = 1, threadContext });
        using var threadResp = await SendAsync(client, threadReq, "POST /threads", token, opts, ct);
        if (threadResp.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowIfForgeError(threadResp, "POST /threads", token);
        var thread = await ReadJsonAsync<AdoThread>(threadResp, ct);
        var first = thread?.Comments?.FirstOrDefault(c => !string.IsNullOrEmpty(c.Content));
        return first is null ? null : MapThreadComment(thread, first);
    }

    // ------------------------------------------------------------------
    // Extended surfaces: service-hook subscriptions, repository metadata
    // ------------------------------------------------------------------

    /// <summary>
    /// Lists service-hook subscriptions. Empty means supported-but-none;
    /// null means the endpoint is unavailable (hooks disabled on the instance).
    /// </summary>
    public async Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(
        CancellationToken ct = default)
    {
        EnsureInitialized();
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var url = $"{InstanceApi(opts)}/hooks/subscriptions?api-version={opts.ApiVersion}";
        using var response = await SendGetAsync(client, url, "GET /hooks/subscriptions", token, opts, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowIfForgeError(response, "GET /hooks/subscriptions", token);
        var list = await ReadJsonAsync<AdoSubscriptionList>(response, ct);
        return (list?.Value ?? [])
            .Where(s => s.Id is not null)
            .Select(s => new UpstreamWebhookSubscription
            {
                Id = s.Id!,
                Scope = ServiceHookScope,
                Events = s.EventType is not null ? [s.EventType] : [],
                TargetUrl = s.ConsumerInputs is not null && s.ConsumerInputs.TryGetValue("url", out var target)
                    ? target : string.Empty,
            })
            .ToList();
    }

    /// <summary>
    /// Creates one service-hook subscription per requested event and returns
    /// the first. Only the native <c>"project"</c> scope is accepted; any
    /// other scope returns null (unsupported) instead of a coerced mapping.
    /// </summary>
    public async Task<UpstreamWebhookSubscription?> CreateWebhookSubscriptionAsync(
        NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(subscription);
        if (!subscription.Scope.Equals(ServiceHookScope, StringComparison.OrdinalIgnoreCase))
            return null;
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var (projectId, repositoryId) = await ResolveScopeIdsAsync(client, opts, token, ct);

        UpstreamWebhookSubscription? first = null;
        var createdIds = new List<string>();
        try
        {
            foreach (var eventName in subscription.Events)
            {
                ct.ThrowIfCancellationRequested();
                var publisherInputs = new Dictionary<string, string>();
                if (projectId is not null)
                    publisherInputs["projectId"] = projectId;
                if (repositoryId is not null)
                    publisherInputs["repository"] = repositoryId;
                var url = $"{InstanceApi(opts)}/hooks/subscriptions?api-version={opts.ApiVersion}";
                using var req = PostJson(url, token, new
                {
                    publisherId = "tfs",
                    eventType = eventName,
                    resourceVersion = "1.0",
                    consumerId = "webHooks",
                    consumerActionId = "httpRequest",
                    publisherInputs,
                    consumerInputs = new { url = subscription.TargetUrl },
                });
                using var response = await SendAsync(client, req, "POST /hooks/subscriptions", token, opts, ct);
                ThrowIfForgeError(response, "POST /hooks/subscriptions", token);
                var created = await ReadJsonAsync<AdoSubscription>(response, ct);
                if (created?.Id is null)
                    throw new InvalidOperationException(
                        "Azure DevOps POST /hooks/subscriptions returned success but no subscription id could be deserialised");
                createdIds.Add(created.Id);
                first ??= new UpstreamWebhookSubscription
                {
                    Id = created.Id,
                    Scope = ServiceHookScope,
                    Events = [eventName],
                    TargetUrl = subscription.TargetUrl,
                };
            }
        }
        catch (Exception ex)
        {
            // Multi-event creation is not atomic on the forge: remove what we
            // created so a retry does not stack duplicate subscriptions.
            _host.Logger.LogWarning(
                "Azure DevOps service-hook creation failed after {Count} subscription(s); rolling back: {Message}",
                createdIds.Count, Scrub(ex.Message, token));
            foreach (var createdId in createdIds)
            {
                try { await DeleteWebhookSubscriptionAsync(createdId, CancellationToken.None); }
                catch { /* best-effort rollback */ }
            }
            throw;
        }
        if (first is not null)
            _host.Logger.LogInformation(
                "Azure DevOps service-hook subscription created for {Events}", subscription.Events.Count);
        return first;
    }

    /// <summary>
    /// Deletes a service-hook subscription: true removed, false unknown id,
    /// null when the endpoint is unavailable.
    /// </summary>
    public async Task<bool?> DeleteWebhookSubscriptionAsync(
        string id, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Subscription id must not be empty.", nameof(id));
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var url = $"{InstanceApi(opts)}/hooks/subscriptions/{Uri.EscapeDataString(id)}?api-version={opts.ApiVersion}";
        using var req = new HttpRequestMessage(HttpMethod.Delete, url);
        req.Headers.Authorization = BasicAuth(token);
        using var response = await SendAsync(client, req, "DELETE /hooks/subscriptions", token, opts, ct);
        if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent)
            return true;
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        ThrowIfForgeError(response, "DELETE /hooks/subscriptions", token);
        return false;
    }

    /// <summary>
    /// Reads default branch, project visibility and branch policies (reviewer
    /// quorum + build validations per ref scope). Reports what the forge has;
    /// policy lookup is best-effort and degrades to an empty rule list.
    /// </summary>
    public async Task<UpstreamRepositoryMetadata?> GetRepositoryMetadataAsync(
        CancellationToken ct = default)
    {
        EnsureInitialized();
        var opts = OptionsFor(null);
        opts.RequireCoordinates("Azure DevOps plugin ScopedConfig");
        var token = RequireScopedToken();

        var client = _httpClientFactory.CreateClient("azure-devops-upstream");
        var repo = await GetRepositoryAsync(client, opts, token, ct);
        if (repo is null)
            return null;

        string? visibility = null;
        try
        {
            var project = await GetProjectAsync(client, opts, token, ct);
            visibility = project?.Visibility?.ToLowerInvariant() switch
            {
                "public" => UpstreamRepositoryVisibility.Public,
                "private" => UpstreamRepositoryVisibility.Private,
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogDebug("Azure DevOps project lookup failed; leaving visibility unset: {Message}",
                Scrub(ex.Message, token));
        }

        IReadOnlyList<UpstreamBranchProtection> protections = [];
        try
        {
            protections = await ReadBranchProtectionsAsync(client, opts, token, repo.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogDebug("Azure DevOps policy lookup failed; leaving branch protections empty: {Message}",
                Scrub(ex.Message, token));
        }

        return new UpstreamRepositoryMetadata
        {
            DefaultBranch = StripRefsHeads(repo.DefaultBranch),
            Visibility = visibility,
            BranchProtections = protections,
        };
    }

    // Forge releases have no Azure DevOps equivalent (no PR-linked release
    // object), so the contract default (null = unsupported) stands. The
    // method is intentionally not overridden.

    // ------------------------------------------------------------------
    // Pull request writes
    // ------------------------------------------------------------------

    private async Task<int?> CreatePullRequestAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token,
        UpstreamCompletionRequest request, CancellationToken ct)
    {
        var url = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}/pullrequests?api-version={opts.ApiVersion}";
        using var req = PostJson(url, token, new
        {
            sourceRefName = $"refs/heads/{request.WorkBranch}",
            targetRefName = $"refs/heads/{request.BaseBranch}",
            title = request.Title,
            description = request.Description ?? string.Empty,
        });
        using var response = await SendAsync(client, req, "POST /pullrequests", token, opts, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            _host.Logger.LogWarning(
                "Azure DevOps POST /pullrequests returned 409 for head='{Branch}'; treating as already-exists",
                SanitizeForLog(request.WorkBranch));
            return null;
        }
        ThrowIfForgeError(response, "POST /pullrequests", token);
        var created = await ReadJsonAsync<AdoPullRequest>(response, ct);
        return created?.PullRequestId is { } id and > 0 ? id : throw new InvalidOperationException(
            "Azure DevOps POST /pullrequests returned success but no pull request id could be deserialised");
    }

    private async Task<(string? Sha, string? Notes, bool Raced)> CompletePullRequestAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token,
        int prId, UpstreamCompletionRequest request, CancellationToken ct)
    {
        var prUrl = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}/pullrequests/{prId}?api-version={opts.ApiVersion}";
        AdoPullRequest? current;
        using (var getReq = GetJson(prUrl, token))
        using (var getResp = await SendAsync(client, getReq, $"GET /pullrequests/{prId}", token, opts, ct))
        {
            if (getResp.StatusCode == HttpStatusCode.NotFound)
                return (null, $"PR #{prId} is no longer available; left open", false);
            ThrowIfForgeError(getResp, $"GET /pullrequests/{prId}", token);
            current = await ReadJsonAsync<AdoPullRequest>(getResp, ct);
        }

        var sourceCommit = current?.LastMergeSourceCommit?.CommitId;
        if (current?.Status?.Equals("completed", StringComparison.OrdinalIgnoreCase) == true)
            return (current.LastMergeCommit?.CommitId, "PR was already completed", false);

        using var patchReq = new HttpRequestMessage(new HttpMethod("PATCH"), prUrl);
        patchReq.Headers.Authorization = BasicAuth(token);
        patchReq.Content = JsonContent.Create(new
        {
            status = "completed",
            lastMergeSourceCommit = sourceCommit is null ? null : new { commitId = sourceCommit },
            completionOptions = new
            {
                mergeStrategy = MapMergeStrategy(request.MergeMethod),
                deleteSourceBranch = opts.DeleteSourceBranchOnMerge,
            },
        }, options: JsonOptions);
        using var patchResp = await SendAsync(client, patchReq, $"PATCH /pullrequests/{prId}", token, opts, ct);
        if (patchResp.StatusCode == HttpStatusCode.Conflict)
        {
            const string note = "Azure DevOps PATCH /pullrequests returned 409 " +
                "(PR not mergeable — likely a race against upstream base; orchestrator will re-fetch base and re-run merge phase)";
            _host.Logger.LogWarning(
                "Azure DevOps PATCH /pullrequests/{Id} returned 409 (PR not mergeable); orchestrator will re-fetch base and re-run merge phase",
                prId);
            return (null, note, true);
        }
        ThrowIfForgeError(patchResp, $"PATCH /pullrequests/{prId}", token);
        var completed = await ReadJsonAsync<AdoPullRequest>(patchResp, ct);
        return (completed?.LastMergeCommit?.CommitId, null, false);
    }

    // ------------------------------------------------------------------
    // Review/check/comment/policy helpers
    // ------------------------------------------------------------------

    private async Task<int> ReadMinimumApproverCountAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token, int prId, CancellationToken ct)
    {
        try
        {
            var repoId = await ResolveRepositoryIdAsync(client, opts, token, ct);
            var policies = await GetPolicyConfigurationsAsync(client, opts, token, ct);
            return policies
                .Where(p => p.IsEnabled
                    && p.Type?.Id?.Equals(ReviewerPolicyTypeId, StringComparison.OrdinalIgnoreCase) == true
                    && PolicyAppliesToRepo(p, repoId, opts.Repository))
                .Select(p => p.Settings?.MinimumApproverCount ?? 0)
                .DefaultIfEmpty(0)
                .Max();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogDebug("Azure DevOps reviewer-policy lookup failed; quorum stays 0: {Message}",
                Scrub(ex.Message, token));
            return 0;
        }
    }

    private async Task<IReadOnlyList<UpstreamCheckResult>?> GetCommitStatusesAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token, string sha, CancellationToken ct)
    {
        var url = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}/commits/{sha}/statuses?api-version={opts.ApiVersion}";
        using var response = await SendGetAsync(client, url, "GET /commits/statuses", token, opts, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowIfForgeError(response, "GET /commits/statuses", token);
        var list = await ReadJsonAsync<AdoCommitStatusList>(response, ct);
        return (list?.Value ?? [])
            .Select(s => new UpstreamCheckResult
            {
                Name = !string.IsNullOrWhiteSpace(s.Context?.Name)
                    ? s.Context.Name!
                    : $"status-{s.Id}",
                State = s.State?.ToLowerInvariant() switch
                {
                    "succeeded" => UpstreamCheckState.Passing,
                    "failed" or "error" => UpstreamCheckState.Failing,
                    "pending" or "notset" => UpstreamCheckState.Pending,
                    "notapplicable" => UpstreamCheckState.Skipped,
                    _ => UpstreamCheckState.Neutral,
                },
                DetailsUrl = s.TargetUrl,
                Description = s.Description,
            })
            .ToList();
    }

    private async Task<IReadOnlyList<UpstreamCheckResult>> GetBuildsForShaAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token,
        string repoId, string sha, CancellationToken ct)
    {
        try
        {
            var url = $"{InstanceApi(opts)}/{Esc(opts.Organization)}/{Esc(opts.Project)}/_apis/build/builds" +
                $"?repositoryId={Uri.EscapeDataString(repoId)}&repositoryType=TfsGit" +
                $"&sourceVersion={sha}&$top=50&api-version={opts.ApiVersion}";
            using var response = await SendGetAsync(client, url, "GET /build/builds", token, opts, ct);
            ThrowIfForgeError(response, "GET /build/builds", token);
            var list = await ReadJsonAsync<AdoBuildList>(response, ct);
            return (list?.Value ?? [])
                .Select(b => new UpstreamCheckResult
                {
                    Name = !string.IsNullOrWhiteSpace(b.Definition?.Name)
                        ? b.Definition.Name!
                        : !string.IsNullOrWhiteSpace(b.BuildNumber) ? b.BuildNumber! : $"build-{b.Id}",
                    State = MapBuild(b.Status, b.Result),
                    DetailsUrl = b.Links?.Web?.Href,
                    Description = b.Status,
                })
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogDebug("Azure DevOps build lookup failed; reporting commit statuses only: {Message}",
                Scrub(ex.Message, token));
            return [];
        }
    }

    private async Task<IReadOnlyList<AdoThread>?> GetThreadsAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token, int prId, CancellationToken ct)
    {
        var url = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}/pullrequests/{prId}/threads?api-version={opts.ApiVersion}";
        using var response = await SendGetAsync(client, url, $"GET /pullrequests/{prId}/threads", token, opts, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowIfForgeError(response, "GET /pullrequests/threads", token);
        var list = await ReadJsonAsync<AdoThreadList>(response, ct);
        return list?.Value ?? [];
    }

    private async Task<IReadOnlyList<UpstreamBranchProtection>> ReadBranchProtectionsAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token,
        string? repoId, CancellationToken ct)
    {
        var policies = await GetPolicyConfigurationsAsync(client, opts, token, ct);
        var reviewerByScope = policies
            .Where(p => p.IsEnabled
                && p.Type?.Id?.Equals(ReviewerPolicyTypeId, StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(p => (p.Settings?.Scope ?? []).Select(s => (Policy: p, Scope: s)))
            .ToList();
        var buildScopes = policies
            .Where(p => p.IsEnabled
                && p.Type?.Id?.Equals(BuildPolicyTypeId, StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(p => (p.Settings?.Scope ?? []).Select(s => (Policy: p, Scope: s)))
            .ToList();

        var protections = new List<UpstreamBranchProtection>();
        foreach (var (policy, scope) in reviewerByScope)
        {
            if (!ScopeMatchesRepo(scope.RepositoryId, repoId, opts.Repository))
                continue;
            var pattern = StripRefsHeads(scope.RefName);
            if (string.IsNullOrEmpty(pattern))
                pattern = "*";
            var requiresChecks = buildScopes.Any(b =>
                string.Equals(StripRefsHeads(b.Scope.RefName), pattern, StringComparison.Ordinal)
                && ScopeMatchesRepo(b.Scope.RepositoryId, repoId, opts.Repository));
            protections.Add(new UpstreamBranchProtection(
                pattern, Math.Max(0, policy.Settings?.MinimumApproverCount ?? 0), requiresChecks));
        }
        return protections;
    }

    // ------------------------------------------------------------------
    // Repository / project / policy resolution
    // ------------------------------------------------------------------

    private async Task<string?> ResolveRepositoryIdAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token, CancellationToken ct)
    {
        var repo = await GetRepositoryAsync(client, opts, token, ct);
        return repo?.Id;
    }

    private async Task<AdoRepository?> GetRepositoryAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token, CancellationToken ct)
    {
        var url = $"{ProjectApi(opts)}/git/repositories/{Esc(opts.Repository)}?api-version={opts.ApiVersion}";
        using var response = await SendGetAsync(client, url, "GET /git/repositories", token, opts, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowIfForgeError(response, "GET /git/repositories", token);
        return await ReadJsonAsync<AdoRepository>(response, ct);
    }

    private async Task<AdoProject?> GetProjectAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token, CancellationToken ct)
    {
        var url = $"{InstanceApi(opts)}/_apis/projects/{Esc(opts.Project)}?api-version={opts.ApiVersion}";
        using var response = await SendGetAsync(client, url, "GET /_apis/projects", token, opts, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        ThrowIfForgeError(response, "GET /_apis/projects", token);
        return await ReadJsonAsync<AdoProject>(response, ct);
    }

    private async Task<IReadOnlyList<AdoPolicyConfiguration>> GetPolicyConfigurationsAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token, CancellationToken ct)
    {
        var url = $"{ProjectApi(opts)}/policy/configurations?api-version={opts.ApiVersion}";
        using var response = await SendGetAsync(client, url, "GET /policy/configurations", token, opts, ct);
        ThrowIfForgeError(response, "GET /policy/configurations", token);
        var list = await ReadJsonAsync<AdoPolicyConfigurationList>(response, ct);
        return list?.Value ?? [];
    }

    private async Task<(string? ProjectId, string? RepositoryId)> ResolveScopeIdsAsync(
        HttpClient client, AzureDevOpsUpstreamOptions opts, string token, CancellationToken ct)
    {
        string? projectId = null;
        string? repositoryId = null;
        try
        {
            projectId = (await GetProjectAsync(client, opts, token, ct))?.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogDebug("Azure DevOps project resolution failed; subscription will be organisation-wide: {Message}",
                Scrub(ex.Message, token));
        }
        try
        {
            repositoryId = await ResolveRepositoryIdAsync(client, opts, token, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _host.Logger.LogDebug("Azure DevOps repository resolution failed; subscription will be project-wide: {Message}",
                Scrub(ex.Message, token));
        }
        return (projectId, repositoryId);
    }

    // ------------------------------------------------------------------
    // HTTP plumbing: auth, timeouts, bounded retries, failure mapping
    // ------------------------------------------------------------------

    private static AuthenticationHeaderValue BasicAuth(string token)
    {
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + token));
        return new AuthenticationHeaderValue("Basic", credentials);
    }

    private static HttpRequestMessage GetJson(string url, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = BasicAuth(token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private HttpRequestMessage PostJson(string url, string token, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = BasicAuth(token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = JsonContent.Create(body, options: JsonOptions);
        return req;
    }

    private async Task<HttpResponseMessage> SendGetAsync(
        HttpClient client, string url, string operation, string token,
        AzureDevOpsUpstreamOptions opts, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            using var req = GetJson(url, token);
            try
            {
                using var timeout = TimeoutScope(ct, opts.HttpTimeoutSeconds);
                var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (IsRetryableStatus(response.StatusCode) && attempt < opts.MaxRetries)
                {
                    response.Dispose();
                    await DelayForRetryAsync(null, attempt, opts, ct);
                    attempt++;
                    continue;
                }
                return response;
            }
            catch (HttpRequestException ex) when (attempt < opts.MaxRetries)
            {
                _host.Logger.LogDebug("Azure DevOps {Operation} attempt {Attempt} failed ({Message}); retrying",
                    operation, attempt + 1, Scrub(ex.Message, token));
                await DelayForRetryAsync(null, attempt, opts, ct);
                attempt++;
            }
            catch (HttpRequestException ex)
            {
                throw Infra(operation, $"unreachable: {Scrub(ex.Message, token)}", ex);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt < opts.MaxRetries)
                {
                    _host.Logger.LogDebug("Azure DevOps {Operation} attempt {Attempt} timed out; retrying",
                        operation, attempt + 1);
                    await DelayForRetryAsync(null, attempt, opts, ct);
                    attempt++;
                    continue;
                }
                throw Infra(operation, $"timed out after {opts.HttpTimeoutSeconds}s");
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage request, string operation, string token,
        AzureDevOpsUpstreamOptions opts, CancellationToken ct)
    {
        // Writes are never retried blindly: CompleteAsync itself is idempotent
        // (create collides to 409-soft, complete is a state transition), so a
        // throw here lets the orchestrator retry the whole step safely.
        try
        {
            using var timeout = TimeoutScope(ct, opts.HttpTimeoutSeconds);
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (HttpRequestException ex)
        {
            throw Infra(operation, $"unreachable: {Scrub(ex.Message, token)}", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw Infra(operation, "timed out", ex);
        }
    }

    private static CancellationTokenSource TimeoutScope(CancellationToken ct, int seconds)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        return timeout;
    }

    private async Task DelayForRetryAsync(
        HttpResponseMessage? response, int attempt, AzureDevOpsUpstreamOptions opts, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(opts.RetryBaseDelayMilliseconds * (1 << Math.Min(attempt, 4)));
        var retryAfter = response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta <= TimeSpan.FromSeconds(30))
            delay = delta > delay ? delta : delay;
        await Task.Delay(delay, ct);
    }

    /// <summary>
    /// Maps forge-side failures to infrastructure: unreachable, unauthorised,
    /// rate-limited or rejecting is never a verdict on the work item's diff.
    /// Callers handle 404/409 themselves before reaching this guard.
    /// </summary>
    private void ThrowIfForgeError(HttpResponseMessage response, string operation, string token)
    {
        if (response.IsSuccessStatusCode)
            return;
        var status = (int)response.StatusCode;
        throw Infra(operation, status switch
        {
            401 => "unauthorised (401 — check the PAT scope and expiry)",
            403 => "forbidden (403 — the PAT lacks permission for this operation)",
            429 => "rate-limited (429)",
            _ when status >= 500 => $"forge error (HTTP {status})",
            _ => $"rejected (HTTP {status})",
        });
    }

    private static InvalidOperationException Infra(string operation, string reason, Exception? inner = null)
        => new($"Azure DevOps {operation} failed (infrastructure): {reason}",
            inner ?? new InvalidOperationException(reason));

    private static bool IsRetryableStatus(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests
            || status == HttpStatusCode.InternalServerError
            || status == HttpStatusCode.BadGateway
            || status == HttpStatusCode.ServiceUnavailable
            || status == HttpStatusCode.GatewayTimeout;

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct);
    }

    private static string? ContinuationToken(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ms-continuationtoken", out var values))
        {
            var token = values.FirstOrDefault();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Config, credentials, URLs, validation
    // ------------------------------------------------------------------

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException(
                "Azure DevOps plugin used before InitializeAsync (host wiring error).");
    }

    private AzureDevOpsUpstreamOptions OptionsFor(Core.ProjectId? projectId)
    {
        var projectConfig = projectId is { } id
            ? _upstreamHost.GetProjectUpstreamConfig(id)
            : new Dictionary<string, string>();
        return AzureDevOpsUpstreamOptions.Bind(_host.ScopedConfig, projectConfig);
    }

    /// <summary>Token comes only from the env var named by the request — never from config files.</summary>
    private static string? ReadToken(UpstreamCompletionRequest request)
        => string.IsNullOrWhiteSpace(request.TokenEnvVar)
            ? null
            : Environment.GetEnvironmentVariable(request.TokenEnvVar);

    private string? ReadScopedToken()
    {
        var name = _host.ScopedConfig["TokenEnvVar"];
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return Environment.GetEnvironmentVariable(name);
    }

    private string RequireScopedToken()
    {
        var token = ReadScopedToken();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "Azure DevOps plugin requires a PAT (ScopedConfig TokenEnvVar names the env var holding it)");
        return token;
    }

    private static string InstanceRoot(AzureDevOpsUpstreamOptions opts)
        => opts.InstanceUrl.TrimEnd('/');

    private static string InstanceApi(AzureDevOpsUpstreamOptions opts)
        => $"{InstanceRoot(opts)}/{Esc(opts.Organization)}/_apis";

    private static string ProjectApi(AzureDevOpsUpstreamOptions opts)
        => $"{InstanceRoot(opts)}/{Esc(opts.Organization)}/{Esc(opts.Project)}/_apis";

    private static string GitUrl(AzureDevOpsUpstreamOptions opts)
        => $"{InstanceRoot(opts)}/{Esc(opts.Organization)}/{Esc(opts.Project)}/_git/{Esc(opts.Repository)}";

    private static string PullRequestUrl(AzureDevOpsUpstreamOptions opts, int prId)
        => $"{InstanceRoot(opts)}/{Esc(opts.Organization)}/{Esc(opts.Project)}/_git/{Esc(opts.Repository)}/pullrequest/{prId}";

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static void ValidateBranch(string branch, string paramName)
    {
        if (!IsValidBranch(branch))
            throw new ArgumentException(
                $"Branch contains invalid characters (whitespace/control chars not allowed): '{SanitizeForLog(branch)}'",
                paramName);
    }

    private static bool IsValidBranch(string? branch)
        => !string.IsNullOrEmpty(branch)
            && !branch.StartsWith('-')
            && !branch.Any(c => char.IsWhiteSpace(c) || (char.IsControl(c) && c != '\t'));

    private static void ValidateSha(string sha)
    {
        if (string.IsNullOrEmpty(sha) || sha.Length is < 4 or > 64 || !sha.All(IsHex))
            throw new ArgumentException("headSha must be a hex commit sha.", nameof(sha));

        static bool IsHex(char c) => char.IsAsciiHexDigit(c);
    }

    private static string StripRefsHeads(string? refName)
    {
        if (string.IsNullOrEmpty(refName))
            return string.Empty;
        const string prefix = "refs/heads/";
        return refName.StartsWith(prefix, StringComparison.Ordinal) ? refName[prefix.Length..] : refName;
    }

    private static string SanitizeForLog(string? value) =>
        value?.Replace("\n", "\\n", StringComparison.Ordinal)
              .Replace("\r", "\\r", StringComparison.Ordinal) ?? "(null)";

    private static string Scrub(string message, string token) =>
        string.IsNullOrEmpty(token) ? message : message.Replace(token, "***", StringComparison.Ordinal);

    private static UpstreamPushReconcileStrategy ToReconcileStrategy(string mergeMethod)
        => mergeMethod.Equals("rebase", StringComparison.OrdinalIgnoreCase)
            ? UpstreamPushReconcileStrategy.Rebase
            : UpstreamPushReconcileStrategy.Merge;

    private static string MapMergeStrategy(string mergeMethod)
        => mergeMethod.ToLowerInvariant() switch
        {
            "merge" => "noFastForward",
            "squash" => "squash",
            "rebase" => "rebase",
            _ => throw new ArgumentException(
                $"MergeMethod '{mergeMethod}' is invalid; valid values: merge, squash, rebase",
                nameof(mergeMethod)),
        };

    private static UpstreamReviewVerdict MapVote(int vote)
        => vote switch
        {
            >= 5 => UpstreamReviewVerdict.Approved,
            -10 => UpstreamReviewVerdict.ChangesRequested,
            _ => UpstreamReviewVerdict.Pending,
        };

    private static UpstreamCheckState MapBuild(string? status, string? result)
    {
        if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
            return UpstreamCheckState.Pending;
        return result?.ToLowerInvariant() switch
        {
            "succeeded" => UpstreamCheckState.Passing,
            "partiallysucceeded" => UpstreamCheckState.Neutral,
            "canceled" => UpstreamCheckState.Cancelled,
            "failed" => UpstreamCheckState.Failing,
            _ => UpstreamCheckState.Pending,
        };
    }

    private static UpstreamComment MapThreadComment(AdoThread? thread, AdoComment comment, int? threadId = null)
    {
        var id = threadId ?? thread?.Id ?? 0;
        return new UpstreamComment
        {
            Id = $"{id}/{comment.Id}",
            Author = !string.IsNullOrWhiteSpace(comment.Author?.DisplayName)
                ? comment.Author.DisplayName!
                : !string.IsNullOrWhiteSpace(comment.Author?.UniqueName)
                    ? comment.Author.UniqueName! : "unknown",
            Body = comment.Content ?? string.Empty,
            FilePath = thread?.ThreadContext?.FilePath?.TrimStart('/'),
            Line = thread?.ThreadContext?.RightFileStart is { Line: > 0 } start ? start.Line : null,
            CreatedAt = comment.PublishedDate,
        };
    }

    private static int ParseThreadId(string replyToId)
    {
        var head = replyToId.Split('/')[0];
        if (int.TryParse(head, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
            return id;
        throw new ArgumentException($"ReplyToId '{replyToId}' does not name an Azure DevOps thread.", nameof(replyToId));
    }

    private static bool PolicyAppliesToRepo(AdoPolicyConfiguration policy, string? repoId, string repoName)
    {
        var scopes = policy.Settings?.Scope ?? [];
        if (scopes.Length == 0)
            return true;
        return scopes.Any(s => ScopeMatchesRepo(s.RepositoryId, repoId, repoName));
    }

    private static bool ScopeMatchesRepo(string? scopeRepoId, string? repoId, string repoName)
    {
        if (string.IsNullOrEmpty(scopeRepoId))
            return true;
        if (!string.IsNullOrEmpty(repoId)
            && scopeRepoId.Equals(repoId, StringComparison.OrdinalIgnoreCase))
            return true;
        return scopeRepoId.Equals(repoName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workdir, CancellationToken ct,
        IReadOnlyDictionary<string, string> extraEnv,
        params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        foreach (var (k, v) in extraEnv)
            psi.EnvironmentVariables[k] = v;

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout, stderr);
    }
}

internal static class AzureDevOpsUpstreamOptionExtensions
{
    internal static bool HasCoordinates(this AzureDevOpsUpstreamOptions opts)
        => !string.IsNullOrWhiteSpace(opts.Organization)
            && !string.IsNullOrWhiteSpace(opts.Project)
            && !string.IsNullOrWhiteSpace(opts.Repository);
}

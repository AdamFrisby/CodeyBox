using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GiteaUpstreamPlugin;

/// <summary>
/// Upstream remote plugin for Gitea (and API-compatible forges), implementing
/// the full <see cref="IUpstreamRemote"/> contract against Gitea API v1.
///
/// <para>Core lifecycle: pushes the work branch via the host git module, opens
/// a pull request (<c>POST /repos/{owner}/{repo}/pulls</c>), optionally
/// auto-merges it, merges upstream branches host-side (Gitea exposes no
/// branch-to-branch merge API), fetches the base branch, and lists/reads PRs.
/// Extended surfaces map Gitea's native concepts without translation into
/// GitHub's shape: pull reviews, commit statuses, issue comments, the four
/// webhook scopes (repository, organisation, user, system), repository plus
/// branch-protection metadata, and releases.</para>
///
/// <para>Configuration: per-project <c>Upstream.PluginConfig</c> entries
/// (<c>BaseUrl</c>, <c>Owner</c>, <c>Repository</c>) override the plugin-scoped
/// defaults (<c>CodeyBox:Plugins:codeybox.gitea-upstream</c>) inside
/// <c>CompleteAsync</c>, which is the only member that carries a project id.
/// Every other member resolves the scoped defaults. Operators should set both
/// consistently; multi-repo deployments are a known contract limitation (see
/// README). Credentials never come from configuration: only the <em>name</em>
/// of the env var holding the token is configured, and the value is read from
/// the process environment at call time — the sandbox never sees it because
/// the token only ever flows into host-side git env and per-request HTTP
/// headers.</para>
///
/// <para>Failure classification: forge-side failures (unreachable, 401/403,
/// 429, 5xx, unexpected bodies) throw <see cref="GiteaUpstreamException"/> so
/// the orchestrator retries as infrastructure — never a verdict on the diff.
/// Soft outcomes (PR already exists, merge blocked) return partial results.
/// Unsupported capabilities return <c>null</c> (or empty when supported and
/// empty) and never fail a work item.</para>
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.gitea-upstream",
    displayName: "Gitea Upstream Remote",
    minHostApiVersion: "1.0")]
public sealed class GiteaUpstreamRemote : IUpstreamRemote, IPluginInitializer
{
    public string Name => "gitea";

    private const int MaxPages = 10;
    private const int PageSize = 50;
    private const int MaxErrorBodyChars = 2048;
    private const int MaxRateLimitRetries = 3;
    private const int MaxRateLimitDelaySeconds = 30;

    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;

    private IPluginHost _host = null!;
    private IUpstreamPluginHost _upstreamHost = null!;
    private Microsoft.Extensions.Configuration.IConfigurationSection _scopedConfig = null!;

    public GiteaUpstreamRemote(IGitHost gitHost, IHttpClientFactory httpClientFactory)
    {
        _gitHost = gitHost;
        _httpClientFactory = httpClientFactory;
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct = default)
    {
        _host = context.Host;
        _scopedConfig = context.ScopedConfig;
        if (context.Host is IUpstreamPluginHost upstreamHost)
        {
            _upstreamHost = upstreamHost;
        }
        else
        {
            throw new InvalidOperationException(
                "Gitea upstream plugin requires a host implementing IUpstreamPluginHost.");
        }

        context.Logger.LogInformation("GiteaUpstreamRemote initialized");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Core lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Pushes <paramref name="branch"/> to the scoped Gitea repo via the host
    /// git module. Returns a failure result (rather than throwing) on
    /// transport errors, matching the <c>git-generic</c> push contract.
    /// </summary>
    public async Task<UpstreamPushResult> PushAsync(
        string repositoryId, string branch, CancellationToken ct = default)
    {
        GiteaEndpoint endpoint;
        try
        {
            endpoint = ResolveScoped("PushAsync");
            GuardBranch(branch, nameof(branch));
        }
        catch (Exception ex)
        {
            return new UpstreamPushResult(false, ex.Message);
        }

        try
        {
            await _gitHost.PushToUpstreamAsync(
                repositoryId,
                endpoint.GitRemoteUrl,
                branch,
                BuildAuthEnv(endpoint.Token),
                UpstreamPushReconcileStrategy.Rebase,
                ct);
            return new UpstreamPushResult(true, null);
        }
        catch (Exception ex)
        {
            return new UpstreamPushResult(false, Scrub(ex.Message, endpoint.Token));
        }
    }

    /// <summary>
    /// Full completion flow: push the work branch, open a PR (Gitea answers
    /// 422 — and 409 carrying an "already exists" body — when the PR exists,
    /// which is a soft partial outcome, not an error), then auto-merge when
    /// requested. Honors <c>ExistingPullRequestNumber</c> for the
    /// orchestrator's race-recovery path by skipping creation. Transient
    /// forge failures throw <see cref="GiteaUpstreamException"/> for retry.
    /// </summary>
    public async Task<UpstreamCompletionOutcome> CompleteAsync(
        UpstreamCompletionRequest request, CancellationToken ct = default)
    {
        GuardBranch(request.WorkBranch, nameof(request.WorkBranch));
        GuardBranch(request.BaseBranch, nameof(request.BaseBranch));

        var endpoint = ResolveForRequest(request);

        await PushBranchAsync(
            request.RepositoryId, endpoint, request.WorkBranch,
            ToReconcileStrategy(request.MergeMethod), ct);

        int prNumber;
        string? prUrl;
        if (request.ExistingPullRequestNumber is { } existing)
        {
            var pr = await GetPullRequestDetailAsync(endpoint, existing, ct)
                ?? throw new GiteaUpstreamException(
                    $"Gitea PR #{existing} from a prior attempt is no longer available.");
            prNumber = existing;
            prUrl = pr.HtmlUrl ?? WebPullUrl(endpoint, existing);
        }
        else
        {
            var created = await OpenPullRequestAsync(endpoint, request, ct);
            if (created is null)
            {
                return new UpstreamCompletionOutcome
                {
                    BranchPushed = true,
                    Notes = "PR creation skipped (already exists — branch may already have an open PR)",
                };
            }

            prNumber = ToInt32(created.Number, "PR number");
            prUrl = created.HtmlUrl ?? WebPullUrl(endpoint, prNumber);
            _host.Logger.LogInformation("Gitea PR #{Number} opened: {Url}", prNumber, prUrl);
        }

        if (!request.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = prUrl,
                PullRequestNumber = prNumber,
            };
        }

        var (mergedSha, notes, raced) = await MergePullRequestAsync(
            endpoint, prNumber, request.MergeMethod, ct);
        if (mergedSha is not null)
            _host.Logger.LogInformation("Gitea PR #{Number} auto-merged: {Sha}", prNumber, mergedSha);

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = prUrl,
            PullRequestNumber = prNumber,
            MergedSha = mergedSha,
            Notes = notes,
            AutoMergeRaced = raced,
        };
    }

    /// <summary>
    /// Gitea has no branch-to-branch merge API, so this merges host-side:
    /// clone the target branch, fetch the source, <c>git merge</c>, push back.
    /// Returns <c>false</c> on merge conflicts (leaving the forge untouched);
    /// throws <see cref="GiteaUpstreamException"/> on transport/auth failures.
    /// </summary>
    public async Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        GuardBranch(targetBranch, nameof(targetBranch));
        GuardBranch(sourceBranch, nameof(sourceBranch));
        var endpoint = ResolveScoped("TryMergeUpstreamBranchAsync");

        var tmpDir = Path.Combine(Path.GetTempPath(), "codeybox-gitea-sync-" + Guid.NewGuid().ToString("N")[..8]);
        var env = BuildAuthEnv(endpoint.Token);
        try
        {
            Directory.CreateDirectory(tmpDir);
            var clone = await RunGitAsync(tmpDir, env, ct,
                "clone", "--branch", targetBranch, "--single-branch", "--", endpoint.GitRemoteUrl, tmpDir);
            if (clone.ExitCode != 0)
                throw new GiteaUpstreamException(
                    $"git clone of '{targetBranch}' failed: {Scrub(clone.Stderr, endpoint.Token)}");

            var fetch = await RunGitAsync(tmpDir, env, ct, "fetch", "origin", sourceBranch);
            if (fetch.ExitCode != 0)
                throw new GiteaUpstreamException(
                    $"git fetch of '{sourceBranch}' failed: {Scrub(fetch.Stderr, endpoint.Token)}");

            var merge = await RunGitAsync(tmpDir, env, ct, "merge", "FETCH_HEAD", "--no-edit", "--no-ff");
            if (merge.ExitCode != 0)
            {
                await RunGitAsync(tmpDir, env, ct, "merge", "--abort");
                return false;
            }

            var push = await RunGitAsync(tmpDir, env, ct, "push", "origin", targetBranch);
            if (push.ExitCode != 0)
                throw new GiteaUpstreamException(
                    $"git push of '{targetBranch}' failed: {Scrub(push.Stderr, endpoint.Token)}");

            return true;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tmpDir))
                    Directory.Delete(tmpDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup — never mask the merge outcome.
            }
        }
    }

    /// <summary>
    /// Fetches the current head of <paramref name="baseBranch"/> from the
    /// scoped Gitea repo into the host bare repo and returns the new sha
    /// (<c>null</c> when the forge does not advertise the branch).
    /// </summary>
    public async Task<string?> FetchBaseBranchAsync(
        string repositoryId, string baseBranch, CancellationToken ct = default)
    {
        GuardBranch(baseBranch, nameof(baseBranch));
        var endpoint = ResolveScoped("FetchBaseBranchAsync");
        try
        {
            return await _gitHost.FetchUpstreamBranchAsync(
                repositoryId, endpoint.GitRemoteUrl, baseBranch, BuildAuthEnv(endpoint.Token), ct);
        }
        catch (Exception ex)
        {
            throw new GiteaUpstreamException(
                $"Failed to fetch base branch '{SanitizeForLog(baseBranch)}': {Scrub(ex.Message, endpoint.Token)}",
                ex);
        }
    }

    /// <summary>
    /// Creates a release named <paramref name="tagName"/> at
    /// <paramref name="sha"/> via the Gitea releases API (Gitea creates the
    /// tag object when it does not yet exist). Returns the release URL, or
    /// <c>null</c> when the tag already has a release (409/422) — unsupported
    /// is non-fatal by design.
    /// </summary>
    public async Task<string?> CreateTagAndReleaseAsync(
        string tagName, string sha, string? releaseNotes, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tagName))
            throw new ArgumentException("Tag name must not be empty.", nameof(tagName));
        if (string.IsNullOrWhiteSpace(sha))
            throw new ArgumentException("Commit sha must not be empty.", nameof(sha));
        var endpoint = ResolveScoped("CreateTagAndReleaseAsync");

        using var response = await SendAsync(endpoint, HttpMethod.Post, "releases", ct,
            () => JsonContent.Create(new
            {
                tag_name = tagName,
                target_commitish = sha,
                name = tagName,
                body = releaseNotes ?? string.Empty,
            }));
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
        {
            _host.Logger.LogWarning(
                "Gitea release for tag {Tag} already exists ({Status}); continuing without a release",
                SanitizeForLog(tagName), (int)response.StatusCode);
            return null;
        }

        await ThrowIfNotSuccessAsync(endpoint, response, "POST releases", ct);
        var release = await response.Content.ReadFromJsonAsync<GiteaRelease>(cancellationToken: ct);
        _host.Logger.LogInformation("Gitea release created for tag {Tag}: {Url}", tagName, release?.HtmlUrl);
        return release?.HtmlUrl;
    }

    /// <summary>
    /// Lists open PRs whose head branch starts with
    /// <paramref name="branchPrefix"/> and whose mergeability Gitea reported.
    /// Pages through <c>GET /pulls?state=open</c>; hitting the page cap throws
    /// rather than returning a partial list that reads as complete.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(
        string branchPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(branchPrefix))
            throw new ArgumentException("branchPrefix must be non-empty", nameof(branchPrefix));
        var endpoint = ResolveScoped("ListOpenPullRequestsAsync");

        var listed = new List<UpstreamPullRequest>();
        var summaries = await GetAllPagesAsync<GiteaPullRequest>(endpoint, "pulls?state=open", ct);
        foreach (var summary in summaries)
        {
            var headRef = summary.Head?.Ref;
            if (string.IsNullOrEmpty(headRef))
                continue;
            if (!headRef.StartsWith(branchPrefix, StringComparison.Ordinal))
                continue;

            var detail = await GetPullRequestDetailAsync(endpoint, ToInt32(summary.Number, "PR number"), ct);
            if (detail is null)
                continue;

            listed.Add(new UpstreamPullRequest
            {
                Number = ToInt32(detail.Number, "PR number"),
                Url = detail.HtmlUrl ?? WebPullUrl(endpoint, ToInt32(detail.Number, "PR number")),
                HeadBranch = headRef,
                HeadSha = summary.Head?.Sha ?? string.Empty,
                BaseBranch = summary.Base?.Ref ?? string.Empty,
                HasMergeConflict = detail.Mergeable == false,
            });
        }

        return listed;
    }

    /// <summary>
    /// Reads one PR by index. Returns <c>null</c> when the PR does not exist
    /// (404); throws <see cref="GiteaUpstreamException"/> on forge failures.
    /// </summary>
    public async Task<UpstreamPullRequestState?> GetPullRequestAsync(
        int number, CancellationToken ct = default)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var endpoint = ResolveScoped("GetPullRequestAsync");

        var detail = await GetPullRequestDetailAsync(endpoint, number, ct);
        if (detail is null)
            return null;

        var status = detail.Merged
            ? PullRequestStatus.Merged
            : string.Equals(detail.State, "closed", StringComparison.OrdinalIgnoreCase)
                ? PullRequestStatus.Closed
                : PullRequestStatus.Open;
        return new UpstreamPullRequestState(
            (int)detail.Number,
            detail.HtmlUrl ?? WebPullUrl(endpoint, number),
            status,
            detail.Merged ? detail.MergeCommitSha : null);
    }

    // ------------------------------------------------------------------
    // Extended surfaces: reviews, checks, comments, webhooks, metadata
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads Gitea pull reviews (<c>GET /pulls/{index}/reviews</c>), mapping
    /// <c>APPROVED</c>/<c>REQUEST_CHANGES</c>/<c>COMMENT</c>/<c>PENDING</c>
    /// (plus <c>REQUEST_REVIEW</c> as pending); a dismissed review reports
    /// <see cref="UpstreamReviewVerdict.Dismissed"/> and stale approvals do
    /// not count toward the quorum. The quorum comes from the branch
    /// protection matching the PR's base branch (<c>required_approvals</c>);
    /// with no rule, zero approvals are required and any non-dismissed change
    /// request blocks. Returns <c>null</c> when the PR does not exist.
    /// </summary>
    public async Task<UpstreamReviewState?> GetReviewStateAsync(
        int number, CancellationToken ct = default)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var endpoint = ResolveScoped("GetReviewStateAsync");

        var detail = await GetPullRequestDetailAsync(endpoint, number, ct);
        if (detail is null)
            return null;

        var reviews = new List<UpstreamReview>();
        var approvals = 0;
        var blockingChanges = false;
        var wireReviews = await GetAllPagesAsync<GiteaPullReview>(
            endpoint, $"pulls/{number}/reviews", ct);
        foreach (var review in wireReviews)
        {
            var reviewer = review.User?.Login;
            if (string.IsNullOrWhiteSpace(reviewer))
                continue;
            var verdict = MapReviewVerdict(review.State, review.Dismissed);
            reviews.Add(new UpstreamReview
            {
                Reviewer = reviewer,
                Verdict = verdict,
                SubmittedAt = review.SubmittedAt,
            });
            // Dismissed reviews no longer speak; stale approvals were given
            // against an older head and do not satisfy the quorum.
            if (review.Dismissed)
                continue;
            if (verdict == UpstreamReviewVerdict.ChangesRequested)
                blockingChanges = true;
            else if (verdict == UpstreamReviewVerdict.Approved && !review.Stale)
                approvals++;
        }

        var requiredApprovals = await GetRequiredApprovalsAsync(
            endpoint, detail.Base?.Ref, ct);

        return new UpstreamReviewState
        {
            Reviews = reviews,
            RequiredReviewers = [],
            RequiredApprovalCount = requiredApprovals,
            RequirementsMet = !blockingChanges && approvals >= requiredApprovals,
        };
    }

    /// <summary>
    /// Reads Gitea commit statuses for <paramref name="headSha"/> via the
    /// combined status endpoint (<c>GET /commits/{sha}/status</c>), mapping
    /// <c>success</c> to passing, <c>error</c>/<c>failure</c> to failing,
    /// <c>pending</c> to pending and <c>warning</c> to neutral. An empty
    /// status list with <c>RequiredChecksPassed = true</c> means Gitea
    /// requires nothing for this ref. Returns <c>null</c> when the sha is
    /// unknown to the forge (404).
    /// </summary>
    public async Task<UpstreamCheckSummary?> GetCheckResultsAsync(
        string headSha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(headSha))
            throw new ArgumentException("headSha must not be empty.", nameof(headSha));
        if (headSha.Length > 256)
            throw new ArgumentException("headSha is too long.", nameof(headSha));
        var endpoint = ResolveScoped("GetCheckResultsAsync");

        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"commits/{Uri.EscapeDataString(headSha)}/status", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(endpoint, response, "GET commits/{sha}/status", ct);

        var combined = await response.Content.ReadFromJsonAsync<GiteaCombinedStatus>(cancellationToken: ct);
        var checks = (combined?.Statuses ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Context))
            .Select(s => new UpstreamCheckResult
            {
                Name = s.Context!,
                State = MapCheckState(s.Status),
                DetailsUrl = string.IsNullOrWhiteSpace(s.TargetUrl) ? null : s.TargetUrl,
                Description = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description,
            })
            .ToList();

        return new UpstreamCheckSummary
        {
            Checks = checks,
            RequiredChecksPassed = checks.All(c =>
                c.State is UpstreamCheckState.Passing or UpstreamCheckState.Neutral),
        };
    }

    /// <summary>
    /// Lists plain discussion comments on the PR via the issues API
    /// (<c>GET /issues/{index}/comments</c>; in Gitea a PR shares its index
    /// with the backing issue). Code-anchored review threads are a different
    /// native kind and are intentionally not merged into this listing.
    /// Returns <c>null</c> when the PR does not exist, and an empty list when
    /// it exists with no comments.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamComment>?> ListCommentsAsync(
        int number, CancellationToken ct = default)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var endpoint = ResolveScoped("ListCommentsAsync");

        List<GiteaIssueComment> wireComments;
        try
        {
            wireComments = await GetAllPagesAsync<GiteaIssueComment>(
                endpoint, $"issues/{number}/comments", ct);
        }
        catch (GiteaUpstreamException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return wireComments.Select(comment => new UpstreamComment
        {
            Id = comment.Id.ToString(CultureInfo.InvariantCulture),
            Author = string.IsNullOrWhiteSpace(comment.User?.Login) ? "unknown" : comment.User!.Login!,
            Body = comment.Body ?? string.Empty,
            CreatedAt = comment.CreatedAt,
        }).ToList();
    }

    /// <summary>
    /// Posts a plain top-level comment via the issues API
    /// (<c>POST /issues/{index}/comments</c>). File-anchored threads and
    /// thread replies are different native kinds (they need a review id in
    /// Gitea's model) and return <c>null</c> rather than a mislabelled post.
    /// Returns <c>null</c> when the PR does not exist.
    /// </summary>
    public async Task<UpstreamComment?> PostCommentAsync(
        int number, NewUpstreamComment comment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comment);
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        if (comment.FilePath is not null || comment.ReplyToId is not null)
            return null;
        var endpoint = ResolveScoped("PostCommentAsync");

        using var response = await SendAsync(endpoint, HttpMethod.Post, $"issues/{number}/comments", ct,
            () => JsonContent.Create(new { body = comment.Body }));
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(endpoint, response, "POST issues/{index}/comments", ct);

        var created = await response.Content.ReadFromJsonAsync<GiteaIssueComment>(cancellationToken: ct);
        if (created is null)
            throw new GiteaUpstreamException("Gitea returned an empty comment body.");
        return new UpstreamComment
        {
            Id = created.Id.ToString(CultureInfo.InvariantCulture),
            Author = string.IsNullOrWhiteSpace(created.User?.Login) ? "unknown" : created.User!.Login!,
            Body = created.Body ?? comment.Body,
            CreatedAt = created.CreatedAt,
        };
    }

    /// <summary>
    /// Lists repository-scoped webhook subscriptions
    /// (<c>GET /repos/{owner}/{repo}/hooks</c>). Use
    /// <see cref="ListScopedWebhooksAsync"/> for the organisation, user and
    /// system scopes. Event names are Gitea-native and mapped verbatim.
    /// Returns <c>null</c> when the forge reports the target unavailable
    /// (404); an empty list means subscriptions are supported and none exist.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(
        CancellationToken ct = default)
        => await ListScopedWebhooksAsync(UpstreamWebhookScopes.Repository, ct);

    /// <summary>
    /// Lists subscriptions at an explicit scope: <c>repository</c>,
    /// <c>organization</c> (or Gitea's own spelling <c>organisation</c>),
    /// <c>user</c>, <c>system</c> — the four scopes Gitea's API genuinely
    /// exposes. Unknown scopes return <c>null</c> rather than a coerced
    /// listing.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListScopedWebhooksAsync(
        string scope, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must not be empty.", nameof(scope));
        var endpoint = ResolveScoped("ListWebhookSubscriptionsAsync");
        var path = WebhookPath(endpoint, scope);
        if (path is null)
            return null;

        List<GiteaHook> hooks;
        try
        {
            hooks = await GetAllPagesAsync<GiteaHook>(endpoint, path, ct);
        }
        catch (GiteaUpstreamException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _host.Logger.LogWarning("Gitea webhook list at scope {Scope} is unavailable (404)", scope);
            return null;
        }

        return hooks.Select(h => MapHook(h, scope)).ToList();
    }

    /// <summary>
    /// Creates a webhook subscription at the scope carried by
    /// <see cref="NewUpstreamWebhookSubscription.Scope"/> (repository,
    /// organization/organisation, user, system — see
    /// <see cref="CreateScopedWebhookAsync"/>). Event names pass through
    /// untouched in Gitea's vocabulary. No delivery secret is set — the
    /// contract carries no secret input — so operators should front the
    /// target accordingly. Returns <c>null</c> when the scope is unknown or
    /// the forge reports the target unavailable (404).
    /// </summary>
    public async Task<UpstreamWebhookSubscription?> CreateWebhookSubscriptionAsync(
        NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return await CreateScopedWebhookAsync(subscription.Scope, subscription, ct);
    }

    /// <summary>
    /// Creates a subscription at an explicit scope (repository, organization/
    /// organisation, user, system). Unknown scopes return <c>null</c> rather
    /// than a coerced subscription.
    /// </summary>
    public async Task<UpstreamWebhookSubscription?> CreateScopedWebhookAsync(
        string scope, NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must not be empty.", nameof(scope));
        var endpoint = ResolveScoped("CreateWebhookSubscriptionAsync");
        var path = WebhookPath(endpoint, scope);
        if (path is null)
            return null;

        using var response = await SendAsync(endpoint, HttpMethod.Post, path, ct,
            () => JsonContent.Create(new
            {
                type = "gitea",
                active = true,
                events = subscription.Events.ToArray(),
                config = new Dictionary<string, string>
                {
                    ["url"] = subscription.TargetUrl,
                    ["content_type"] = "json",
                },
            }));
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            _host.Logger.LogWarning("Gitea webhook create at scope {Scope} is unavailable (404)", scope);
            return null;
        }

        await ThrowIfNotSuccessAsync(endpoint, response, "POST webhooks", ct);
        var hook = await response.Content.ReadFromJsonAsync<GiteaHook>(cancellationToken: ct);
        if (hook is null)
            throw new GiteaUpstreamException("Gitea returned an empty webhook body.");
        return MapHook(hook, scope);
    }

    /// <summary>
    /// Removes a repository-scoped subscription by forge-assigned id.
    /// Returns <c>null</c> when the scope is unsupported here,
    /// <c>true</c> when removed, <c>false</c> when the id is unknown (404) or
    /// not a numeric Gitea hook id.
    /// </summary>
    public async Task<bool?> DeleteWebhookSubscriptionAsync(
        string id, CancellationToken ct = default)
        => await DeleteScopedWebhookAsync(UpstreamWebhookScopes.Repository, id, ct);

    /// <summary>
    /// Removes a subscription at an explicit scope. Unknown scopes return
    /// <c>null</c>; unknown ids return <c>false</c>.
    /// </summary>
    public async Task<bool?> DeleteScopedWebhookAsync(
        string scope, string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scope))
            throw new ArgumentException("Scope must not be empty.", nameof(scope));
        if (string.IsNullOrWhiteSpace(id))
            return false;
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return false;
        var endpoint = ResolveScoped("DeleteWebhookSubscriptionAsync");
        var path = WebhookPath(endpoint, scope);
        if (path is null)
            return null;

        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"{path}/{Uri.EscapeDataString(id)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await ThrowIfNotSuccessAsync(endpoint, response, "DELETE webhooks/{id}", ct);
        return true;
    }

    /// <summary>
    /// Reads repository metadata Gitea genuinely reports: default branch,
    /// visibility (<c>internal</c> passes through Gitea's own level;
    /// otherwise <c>private</c>/<c>public</c>) and branch protection rules
    /// (rule name, required approvals, status-check requirement). Returns
    /// <c>null</c> when the repo is unavailable (404).
    /// </summary>
    public async Task<UpstreamRepositoryMetadata?> GetRepositoryMetadataAsync(
        CancellationToken ct = default)
    {
        var endpoint = ResolveScoped("GetRepositoryMetadataAsync");

        using var response = await SendAsync(endpoint, HttpMethod.Get, string.Empty, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(endpoint, response, "GET repos/{owner}/{repo}", ct);

        var repo = await response.Content.ReadFromJsonAsync<GiteaRepository>(cancellationToken: ct);
        if (repo is null)
            throw new GiteaUpstreamException("Gitea returned an empty repository body.");

        var protections = new List<UpstreamBranchProtection>();
        try
        {
            var rules = await GetAllPagesAsync<GiteaBranchProtection>(
                endpoint, "branch_protections", ct);
            protections.AddRange(rules
                .Where(r => !string.IsNullOrWhiteSpace(r.RuleName))
                .Select(r => new UpstreamBranchProtection(
                    r.RuleName!,
                    (int)Math.Min(r.RequiredApprovals, int.MaxValue),
                    r.EnableStatusCheck)));
        }
        catch (GiteaUpstreamException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _host.Logger.LogDebug("Gitea branch protections unavailable (404); reporting repo without rules");
        }

        return new UpstreamRepositoryMetadata
        {
            DefaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? null : repo.DefaultBranch,
            Visibility = repo.Internal
                ? UpstreamRepositoryVisibility.Internal
                : repo.Private
                    ? UpstreamRepositoryVisibility.Private
                    : UpstreamRepositoryVisibility.Public,
            BranchProtections = protections,
        };
    }

    // ------------------------------------------------------------------
    // Config + HTTP plumbing (every guard sits adjacent to its sink)
    // ------------------------------------------------------------------

    private sealed record GiteaEndpoint(
        string ApiBase, string Owner, string Repo, string? Token)
    {
        public string RepoPath =>
            $"repos/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repo)}";

        public string GitRemoteUrl
        {
            get
            {
                var idx = ApiBase.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
                var webBase = idx >= 0 ? ApiBase[..idx] : ApiBase;
                return $"{webBase.TrimEnd('/')}/{Uri.EscapeDataString(Owner)}/{Uri.EscapeDataString(Repo)}.git";
            }
        }
    }

    private GiteaEndpoint ResolveScoped(string context)
    {
        var opts = GiteaUpstreamOptions.FromScopedConfig(_scopedConfig);
        return Materialize(opts, opts.TokenEnvVar, context);
    }

    private GiteaEndpoint ResolveForRequest(UpstreamCompletionRequest request)
    {
        var pluginConfig = _upstreamHost.GetProjectUpstreamConfig(request.ProjectId);
        var opts = GiteaUpstreamOptions.FromScopedConfig(_scopedConfig)
            .WithProjectOverrides(pluginConfig);
        var tokenEnvVar = string.IsNullOrWhiteSpace(request.TokenEnvVar)
            ? opts.TokenEnvVar
            : request.TokenEnvVar;
        return Materialize(opts, tokenEnvVar, $"Project {request.ProjectId}");
    }

    private static GiteaEndpoint Materialize(
        GiteaUpstreamOptions opts, string? tokenEnvVar, string context)
    {
        var apiBase = opts.Validate(context);
        return new GiteaEndpoint(apiBase, opts.Owner, opts.Repository, ResolveToken(tokenEnvVar, context));
    }

    private static string? ResolveToken(string? envVarName, string context)
    {
        if (string.IsNullOrWhiteSpace(envVarName))
            return null;
        var token = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrEmpty(token))
            throw new GiteaUpstreamException(
                $"{context}: env var '{envVarName}' is empty (set it to the Gitea token)");
        return token;
    }

    private static IReadOnlyDictionary<string, string> BuildAuthEnv(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return new Dictionary<string, string>();
        return new Dictionary<string, string>
        {
            ["GIT_USERNAME"] = "git",
            ["GIT_PASSWORD"] = token,
        };
    }

    private static void GuardBranch(string branch, string paramName)
    {
        if (string.IsNullOrEmpty(branch))
            throw new ArgumentException("Branch name must not be empty.", paramName);
        if (branch.StartsWith('-'))
            throw new ArgumentException("Branch name must not start with '-'.", paramName);
        if (branch.Any(c => char.IsWhiteSpace(c) || (char.IsControl(c) && c != '\t')))
            throw new ArgumentException(
                $"Branch name contains invalid characters (whitespace/control chars not allowed): '{SanitizeForLog(branch)}'",
                paramName);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return new string(value.Where(c => !char.IsControl(c)).ToArray());
    }

    private static string Scrub(string message, string? token)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(message))
            return message;
        return message.Replace(token, "***", StringComparison.Ordinal);
    }

    private static int ToInt32(long value, string what)
    {
        if (value < 0 || value > int.MaxValue)
            throw new GiteaUpstreamException($"Gitea {what} {value} is out of range.");
        return (int)value;
    }

    private static UpstreamPushReconcileStrategy ToReconcileStrategy(string mergeMethod)
        => mergeMethod.Equals("rebase", StringComparison.OrdinalIgnoreCase)
            ? UpstreamPushReconcileStrategy.Rebase
            : UpstreamPushReconcileStrategy.Merge;

    private static string WebPullUrl(GiteaEndpoint endpoint, int number)
    {
        var idx = endpoint.ApiBase.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
        var webBase = (idx >= 0 ? endpoint.ApiBase[..idx] : endpoint.ApiBase).TrimEnd('/');
        return $"{webBase}/{endpoint.Owner}/{endpoint.Repo}/pulls/{number}";
    }

    private async Task PushBranchAsync(
        string repositoryId, GiteaEndpoint endpoint, string branch,
        UpstreamPushReconcileStrategy strategy, CancellationToken ct)
    {
        try
        {
            await _gitHost.PushToUpstreamAsync(
                repositoryId, endpoint.GitRemoteUrl, branch, BuildAuthEnv(endpoint.Token), strategy, ct);
        }
        catch (Exception ex)
        {
            throw new GiteaUpstreamException(
                $"Failed to push work branch '{SanitizeForLog(branch)}': {Scrub(ex.Message, endpoint.Token)}",
                ex);
        }
    }

    private async Task<GiteaPullRequest?> OpenPullRequestAsync(
        GiteaEndpoint endpoint, UpstreamCompletionRequest request, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Post, "pulls", ct,
            () => JsonContent.Create(new
            {
                title = request.Title,
                body = request.Description ?? string.Empty,
                head = request.WorkBranch,
                @base = request.BaseBranch,
            }));
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            _host.Logger.LogWarning(
                "Gitea: PR already exists for branch '{Branch}' (422); treating as soft-failure",
                SanitizeForLog(request.WorkBranch));
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var detail = await ReadErrorBodyAsync(response, ct);
            if (detail.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                _host.Logger.LogWarning(
                    "Gitea: PR already exists for branch '{Branch}' (409); treating as soft-failure",
                    SanitizeForLog(request.WorkBranch));
                return null;
            }

            throw new GiteaUpstreamException(
                $"Gitea rejected PR creation (409): {Scrub(detail, endpoint.Token)}",
                HttpStatusCode.Conflict);
        }

        await ThrowIfNotSuccessAsync(endpoint, response, "POST pulls", ct);
        var created = await response.Content.ReadFromJsonAsync<GiteaPullRequest>(cancellationToken: ct);
        if (created is null)
            throw new GiteaUpstreamException("Gitea returned an empty pull request body.");
        return created;
    }

    private async Task<(string? Sha, string? Notes, bool Raced)> MergePullRequestAsync(
        GiteaEndpoint endpoint, int prNumber, string mergeMethod, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Post, $"pulls/{prNumber}/merge", ct,
            () => JsonContent.Create(new Dictionary<string, string>
            {
                // Gitea's documented option name is "Do" (the Go field); the
                // default Web JSON policy would camelCase an anonymous member.
                ["Do"] = MapMergeAction(mergeMethod),
            }));

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
        {
            _host.Logger.LogWarning(
                "Gitea POST /pulls/{N}/merge returned 405; leaving PR open for race recovery",
                prNumber);
            return (null, "Auto-merge raced upstream base motion (405); PR left open", true);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            _host.Logger.LogWarning(
                "Gitea POST /pulls/{N}/merge returned 409 (not mergeable); leaving PR open",
                prNumber);
            return (null, "Auto-merge blocked (PR not mergeable); PR left open", false);
        }

        if ((int)response.StatusCode == 423)
        {
            _host.Logger.LogWarning(
                "Gitea POST /pulls/{N}/merge returned 423 (repository archived); leaving PR open",
                prNumber);
            return (null, "Auto-merge blocked (repository archived); PR left open", false);
        }

        await ThrowIfNotSuccessAsync(endpoint, response, "POST pulls/{index}/merge", ct);

        var merged = await GetPullRequestDetailAsync(endpoint, prNumber, ct);
        return (merged?.MergeCommitSha, null, false);
    }

    private static string MapMergeAction(string mergeMethod)
        => mergeMethod.Equals("squash", StringComparison.OrdinalIgnoreCase) ? "squash"
            : mergeMethod.Equals("rebase", StringComparison.OrdinalIgnoreCase) ? "rebase"
            : "merge";

    private async Task<GiteaPullRequest?> GetPullRequestDetailAsync(
        GiteaEndpoint endpoint, int number, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"pulls/{number}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(endpoint, response, "GET pulls/{index}", ct);
        return await response.Content.ReadFromJsonAsync<GiteaPullRequest>(cancellationToken: ct);
    }

    private async Task<int> GetRequiredApprovalsAsync(
        GiteaEndpoint endpoint, string? baseBranch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseBranch))
            return 0;
        using var response = await SendAsync(
            endpoint, HttpMethod.Get,
            $"branch_protections/{Uri.EscapeDataString(baseBranch)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return 0;
        await ThrowIfNotSuccessAsync(endpoint, response, "GET branch_protections/{name}", ct);
        var protection = await response.Content.ReadFromJsonAsync<GiteaBranchProtection>(cancellationToken: ct);
        if (protection is null)
            return 0;
        return (int)Math.Min(protection.RequiredApprovals, int.MaxValue);
    }

    private static UpstreamReviewVerdict MapReviewVerdict(string? state, bool dismissed)
    {
        if (dismissed)
            return UpstreamReviewVerdict.Dismissed;
        return state?.ToUpperInvariant() switch
        {
            "APPROVED" => UpstreamReviewVerdict.Approved,
            "REQUEST_CHANGES" or "CHANGES_REQUESTED" => UpstreamReviewVerdict.ChangesRequested,
            "PENDING" or "REQUEST_REVIEW" => UpstreamReviewVerdict.Pending,
            _ => UpstreamReviewVerdict.Commented,
        };
    }

    private static UpstreamCheckState MapCheckState(string? status)
        => status?.ToLowerInvariant() switch
        {
            "success" => UpstreamCheckState.Passing,
            "pending" => UpstreamCheckState.Pending,
            "error" or "failure" => UpstreamCheckState.Failing,
            "warning" => UpstreamCheckState.Neutral,
            _ => UpstreamCheckState.Neutral,
        };

    private static string? WebhookPath(GiteaEndpoint endpoint, string scope)
    {
        // Repository paths stay relative to the repo root (the sender
        // prepends it); the other three scopes are instance-level routes.
        if (string.Equals(scope, UpstreamWebhookScopes.Repository, StringComparison.OrdinalIgnoreCase))
            return "hooks";
        if (string.Equals(scope, UpstreamWebhookScopes.Organization, StringComparison.OrdinalIgnoreCase)
            || string.Equals(scope, "organisation", StringComparison.OrdinalIgnoreCase))
            return $"orgs/{Uri.EscapeDataString(endpoint.Owner)}/hooks";
        if (string.Equals(scope, UpstreamWebhookScopes.User, StringComparison.OrdinalIgnoreCase))
            return "user/hooks";
        if (string.Equals(scope, UpstreamWebhookScopes.System, StringComparison.OrdinalIgnoreCase))
            return "admin/hooks";
        return null;
    }

    private static UpstreamWebhookSubscription MapHook(GiteaHook hook, string scope)
        => new()
        {
            Id = hook.Id.ToString(CultureInfo.InvariantCulture),
            Scope = scope,
            Events = hook.Events ?? [],
            TargetUrl = hook.Config is not null && hook.Config.TryGetValue("url", out var url) ? url : string.Empty,
        };

    private async Task<List<T>> GetAllPagesAsync<T>(
        GiteaEndpoint endpoint, string relativePath, CancellationToken ct)
    {
        var all = new List<T>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var separator = relativePath.Contains('?') ? "&" : "?";
            using var response = await SendAsync(endpoint, HttpMethod.Get,
                $"{relativePath}{separator}page={page}&limit={PageSize}", ct);
            await ThrowIfNotSuccessAsync(endpoint, response, $"GET {relativePath}", ct);
            var items = await response.Content.ReadFromJsonAsync<List<T>>(cancellationToken: ct);
            if (items is null || items.Count == 0)
                return all;
            all.AddRange(items);
            if (items.Count < PageSize)
                return all;
        }

        throw new GiteaUpstreamException(
            $"Gitea list '{relativePath}' exceeded {MaxPages} pages; refusing to return a partial list.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        GiteaEndpoint endpoint, HttpMethod method, string relativePath, CancellationToken ct,
        Func<HttpContent>? contentFactory = null)
    {
        string url = relativePath.Length == 0
            ? $"{endpoint.ApiBase}/{endpoint.RepoPath}"
            : IsTopLevelPath(relativePath)
                ? $"{endpoint.ApiBase}/{relativePath}"
                : $"{endpoint.ApiBase}/{endpoint.RepoPath}/{relativePath}";

        var client = _httpClientFactory.CreateClient("gitea-upstream");
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrWhiteSpace(endpoint.Token))
                request.Headers.Authorization = new AuthenticationHeaderValue("token", endpoint.Token);
            request.Headers.UserAgent.ParseAdd("CodeyBox-GiteaUpstream/1.0");
            if (contentFactory is not null && method != HttpMethod.Get && method != HttpMethod.Delete)
                request.Content = contentFactory();

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new GiteaUpstreamException(
                    $"Gitea request failed (unreachable): {Scrub(ex.Message, endpoint.Token)}", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new GiteaUpstreamException("Gitea request timed out.", ex);
            }

            if (response.StatusCode != (HttpStatusCode)429 || attempt > MaxRateLimitRetries)
                return response;

            response.Dispose();
            var delay = RetryDelay(response, attempt);
            _host.Logger.LogWarning(
                "Gitea rate-limited the request; retrying in {Delay}s (attempt {Attempt})",
                delay.TotalSeconds, attempt);
            await Task.Delay(delay, ct);
        }
    }

    private static bool IsTopLevelPath(string relativePath)
        => relativePath.StartsWith("user/hooks", StringComparison.Ordinal)
            || relativePath.StartsWith("admin/hooks", StringComparison.Ordinal)
            || relativePath.StartsWith("orgs/", StringComparison.Ordinal);

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta
            && delta >= TimeSpan.Zero
            && delta <= TimeSpan.FromSeconds(MaxRateLimitDelaySeconds))
            return delta;
        var backoff = Math.Min(1 << attempt, MaxRateLimitDelaySeconds);
        return TimeSpan.FromSeconds(backoff);
    }

    private async Task ThrowIfNotSuccessAsync(
        GiteaEndpoint endpoint, HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await ReadErrorBodyAsync(response, ct);
        throw new GiteaUpstreamException(
            $"Gitea {operation} failed ({(int)response.StatusCode}): {Scrub(detail, endpoint.Token)}",
            response.StatusCode);
    }

    private static async Task<string> ReadErrorBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body))
                return response.ReasonPhrase ?? "unknown error";
            var trimmed = body.Trim();
            return trimmed.Length > MaxErrorBodyChars
                ? trimmed[..MaxErrorBodyChars] + "…"
                : trimmed;
        }
        catch
        {
            return response.ReasonPhrase ?? "unknown error";
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workdir, IReadOnlyDictionary<string, string> extraEnv,
        CancellationToken ct, params string[] args)
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

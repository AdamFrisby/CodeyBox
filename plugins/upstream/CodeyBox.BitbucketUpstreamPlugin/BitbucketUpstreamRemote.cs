using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using CodeyBox.Core;
using CodeyBox.PluginSdk;
using Microsoft.Extensions.Logging;

namespace CodeyBox.BitbucketUpstreamPlugin;

/// <summary>
/// Upstream remote plugin for Bitbucket Cloud, implementing
/// <see cref="IUpstreamRemote"/> against Bitbucket Cloud API 2.0
/// (<c>api.bitbucket.org/2.0</c>).
///
/// <para>Core lifecycle: pushes the work branch via the host git module, opens
/// a pull request (<c>POST /repositories/{workspace}/{repo}/pullrequests</c>),
/// optionally auto-merges it, merges upstream branches host-side (Bitbucket
/// Cloud exposes no branch-to-branch merge API), fetches the base branch, and
/// lists/reads PRs. Extended surfaces map Bitbucket's native concepts without
/// translation into GitHub's shape: PR participants as reviews, commit build
/// statuses as checks (Bitbucket's model is coarser — statuses attach to a
/// commit with a key and state, with no check-run structure — and is reported
/// as such), plain plus inline PR comments, repository webhooks, and
/// repository plus branch-restriction metadata. Bitbucket Cloud has no
/// releases concept, no mergeability flag, and no non-repository webhook
/// scopes, so those stay on the contract defaults (unsupported) rather than
/// synthesised.</para>
///
/// <para>Configuration: per-project <c>Upstream.PluginConfig</c> entries
/// (<c>BaseUrl</c>, <c>Workspace</c>, <c>Repository</c>, <c>PageSize</c>,
/// <c>MaxListPages</c>) override the plugin-scoped defaults
/// (<c>CodeyBox:Plugins:codeybox.bitbucket-upstream</c>) inside
/// <c>CompleteAsync</c>, which is the only member that carries a project id.
/// Every other member resolves the scoped defaults. Credentials never come
/// from configuration: only the <em>name</em> of the env var holding the
/// credential is configured, and the value is read from the process
/// environment at call time — the sandbox never sees it because the
/// credential only ever flows into host-side git env and per-request HTTP
/// headers.</para>
///
/// <para>Failure classification: forge-side failures (unreachable, 401/403,
/// 429, 5xx, unexpected bodies) throw
/// <see cref="BitbucketUpstreamException"/> so the orchestrator retries as
/// infrastructure — never a verdict on the diff. Soft outcomes (PR already
/// exists, merge blocked) return partial results. Unsupported capabilities
/// return <c>null</c> (or empty when supported and empty) and never fail a
/// work item.</para>
/// </summary>
[CodeyBoxPlugin(
    id: "codeybox.bitbucket-upstream",
    displayName: "Bitbucket Upstream Remote",
    minHostApiVersion: "1.0")]
public sealed class BitbucketUpstreamRemote : IUpstreamRemote, IPluginInitializer
{
    public string Name => "bitbucket";

    private const int MaxErrorBodyChars = 2048;
    private const int MaxRateLimitRetries = 3;
    private const int MaxRateLimitDelaySeconds = 30;

    private readonly IGitHost _gitHost;
    private readonly IHttpClientFactory _httpClientFactory;

    private IPluginHost _host = null!;
    private IUpstreamPluginHost _upstreamHost = null!;
    private Microsoft.Extensions.Configuration.IConfigurationSection _scopedConfig = null!;

    public BitbucketUpstreamRemote(IGitHost gitHost, IHttpClientFactory httpClientFactory)
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
                "Bitbucket upstream plugin requires a host implementing IUpstreamPluginHost.");
        }

        context.Logger.LogInformation("BitbucketUpstreamRemote initialized");
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Core lifecycle
    // ------------------------------------------------------------------

    /// <summary>
    /// Pushes <paramref name="branch"/> to the scoped Bitbucket Cloud repo via
    /// the host git module. Returns a failure result (rather than throwing) on
    /// transport errors, matching the <c>git-generic</c> push contract.
    /// </summary>
    public async Task<UpstreamPushResult> PushAsync(
        string repositoryId, string branch, CancellationToken ct = default)
    {
        BitbucketEndpoint endpoint;
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
                endpoint.GitAuthEnv,
                UpstreamPushReconcileStrategy.Rebase,
                ct);
            return new UpstreamPushResult(true, null);
        }
        catch (Exception ex)
        {
            return new UpstreamPushResult(false, endpoint.Scrub(ex.Message));
        }
    }

    /// <summary>
    /// Full completion flow: push the work branch, open a PR (Bitbucket
    /// answers 400 carrying an "already exists" body when the PR exists,
    /// which is a soft partial outcome, not an error), then auto-merge when
    /// requested. Honors <c>ExistingPullRequestNumber</c> for the
    /// orchestrator's race-recovery path by skipping creation. Transient
    /// forge failures throw <see cref="BitbucketUpstreamException"/> for
    /// retry.
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

        int prId;
        string? prUrl;
        if (request.ExistingPullRequestNumber is { } existing)
        {
            GuardNumber(existing, nameof(request.ExistingPullRequestNumber));
            var pr = await GetPullRequestDetailAsync(endpoint, existing, ct)
                ?? throw new BitbucketUpstreamException(
                    $"Bitbucket PR #{existing} from a prior attempt is no longer available.");
            prId = existing;
            prUrl = pr.Links?.Html?.Href ?? WebPullUrl(endpoint, existing);
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

            prId = ToInt32(created.Id, "PR id");
            prUrl = created.Links?.Html?.Href ?? WebPullUrl(endpoint, prId);
            _host.Logger.LogInformation("Bitbucket PR #{Id} opened: {Url}", prId, prUrl);
        }

        if (!request.AutoMerge)
        {
            return new UpstreamCompletionOutcome
            {
                BranchPushed = true,
                PullRequestUrl = prUrl,
                PullRequestNumber = prId,
            };
        }

        var (mergedSha, notes) = await MergePullRequestAsync(
            endpoint, prId, request.MergeMethod, ct);
        if (mergedSha is not null)
            _host.Logger.LogInformation("Bitbucket PR #{Id} auto-merged: {Sha}", prId, mergedSha);

        return new UpstreamCompletionOutcome
        {
            BranchPushed = true,
            PullRequestUrl = prUrl,
            PullRequestNumber = prId,
            MergedSha = mergedSha,
            Notes = notes,
        };
    }

    /// <summary>
    /// Bitbucket Cloud has no branch-to-branch merge API, so this merges
    /// host-side: clone the target branch, fetch the source, <c>git
    /// merge</c>, push back. Returns <c>false</c> on merge conflicts (leaving
    /// the forge untouched); throws
    /// <see cref="BitbucketUpstreamException"/> on transport/auth failures.
    /// </summary>
    public async Task<bool> TryMergeUpstreamBranchAsync(
        string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        GuardBranch(targetBranch, nameof(targetBranch));
        GuardBranch(sourceBranch, nameof(sourceBranch));
        var endpoint = ResolveScoped("TryMergeUpstreamBranchAsync");
        var env = endpoint.GitAuthEnv;

        var tmpDir = Path.Combine(Path.GetTempPath(), "codeybox-sync-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tmpDir);
            var clone = await RunGitAsync(tmpDir, env, ct,
                "clone", "--branch", targetBranch, "--single-branch", "--", endpoint.GitRemoteUrl, tmpDir);
            if (clone.ExitCode != 0)
                throw new BitbucketUpstreamException(
                    $"git clone of '{SanitizeForLog(targetBranch)}' failed: {endpoint.Scrub(clone.Stderr)}");

            var fetch = await RunGitAsync(tmpDir, env, ct, "fetch", "origin", sourceBranch);
            if (fetch.ExitCode != 0)
                throw new BitbucketUpstreamException(
                    $"git fetch of '{SanitizeForLog(sourceBranch)}' failed: {endpoint.Scrub(fetch.Stderr)}");

            var merge = await RunGitAsync(tmpDir, env, ct,
                "merge", "FETCH_HEAD", "--no-edit", "--no-ff");
            if (merge.ExitCode != 0)
            {
                await RunGitAsync(tmpDir, env, ct, "merge", "--abort");
                return false;
            }

            var push = await RunGitAsync(tmpDir, env, ct, "push", "origin", targetBranch);
            if (push.ExitCode != 0)
                throw new BitbucketUpstreamException(
                    $"git push of '{SanitizeForLog(targetBranch)}' failed: {endpoint.Scrub(push.Stderr)}");

            return true;
        }
        finally
        {
            try { if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Fetches the current head of <paramref name="baseBranch"/> from
    /// Bitbucket Cloud into the host bare repo, overwriting the local ref.
    /// Returns the new sha (or null when the upstream does not advertise the
    /// branch); throws <see cref="BitbucketUpstreamException"/> on
    /// transport/auth failures.
    /// </summary>
    public async Task<string?> FetchBaseBranchAsync(
        string repositoryId, string baseBranch, CancellationToken ct = default)
    {
        GuardBranch(baseBranch, nameof(baseBranch));
        var endpoint = ResolveScoped("FetchBaseBranchAsync");
        try
        {
            return await _gitHost.FetchUpstreamBranchAsync(
                repositoryId, endpoint.GitRemoteUrl, baseBranch, endpoint.GitAuthEnv, ct);
        }
        catch (Exception ex)
        {
            throw new BitbucketUpstreamException(
                $"Failed to fetch base branch '{SanitizeForLog(baseBranch)}': {endpoint.Scrub(ex.Message)}",
                ex);
        }
    }

    // Bitbucket Cloud has no releases concept, so CreateTagAndReleaseAsync
    // keeps the contract default (null = unsupported, non-fatal).

    /// <summary>
    /// Open PRs whose head branch starts with <paramref name="branchPrefix"/>.
    /// Bitbucket Cloud exposes no mergeability flag on PRs, so
    /// <c>HasMergeConflict</c> is always <c>false</c> here: the sweeper cannot
    /// detect conflicts on this forge (see README). PRs missing head sha or
    /// base branch are skipped so the sweeper reconsiders them next tick.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(
        string branchPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(branchPrefix))
            throw new ArgumentException("branchPrefix must be non-empty", nameof(branchPrefix));
        var endpoint = ResolveScoped("ListOpenPullRequestsAsync");

        var pulls = await GetPagedAsync<BitbucketPullRequest>(endpoint, "pullrequests?state=OPEN", ct);
        var result = new List<UpstreamPullRequest>();
        foreach (var pull in pulls)
        {
            if (pull.Id <= 0 || pull.Id > int.MaxValue)
                continue;
            if (!string.Equals(pull.State, "OPEN", StringComparison.OrdinalIgnoreCase))
                continue;
            var headBranch = pull.Source?.Branch?.Name;
            if (string.IsNullOrEmpty(headBranch)
                || !headBranch.StartsWith(branchPrefix, StringComparison.Ordinal))
                continue;
            var headSha = pull.Source?.Commit?.Hash;
            var baseBranch = pull.Destination?.Branch?.Name;
            if (string.IsNullOrEmpty(headSha) || string.IsNullOrEmpty(baseBranch))
                continue;
            result.Add(new UpstreamPullRequest
            {
                Number = (int)pull.Id,
                Url = pull.Links?.Html?.Href ?? WebPullUrl(endpoint, (int)pull.Id),
                HeadBranch = headBranch,
                HeadSha = headSha,
                BaseBranch = baseBranch,
                HasMergeConflict = false,
            });
        }

        return result;
    }

    /// <summary>
    /// Reads a pull request by Bitbucket-assigned id. Returns <c>null</c> on
    /// 404 (PR unavailable); throws
    /// <see cref="BitbucketUpstreamException"/> on infrastructure failures.
    /// </summary>
    public async Task<UpstreamPullRequestState?> GetPullRequestAsync(
        int number, CancellationToken ct = default)
    {
        GuardNumber(number, nameof(number));
        var endpoint = ResolveScoped("GetPullRequestAsync");

        var detail = await GetPullRequestDetailAsync(endpoint, number, ct);
        if (detail is null)
            return null;

        var status = detail.State?.ToUpperInvariant() switch
        {
            "OPEN" => PullRequestStatus.Open,
            "MERGED" => PullRequestStatus.Merged,
            _ => PullRequestStatus.Closed,
        };
        return new UpstreamPullRequestState(
            number,
            detail.Links?.Html?.Href ?? WebPullUrl(endpoint, number),
            status,
            detail.MergeCommit?.Hash);
    }

    /// <summary>
    /// Review state from Bitbucket PR participants: approvals, change
    /// requests, and still-outstanding reviewers, with the quorum taken from
    /// the destination branch's <c>require_approvals_to_merge</c> restriction.
    /// Returns <c>null</c> when the PR is unavailable. A non-<c>null</c>
    /// result with empty <c>Reviews</c> means reviews are supported and there
    /// are none yet.
    /// </summary>
    public async Task<UpstreamReviewState?> GetReviewStateAsync(
        int number, CancellationToken ct = default)
    {
        GuardNumber(number, nameof(number));
        var endpoint = ResolveScoped("GetReviewStateAsync");

        var detail = await GetPullRequestDetailAsync(endpoint, number, ct);
        if (detail is null)
            return null;

        var reviews = new List<UpstreamReview>();
        var outstanding = new List<string>();
        var approvals = 0;
        var blocked = false;
        foreach (var participant in detail.Participants ?? [])
        {
            var name = participant.User?.DisplayName ?? participant.User?.Nickname;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            var verdict = MapParticipantVerdict(participant);
            reviews.Add(new UpstreamReview
            {
                Reviewer = name,
                Verdict = verdict,
                SubmittedAt = participant.ParticipatedOn,
            });
            if (verdict == UpstreamReviewVerdict.Approved)
                approvals++;
            else if (verdict == UpstreamReviewVerdict.ChangesRequested)
                blocked = true;
            else if (verdict == UpstreamReviewVerdict.Pending
                && string.Equals(participant.Role, "REVIEWER", StringComparison.OrdinalIgnoreCase))
                outstanding.Add(name);
        }

        var required = await GetRequiredApprovalsAsync(
            endpoint, detail.Destination?.Branch?.Name, ct);
        return new UpstreamReviewState
        {
            Reviews = reviews,
            RequiredReviewers = outstanding,
            RequiredApprovalCount = required,
            RequirementsMet = !blocked && approvals >= required,
        };
    }

    /// <summary>
    /// CI results from Bitbucket commit build statuses for
    /// <paramref name="headSha"/>: each build's key, forge-neutral state and
    /// human-openable URL. Bitbucket's model is coarser than GitHub checks —
    /// statuses attach to a commit with a key and state and carry no
    /// check-run structure — and is reported as such.
    /// <c>RequiredChecksPassed</c> is "nothing failing" (empty counts as
    /// satisfied) because Bitbucket exposes no combined required-check
    /// verdict. Returns <c>null</c> when the commit's statuses are
    /// unavailable (404).
    /// </summary>
    public async Task<UpstreamCheckSummary?> GetCheckResultsAsync(
        string headSha, CancellationToken ct = default)
    {
        GuardSha(headSha, nameof(headSha));
        var endpoint = ResolveScoped("GetCheckResultsAsync");

        using var response = await SendAsync(
            endpoint, HttpMethod.Get,
            $"commit/{Uri.EscapeDataString(headSha)}/statuses/builds?pagelen={endpoint.PageSize}&page=1",
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(endpoint, response, "GET commit/{sha}/statuses/builds", ct);

        var page = await response.Content.ReadFromJsonAsync<BitbucketPaged<BitbucketBuildStatus>>(
            cancellationToken: ct);
        if (page is null)
            throw new BitbucketUpstreamException("Bitbucket returned an empty build-status body.");

        var checks = (page.Values ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s.Key ?? s.Name))
            .Select(s => new UpstreamCheckResult
            {
                Name = s.Key ?? s.Name!,
                State = MapBuildState(s.State),
                DetailsUrl = string.IsNullOrWhiteSpace(s.Url) ? null : s.Url,
                Description = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description,
            })
            .ToList();

        return new UpstreamCheckSummary
        {
            Checks = checks,
            RequiredChecksPassed = checks.All(c => c.State == UpstreamCheckState.Passing),
        };
    }

    /// <summary>
    /// Lists PR comments, oldest first. Both plain discussion and inline
    /// code-anchored comments are returned; inline ones carry
    /// <c>FilePath</c> and <c>Line</c>. Returns <c>null</c> when the PR is
    /// unavailable; an empty list means comments are supported and there are
    /// none.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamComment>?> ListCommentsAsync(
        int number, CancellationToken ct = default)
    {
        GuardNumber(number, nameof(number));
        var endpoint = ResolveScoped("ListCommentsAsync");

        List<BitbucketComment> comments;
        try
        {
            comments = await GetPagedAsync<BitbucketComment>(endpoint, $"pullrequests/{number}/comments", ct);
        }
        catch (BitbucketUpstreamException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return comments
            .Where(c => c.Id > 0 && !string.IsNullOrWhiteSpace(c.Content?.Raw))
            .OrderBy(c => c.Id)
            .Select(c => new UpstreamComment
            {
                Id = c.Id.ToString(CultureInfo.InvariantCulture),
                Author = c.User?.DisplayName ?? c.User?.Nickname ?? "unknown",
                Body = c.Content!.Raw!,
                FilePath = string.IsNullOrWhiteSpace(c.Inline?.Path) ? null : c.Inline.Path,
                Line = string.IsNullOrWhiteSpace(c.Inline?.Path)
                    ? null
                    : c.Inline!.To ?? c.Inline.From,
                CreatedAt = c.CreatedOn,
            })
            .ToList();
    }

    /// <summary>
    /// Posts a plain top-level comment, or — when <c>ReplyToId</c> carries a
    /// Bitbucket comment id — a reply inside that thread (Bitbucket's
    /// <c>parent</c> reference). File-anchored comments return <c>null</c>
    /// without touching the forge: Bitbucket inline comments need diff-hunk
    /// positions a bare path-plus-line cannot faithfully supply. Returns
    /// <c>null</c> when the PR is unavailable.
    /// </summary>
    public async Task<UpstreamComment?> PostCommentAsync(
        int number, NewUpstreamComment comment, CancellationToken ct = default)
    {
        GuardNumber(number, nameof(number));
        ArgumentNullException.ThrowIfNull(comment);
        if (comment is { FilePath: not null } or { Line: not null })
            return null;
        var endpoint = ResolveScoped("PostCommentAsync");

        Dictionary<string, object?> payload = new()
        {
            ["content"] = new Dictionary<string, string> { ["raw"] = comment.Body },
        };
        if (comment.ReplyToId is not null)
        {
            if (!long.TryParse(comment.ReplyToId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentId)
                || parentId <= 0)
                return null;
            payload["parent"] = new Dictionary<string, long> { ["id"] = parentId };
        }

        using var response = await SendAsync(endpoint, HttpMethod.Post,
            $"pullrequests/{number}/comments", ct, () => JsonContent.Create(payload));
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(endpoint, response, "POST pullrequests/{id}/comments", ct);

        var created = await response.Content.ReadFromJsonAsync<BitbucketComment>(cancellationToken: ct);
        if (created is null || created.Id <= 0)
            throw new BitbucketUpstreamException("Bitbucket returned an empty comment body.");
        return new UpstreamComment
        {
            Id = created.Id.ToString(CultureInfo.InvariantCulture),
            Author = created.User?.DisplayName ?? created.User?.Nickname ?? "unknown",
            Body = created.Content?.Raw ?? comment.Body,
            CreatedAt = created.CreatedOn,
        };
    }

    /// <summary>
    /// Lists repository webhook subscriptions. Returns <c>null</c> when the
    /// hooks endpoint is unavailable; an empty list means webhooks are
    /// supported and none exist. Bitbucket Cloud webhooks are
    /// repository-scoped only, so every subscription reports the
    /// <c>repository</c> scope.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(
        CancellationToken ct = default)
    {
        var endpoint = ResolveScoped("ListWebhookSubscriptionsAsync");
        List<BitbucketHook> hooks;
        try
        {
            hooks = await GetPagedAsync<BitbucketHook>(endpoint, "hooks", ct);
        }
        catch (BitbucketUpstreamException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return hooks
            .Where(h => !string.IsNullOrWhiteSpace(h.Uuid))
            .Select(h => new UpstreamWebhookSubscription
            {
                Id = h.Uuid!,
                Scope = UpstreamWebhookScopes.Repository,
                Events = h.Events ?? [],
                TargetUrl = h.Url ?? string.Empty,
            })
            .ToList();
    }

    /// <summary>
    /// Creates a repository webhook subscription. Bitbucket Cloud webhooks are
    /// repository-scoped only: any other scope returns <c>null</c> without
    /// touching the forge rather than a forced translation.
    /// </summary>
    public async Task<UpstreamWebhookSubscription?> CreateWebhookSubscriptionAsync(
        NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!string.Equals(subscription.Scope, UpstreamWebhookScopes.Repository, StringComparison.OrdinalIgnoreCase))
            return null;
        var endpoint = ResolveScoped("CreateWebhookSubscriptionAsync");

        using var response = await SendAsync(endpoint, HttpMethod.Post, "hooks", ct,
            () => JsonContent.Create(new Dictionary<string, object>
            {
                ["description"] = "CodeyBox upstream webhook",
                ["url"] = subscription.TargetUrl,
                ["active"] = true,
                ["events"] = subscription.Events,
            }));
        await ThrowIfNotSuccessAsync(endpoint, response, "POST hooks", ct);

        var created = await response.Content.ReadFromJsonAsync<BitbucketHook>(cancellationToken: ct);
        if (created is null || string.IsNullOrWhiteSpace(created.Uuid))
            throw new BitbucketUpstreamException("Bitbucket returned an empty webhook body.");
        return new UpstreamWebhookSubscription
        {
            Id = created.Uuid,
            Scope = UpstreamWebhookScopes.Repository,
            Events = created.Events ?? subscription.Events,
            TargetUrl = created.Url ?? subscription.TargetUrl,
        };
    }

    /// <summary>
    /// Removes a repository webhook subscription by Bitbucket-assigned
    /// <c>uuid</c>. Returns <c>true</c> when removed, <c>false</c> when the id
    /// is unknown.
    /// </summary>
    public async Task<bool?> DeleteWebhookSubscriptionAsync(
        string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Webhook id must not be empty.", nameof(id));
        var endpoint = ResolveScoped("DeleteWebhookSubscriptionAsync");

        using var response = await SendAsync(
            endpoint, HttpMethod.Delete, $"hooks/{Uri.EscapeDataString(id.Trim())}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await ThrowIfNotSuccessAsync(endpoint, response, "DELETE hooks/{uid}", ct);
        return true;
    }

    /// <summary>
    /// Repository metadata: default branch, visibility (Bitbucket Cloud
    /// repositories are <c>public</c> or <c>private</c> — there is no
    /// <c>internal</c>), and branch protection derived from branch
    /// restrictions grouped by pattern. Returns <c>null</c> when the
    /// repository is unavailable. Restriction reads need elevated scopes; on
    /// 403/404 the metadata is reported with no rules rather than failing.
    /// </summary>
    public async Task<UpstreamRepositoryMetadata?> GetRepositoryMetadataAsync(
        CancellationToken ct = default)
    {
        var endpoint = ResolveScoped("GetRepositoryMetadataAsync");

        using var response = await SendAsync(endpoint, HttpMethod.Get, string.Empty, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(endpoint, response, "GET repositories/{workspace}/{repo}", ct);

        var repo = await response.Content.ReadFromJsonAsync<BitbucketRepository>(cancellationToken: ct);
        if (repo is null)
            throw new BitbucketUpstreamException("Bitbucket returned an empty repository body.");

        var protections = new List<UpstreamBranchProtection>();
        try
        {
            var restrictions = await GetPagedAsync<BitbucketBranchRestriction>(
                endpoint, "branch-restrictions", ct);
            protections.AddRange(GroupRestrictions(restrictions));
        }
        catch (BitbucketUpstreamException ex)
            when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            _host.Logger.LogDebug("Bitbucket branch restrictions unreadable ({Status}); reporting repo without rules",
                (int?)ex.StatusCode);
        }

        return new UpstreamRepositoryMetadata
        {
            DefaultBranch = string.IsNullOrWhiteSpace(repo.MainBranch?.Name) ? null : repo.MainBranch.Name,
            Visibility = repo.IsPrivate
                ? UpstreamRepositoryVisibility.Private
                : UpstreamRepositoryVisibility.Public,
            BranchProtections = protections,
        };
    }

    // ------------------------------------------------------------------
    // Config + HTTP plumbing (every guard sits adjacent to its sink)
    // ------------------------------------------------------------------

    private sealed record BitbucketEndpoint(
        string ApiBase,
        string Workspace,
        string Repo,
        BitbucketCredential? Credential,
        int PageSize,
        int MaxPages)
    {
        public string RepoPath =>
            $"repositories/{Uri.EscapeDataString(Workspace)}/{Uri.EscapeDataString(Repo)}";

        public string GitRemoteUrl =>
            $"https://bitbucket.org/{Uri.EscapeDataString(Workspace)}/{Uri.EscapeDataString(Repo)}.git";

        public IReadOnlyDictionary<string, string> GitAuthEnv
        {
            get
            {
                if (Credential is null)
                    return new Dictionary<string, string>();
                // Bitbucket git-over-HTTPS takes the app password as the
                // password; OAuth/API tokens authenticate as x-token-auth.
                return Credential.BasicPassword is { } password
                    ? new Dictionary<string, string>
                    {
                        ["GIT_USERNAME"] = Credential.BasicUsername!,
                        ["GIT_PASSWORD"] = password,
                    }
                    : new Dictionary<string, string>
                    {
                        ["GIT_USERNAME"] = "x-token-auth",
                        ["GIT_PASSWORD"] = Credential.BearerToken!,
                    };
            }
        }

        public void ApplyAuth(HttpRequestMessage request)
        {
            if (Credential is null)
                return;
            request.Headers.Authorization = Credential.BasicPassword is { } password
                ? new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes($"{Credential.BasicUsername}:{password}")))
                : new AuthenticationHeaderValue("Bearer", Credential.BearerToken);
        }

        public string Scrub(string message)
        {
            if (string.IsNullOrEmpty(message))
                return message;
            return Credential?.Scrub(message) ?? message;
        }
    }

    private sealed record BitbucketCredential(
        string? BasicUsername,
        string? BasicPassword,
        string? BearerToken)
    {
        public string Scrub(string message)
        {
            if (BasicPassword is { Length: > 0 } password)
                message = message.Replace(password, "***", StringComparison.Ordinal);
            if (BearerToken is { Length: > 0 } token)
                message = message.Replace(token, "***", StringComparison.Ordinal);
            return message;
        }
    }

    private BitbucketEndpoint ResolveScoped(string context)
    {
        var opts = BitbucketUpstreamOptions.FromScopedConfig(_scopedConfig);
        return Materialize(opts, opts.TokenEnvVar, context);
    }

    private BitbucketEndpoint ResolveForRequest(UpstreamCompletionRequest request)
    {
        var pluginConfig = _upstreamHost.GetProjectUpstreamConfig(request.ProjectId);
        var opts = BitbucketUpstreamOptions.FromScopedConfig(_scopedConfig)
            .WithProjectOverrides(pluginConfig);
        var tokenEnvVar = string.IsNullOrWhiteSpace(request.TokenEnvVar)
            ? opts.TokenEnvVar
            : request.TokenEnvVar;
        return Materialize(opts, tokenEnvVar, $"Project {request.ProjectId}");
    }

    private static BitbucketEndpoint Materialize(
        BitbucketUpstreamOptions opts, string? tokenEnvVar, string context)
    {
        var apiBase = opts.Validate(context);
        return new BitbucketEndpoint(
            apiBase, opts.Workspace, opts.Repository,
            ParseCredential(tokenEnvVar, context),
            opts.PageSize, opts.MaxListPages);
    }

    private static BitbucketCredential? ParseCredential(string? envVarName, string context)
    {
        if (string.IsNullOrWhiteSpace(envVarName))
            return null;
        var raw = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrEmpty(raw))
            throw new BitbucketUpstreamException(
                $"{context}: env var '{envVarName}' is empty (set it to the Bitbucket credential)");
        var separator = raw.IndexOf(':');
        if (separator > 0 && separator < raw.Length - 1)
        {
            // App-password form: "username:app-password" -> HTTP Basic.
            return new BitbucketCredential(
                raw[..separator], raw[(separator + 1)..], null);
        }

        // Bare token form (OAuth access token or repository access token).
        return new BitbucketCredential(null, null, raw);
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

    private static void GuardNumber(int number, string paramName)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(paramName, "Pull request number must be positive.");
    }

    private static void GuardSha(string sha, string paramName)
    {
        if (string.IsNullOrWhiteSpace(sha))
            throw new ArgumentException("Commit sha must not be empty.", paramName);
        if (sha.Length > 256 || sha.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Commit sha contains invalid characters.", paramName);
    }

    private static string SanitizeForLog(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        return new string(value.Where(c => !char.IsControl(c)).ToArray());
    }

    private static int ToInt32(long value, string what)
    {
        if (value < 0 || value > int.MaxValue)
            throw new BitbucketUpstreamException($"Bitbucket {what} {value} is out of range.");
        return (int)value;
    }

    private static UpstreamPushReconcileStrategy ToReconcileStrategy(string mergeMethod)
        => mergeMethod.Equals("rebase", StringComparison.OrdinalIgnoreCase)
            ? UpstreamPushReconcileStrategy.Rebase
            : UpstreamPushReconcileStrategy.Merge;

    private static string WebPullUrl(BitbucketEndpoint endpoint, int id)
        => $"https://bitbucket.org/{endpoint.Workspace}/{endpoint.Repo}/pull-requests/{id}";

    private async Task PushBranchAsync(
        string repositoryId, BitbucketEndpoint endpoint, string branch,
        UpstreamPushReconcileStrategy strategy, CancellationToken ct)
    {
        try
        {
            await _gitHost.PushToUpstreamAsync(
                repositoryId, endpoint.GitRemoteUrl, branch, endpoint.GitAuthEnv, strategy, ct);
        }
        catch (Exception ex)
        {
            throw new BitbucketUpstreamException(
                $"Failed to push work branch '{SanitizeForLog(branch)}': {endpoint.Scrub(ex.Message)}",
                ex);
        }
    }

    private async Task<BitbucketPullRequest?> OpenPullRequestAsync(
        BitbucketEndpoint endpoint, UpstreamCompletionRequest request, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Post, "pullrequests", ct,
            () => JsonContent.Create(new Dictionary<string, object>
            {
                ["title"] = request.Title,
                ["description"] = request.Description ?? string.Empty,
                ["source"] = new Dictionary<string, object>
                {
                    ["branch"] = new Dictionary<string, string> { ["name"] = request.WorkBranch },
                },
                ["destination"] = new Dictionary<string, object>
                {
                    ["branch"] = new Dictionary<string, string> { ["name"] = request.BaseBranch },
                },
            }));
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var detail = await ReadErrorBodyAsync(response, ct);
            if (detail.Contains("already", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("duplicate", StringComparison.OrdinalIgnoreCase))
            {
                _host.Logger.LogWarning(
                    "Bitbucket: PR already exists for branch '{Branch}' ({Status}); treating as soft-failure",
                    SanitizeForLog(request.WorkBranch), (int)response.StatusCode);
                return null;
            }

            throw new BitbucketUpstreamException(
                $"Bitbucket rejected PR creation ({(int)response.StatusCode}): {endpoint.Scrub(detail)}",
                response.StatusCode);
        }

        await ThrowIfNotSuccessAsync(endpoint, response, "POST pullrequests", ct);
        var created = await response.Content.ReadFromJsonAsync<BitbucketPullRequest>(cancellationToken: ct);
        if (created is null)
            throw new BitbucketUpstreamException("Bitbucket returned an empty pull request body.");
        return created;
    }

    private async Task<(string? Sha, string? Notes)> MergePullRequestAsync(
        BitbucketEndpoint endpoint, int prId, string mergeMethod, CancellationToken ct)
    {
        using var response = await SendAsync(endpoint, HttpMethod.Post, $"pullrequests/{prId}/merge", ct,
            () => JsonContent.Create(new Dictionary<string, object>
            {
                ["merge_strategy"] = MapMergeStrategy(mergeMethod),
                ["close_source_branch"] = false,
            }));

        if (response.StatusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.Conflict
            or HttpStatusCode.UnprocessableEntity)
        {
            var detail = await ReadErrorBodyAsync(response, ct);
            _host.Logger.LogWarning(
                "Bitbucket POST /pullrequests/{Id}/merge returned {Status} (not mergeable); leaving PR open",
                prId, (int)response.StatusCode);
            return (null, $"Auto-merge blocked ({endpoint.Scrub(detail)}); PR left open");
        }

        await ThrowIfNotSuccessAsync(endpoint, response, "POST pullrequests/{id}/merge", ct);

        var merged = await GetPullRequestDetailAsync(endpoint, prId, ct);
        return (merged?.MergeCommit?.Hash, null);
    }

    private static string MapMergeStrategy(string mergeMethod)
        => mergeMethod.Equals("squash", StringComparison.OrdinalIgnoreCase) ? "squash"
            : mergeMethod.Equals("rebase", StringComparison.OrdinalIgnoreCase) ? "fast_forward"
            : "merge_commit";

    private async Task<BitbucketPullRequest?> GetPullRequestDetailAsync(
        BitbucketEndpoint endpoint, int id, CancellationToken ct)
    {
        using var response = await SendAsync(
            endpoint, HttpMethod.Get, $"pullrequests/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowIfNotSuccessAsync(endpoint, response, "GET pullrequests/{id}", ct);
        return await response.Content.ReadFromJsonAsync<BitbucketPullRequest>(cancellationToken: ct);
    }

    private async Task<int> GetRequiredApprovalsAsync(
        BitbucketEndpoint endpoint, string? destinationBranch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(destinationBranch))
            return 0;
        List<BitbucketBranchRestriction> restrictions;
        try
        {
            restrictions = await GetPagedAsync<BitbucketBranchRestriction>(
                endpoint, "branch-restrictions", ct);
        }
        catch (BitbucketUpstreamException ex)
            when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return 0;
        }

        return restrictions
            .Where(r => string.Equals(r.Kind, "require_approvals_to_merge", StringComparison.OrdinalIgnoreCase)
                && MatchesPattern(r.Pattern, destinationBranch))
            .Select(r => r.Value ?? 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static IReadOnlyList<UpstreamBranchProtection> GroupRestrictions(
        IReadOnlyList<BitbucketBranchRestriction> restrictions)
        => restrictions
            .Where(r => !string.IsNullOrWhiteSpace(r.Pattern))
            .GroupBy(r => r.Pattern!)
            .Select(g => new UpstreamBranchProtection(
                g.Key,
                g.Where(r => string.Equals(r.Kind, "require_approvals_to_merge", StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Value ?? 0)
                    .DefaultIfEmpty(0)
                    .Max(),
                g.Any(r => string.Equals(r.Kind, "require_passing_builds_to_merge", StringComparison.OrdinalIgnoreCase))))
            .ToList();

    private static bool MatchesPattern(string? pattern, string branch)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;
        if (!pattern.Contains('*'))
            return string.Equals(pattern, branch, StringComparison.Ordinal);
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(branch, regex, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    private static UpstreamReviewVerdict MapParticipantVerdict(BitbucketParticipant participant)
    {
        if (participant.Approved)
            return UpstreamReviewVerdict.Approved;
        if (string.Equals(participant.State, "changes_requested", StringComparison.OrdinalIgnoreCase))
            return UpstreamReviewVerdict.ChangesRequested;
        if (string.Equals(participant.State, "approved", StringComparison.OrdinalIgnoreCase))
            return UpstreamReviewVerdict.Approved;
        if (participant.ParticipatedOn.HasValue)
            return UpstreamReviewVerdict.Commented;
        return UpstreamReviewVerdict.Pending;
    }

    private static UpstreamCheckState MapBuildState(string? state)
        => state?.ToUpperInvariant() switch
        {
            "SUCCESSFUL" => UpstreamCheckState.Passing,
            "INPROGRESS" => UpstreamCheckState.Pending,
            "FAILED" => UpstreamCheckState.Failing,
            "STOPPED" => UpstreamCheckState.Cancelled,
            "SKIPPED" => UpstreamCheckState.Skipped,
            _ => UpstreamCheckState.Neutral,
        };

    private async Task<List<T>> GetPagedAsync<T>(
        BitbucketEndpoint endpoint, string relativePath, CancellationToken ct)
    {
        var all = new List<T>();
        var separator = relativePath.Contains('?') ? "&" : "?";
        var url = $"{endpoint.ApiBase}/{endpoint.RepoPath}/{relativePath}{separator}pagelen={endpoint.PageSize}&page=1";
        var client = _httpClientFactory.CreateClient("bitbucket-upstream");

        for (var page = 1; page <= endpoint.MaxPages; page++)
        {
            using var response = await SendWithUrlAsync(endpoint, client, HttpMethod.Get, url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new BitbucketUpstreamException(
                    $"Bitbucket list '{relativePath}' returned 404.", HttpStatusCode.NotFound);
            await ThrowIfNotSuccessAsync(endpoint, response, $"GET {relativePath}", ct);
            var envelope = await response.Content.ReadFromJsonAsync<BitbucketPaged<T>>(cancellationToken: ct);
            var items = envelope?.Values ?? [];
            all.AddRange(items);
            if (string.IsNullOrWhiteSpace(envelope?.Next))
                return all;
            url = envelope.Next;
        }

        throw new BitbucketUpstreamException(
            $"Bitbucket list '{relativePath}' exceeded {endpoint.MaxPages} pages; refusing to return a partial list.");
    }

    private async Task<HttpResponseMessage> SendAsync(
        BitbucketEndpoint endpoint, HttpMethod method, string relativePath, CancellationToken ct,
        Func<HttpContent>? contentFactory = null)
    {
        var url = relativePath.Length == 0
            ? $"{endpoint.ApiBase}/{endpoint.RepoPath}"
            : $"{endpoint.ApiBase}/{endpoint.RepoPath}/{relativePath}";
        var client = _httpClientFactory.CreateClient("bitbucket-upstream");
        return await SendWithUrlAsync(endpoint, client, method, url, ct, contentFactory);
    }

    private async Task<HttpResponseMessage> SendWithUrlAsync(
        BitbucketEndpoint endpoint, HttpClient client, HttpMethod method, string url,
        CancellationToken ct, Func<HttpContent>? contentFactory = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            endpoint.ApplyAuth(request);
            request.Headers.UserAgent.ParseAdd("CodeyBox-BitbucketUpstream/1.0");
            if (contentFactory is not null && method != HttpMethod.Get && method != HttpMethod.Delete)
                request.Content = contentFactory();

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new BitbucketUpstreamException(
                    $"Bitbucket request failed (unreachable): {endpoint.Scrub(ex.Message)}", ex);
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new BitbucketUpstreamException("Bitbucket request timed out.", ex);
            }

            if (response.StatusCode != (HttpStatusCode)429 || attempt > MaxRateLimitRetries)
                return response;

            response.Dispose();
            var delay = RetryDelay(response, attempt);
            _host.Logger.LogWarning(
                "Bitbucket rate-limited the request; retrying in {Delay}s (attempt {Attempt})",
                delay.TotalSeconds, attempt);
            await Task.Delay(delay, ct);
        }
    }

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
        BitbucketEndpoint endpoint, HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await ReadErrorBodyAsync(response, ct);
        throw new BitbucketUpstreamException(
            $"Bitbucket {operation} failed ({(int)response.StatusCode}): {endpoint.Scrub(detail)}",
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

using System.Globalization;
using System.Net;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.GitLabUpstreamPlugin;

// Extended IUpstreamRemote surfaces genuinely supported by GitLab REST API
// v4: approval-based review state, pipeline/commit check results, merge
// request discussions and notes, project/group/system webhooks, and project
// metadata. Capability stays discoverable: unsupported returns null (or
// null-vs-empty per the contract), "supported and empty" returns a non-null
// empty result. Anything GitLab cannot represent stays unimplemented rather
// than translated into another forge's shape: user-scoped webhooks (GitLab
// has no user-level hooks) and releases/tags (left on the contract default;
// see README.md).
public sealed partial class GitLabUpstreamRemote
{
    /// <summary>
    /// Open MRs whose source branch starts with <paramref name="branchPrefix"/>
    /// and whose mergeability GitLab has computed. MRs with an unknown merge
    /// status are skipped so the sweeper reconsiders them next tick.
    /// Empty when the plugin has no scoped project or nothing matches.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamPullRequest>> ListOpenPullRequestsAsync(
        string branchPrefix, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(branchPrefix))
            throw new ArgumentException("branchPrefix must be non-empty", nameof(branchPrefix));
        var config = ResolveScopedConfig();
        if (config is null)
            return [];
        var token = ResolveToken(null);

        var mrs = await GetPagedAsync<GitLabMergeRequest>(
            config, token, config.ProjectPath("merge_requests?state=opened"), ct);
        var result = new List<UpstreamPullRequest>();
        // Resolved lazily: only MRs without a forge-supplied URL need it,
        // and path-based projects never touch the network for it.
        string? webPath = null;
        foreach (var mr in mrs)
        {
            if (mr.Iid <= 0 || mr.Iid > int.MaxValue)
                continue;
            if (string.IsNullOrEmpty(mr.SourceBranch)
                || !mr.SourceBranch.StartsWith(branchPrefix, StringComparison.Ordinal))
                continue;
            if (!IsMergeStatusKnown(mr.DetailedMergeStatus))
                continue;
            if (string.IsNullOrEmpty(mr.Sha) || string.IsNullOrEmpty(mr.TargetBranch))
                continue;
            var url = mr.WebUrl;
            if (url is null)
            {
                webPath ??= await GetProjectWebPathAsync(config, token, ct);
                url = MergeRequestUrl(config, webPath, mr.Iid);
            }
            result.Add(new UpstreamPullRequest
            {
                Number = (int)mr.Iid,
                Url = url,
                HeadBranch = mr.SourceBranch,
                HeadSha = mr.Sha,
                BaseBranch = mr.TargetBranch,
                HasMergeConflict = IsMergeConflict(mr.DetailedMergeStatus),
            });
        }
        return result;
    }

    /// <summary>
    /// Reads an MR by iid. Null when the plugin has no scoped project or the
    /// MR is unavailable. GitLab reports <c>merged</c> explicitly, so merged
    /// MRs are not misread as merely closed.
    /// </summary>
    public async Task<UpstreamPullRequestState?> GetPullRequestAsync(
        int number, CancellationToken ct = default)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var mr = await GetMergeRequestAsync(config, ResolveToken(null), number, ct);
        if (mr is null)
            return null;
        var url = mr.WebUrl;
        if (url is null)
            url = MergeRequestUrl(config, await GetProjectWebPathAsync(config, ResolveToken(null), ct), mr.Iid);
        var status = string.Equals(mr.State, "merged", StringComparison.OrdinalIgnoreCase)
            ? PullRequestStatus.Merged
            : string.Equals(mr.State, "closed", StringComparison.OrdinalIgnoreCase)
                ? PullRequestStatus.Closed
                : PullRequestStatus.Open;
        return new UpstreamPullRequestState(
            (int)mr.Iid,
            url,
            status,
            mr.EffectiveMergeSha);
    }

    /// <summary>
    /// Review state from the MR approvals endpoint plus the approval rules:
    /// individual approvals, the rule names still outstanding, the quorum,
    /// and whether GitLab itself considers the MR approved. GitLab approvals
    /// are not GitHub reviews — approvals can be rule-based with required
    /// counts and eligible approvers, so rule names appear in
    /// <see cref="UpstreamReviewState.RequiredReviewers"/> and the quorum in
    /// <see cref="UpstreamReviewState.RequiredApprovalCount"/>.
    /// </summary>
    public async Task<UpstreamReviewState?> GetReviewStateAsync(
        int number, CancellationToken ct = default)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        GitLabApprovals approvals;
        using (var response = await SendGitLabAsync(
                   config, HttpMethod.Get, config.ProjectPath($"merge_requests/{number}/approvals"), token, ct))
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(response, "read merge request approvals", ct);
            approvals = await DeserializeAsync<GitLabApprovals>(response, ct)
                ?? throw new GitLabUpstreamException("GitLab returned an unusable approvals object.");
        }

        var rules = await GetApprovalRulesAsync(config, token, number, ct);

        var reviews = (approvals.ApprovedBy ?? [])
            .Select(a => new UpstreamReview
            {
                Reviewer = a.User?.DisplayName ?? "unknown",
                Verdict = UpstreamReviewVerdict.Approved,
                SubmittedAt = null,
            })
            .ToList();

        var requiredReviewers = rules
            .Where(r => !r.Approved && !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => r.Name!)
            .ToList();

        return new UpstreamReviewState
        {
            Reviews = reviews,
            RequiredReviewers = requiredReviewers,
            RequiredApprovalCount = Math.Max(0, approvals.ApprovalsRequired),
            RequirementsMet = approvals.Approved,
        };
    }

    private async Task<IReadOnlyList<GitLabApprovalRule>> GetApprovalRulesAsync(
        GitLabEndpointConfig config, string? token, long iid, CancellationToken ct)
    {
        // approval_state carries per-rule approved flags; fall back to the
        // plain rules list on instances without it.
        using (var state = await SendGitLabAsync(
                   config, HttpMethod.Get, config.ProjectPath($"merge_requests/{iid}/approval_state"), token, ct))
        {
            if (state.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(state, "read merge request approval state", ct);
                var wrapper = await DeserializeAsync<GitLabApprovalStateWrapper>(state, ct);
                if (wrapper?.ApprovalState?.Rules is { } stateRules)
                    return stateRules;
            }
        }

        using var rules = await SendGitLabAsync(
            config, HttpMethod.Get, config.ProjectPath($"merge_requests/{iid}/approval_rules"), token, ct);
        if (rules.StatusCode == HttpStatusCode.NotFound)
            return [];
        await EnsureSuccessAsync(rules, "list merge request approval rules", ct);
        return await DeserializeAsync<IReadOnlyList<GitLabApprovalRule>>(rules, ct) ?? [];
    }

    /// <summary>
    /// Check results for a head sha from the latest pipeline for that sha
    /// (jobs are the individual checks) plus the commit statuses. An empty
    /// <c>Checks</c> with <c>RequiredChecksPassed</c> means the forge
    /// requires nothing for this sha. Null when the sha is unknown to the
    /// forge.
    /// </summary>
    public async Task<UpstreamCheckSummary?> GetCheckResultsAsync(
        string headSha, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(headSha))
            throw new ArgumentException("headSha must be non-empty", nameof(headSha));
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);
        var sha = Uri.EscapeDataString(headSha.Trim());

        // Commit statuses first: a 404 here means the sha itself is unknown,
        // which is "unavailable" (null), not infrastructure.
        IReadOnlyList<GitLabCommitStatus> statuses;
        using (var statusesResponse = await SendGitLabAsync(
                   config, HttpMethod.Get, config.ProjectPath($"repository/commits/{sha}/statuses"), token, ct))
        {
            if (statusesResponse.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(statusesResponse, "read commit statuses", ct);
            statuses = await DeserializeAsync<IReadOnlyList<GitLabCommitStatus>>(statusesResponse, ct) ?? [];
        }

        IReadOnlyList<GitLabPipeline> pipelines;
        using (var pipelinesResponse = await SendGitLabAsync(
                   config, HttpMethod.Get, config.ProjectPath($"pipelines?sha={sha}"), token, ct))
        {
            if (pipelinesResponse.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(pipelinesResponse, "list pipelines for sha", ct);
            pipelines = await DeserializeAsync<IReadOnlyList<GitLabPipeline>>(pipelinesResponse, ct) ?? [];
        }

        var checks = statuses
            .Select(s => new UpstreamCheckResult
            {
                Name = string.IsNullOrWhiteSpace(s.Name) ? "unknown" : s.Name,
                State = MapCheckState(s.Status),
                DetailsUrl = string.IsNullOrWhiteSpace(s.TargetUrl) ? null : s.TargetUrl,
                Description = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description,
            })
            .ToList();

        var latest = pipelines.Count == 0 ? null : pipelines[0];
        if (latest is not null)
        {
            var jobs = await GetPagedAsync<GitLabJob>(
                config, token, config.ProjectPath($"pipelines/{latest.Id}/jobs"), ct);
            foreach (var job in jobs)
            {
                checks.Add(new UpstreamCheckResult
                {
                    Name = string.IsNullOrWhiteSpace(job.Name) ? "unknown" : job.Name,
                    State = MapCheckState(job.Status),
                    DetailsUrl = string.IsNullOrWhiteSpace(job.WebUrl) ? null : job.WebUrl,
                    Description = string.IsNullOrWhiteSpace(job.Stage) ? null : $"stage: {job.Stage}",
                });
            }

            if (jobs.Count == 0 && checks.Count == 0)
            {
                checks.Add(new UpstreamCheckResult
                {
                    Name = "pipeline",
                    State = MapCheckState(latest.Status),
                    DetailsUrl = string.IsNullOrWhiteSpace(latest.WebUrl) ? null : latest.WebUrl,
                });
            }
        }

        var requiredPassed = latest is not null
            ? string.Equals(latest.Status, "success", StringComparison.OrdinalIgnoreCase)
            : checks.All(c => c.State != UpstreamCheckState.Failing);

        return new UpstreamCheckSummary { Checks = checks, RequiredChecksPassed = requiredPassed };
    }

    /// <summary>
    /// Discussion notes on the MR, oldest first. System notes (pushes,
    /// merges, status changes) are filtered out; only human discussion
    /// remains. Code-anchored threads carry <c>FilePath</c> and <c>Line</c>
    /// from the note position; GitLab calls these discussions, the contract
    /// deliberately uses the neutral noun "comment".
    /// </summary>
    public async Task<IReadOnlyList<UpstreamComment>?> ListCommentsAsync(
        int number, CancellationToken ct = default)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        IReadOnlyList<GitLabNote> notes;
        try
        {
            notes = await GetPagedAsync<GitLabNote>(
                config, token, config.ProjectPath($"merge_requests/{number}/notes?sort=asc&order_by=created_at"), ct);
        }
        catch (GitLabUpstreamException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return notes
            .Where(n => !n.System)
            .Select(n => new UpstreamComment
            {
                Id = n.Id.ToString(CultureInfo.InvariantCulture),
                Author = n.Author?.DisplayName ?? "unknown",
                Body = n.Body ?? string.Empty,
                FilePath = n.Position?.AnchorPath,
                // A line without a path is not an anchor: report plain.
                Line = n.Position?.AnchorPath is null ? null : n.Position.AnchorLine,
                CreatedAt = n.CreatedAt,
            })
            .ToList();
    }

    /// <summary>
    /// Posts a comment on the MR: a plain top-level note, a reply inside an
    /// existing discussion (<see cref="NewUpstreamComment.ReplyToId"/> carries
    /// the GitLab discussion id), or a file-anchored review thread (the MR's
    /// diff refs supply the base/head SHAs GitLab requires for a positioned
    /// discussion). All three are genuine GitLab concepts.
    /// </summary>
    public async Task<UpstreamComment?> PostCommentAsync(
        int number, NewUpstreamComment comment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comment);
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        if (comment.ReplyToId is not null)
        {
            using var reply = await SendGitLabAsync(
                config, HttpMethod.Post,
                config.ProjectPath($"merge_requests/{number}/discussions/{Uri.EscapeDataString(comment.ReplyToId)}/notes"),
                token, ct,
                new { body = comment.Body });
            if (reply.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(reply, "reply to discussion", ct);
            var created = await DeserializeAsync<GitLabNote>(reply, ct);
            if (created is null)
                throw new GitLabUpstreamException("GitLab returned an unusable note after replying.");
            return new UpstreamComment
            {
                Id = created.Id.ToString(CultureInfo.InvariantCulture),
                Author = created.Author?.DisplayName ?? "unknown",
                Body = created.Body ?? comment.Body,
                FilePath = created.Position?.AnchorPath,
                Line = created.Position?.AnchorLine,
                CreatedAt = created.CreatedAt,
            };
        }

        if (comment.FilePath is not null)
        {
            if (comment.Line is null)
            {
                // GitLab positioned discussions anchor to a diff line; a
                // file-level thread has no GitLab equivalent on this
                // endpoint, so decline rather than mislabel it.
                _host.Logger.LogInformation(
                    "GitLab: file-level (lineless) discussions are not supported; declining post on MR !{Number}",
                    number);
                return null;
            }

            var mr = await GetMergeRequestAsync(config, token, number, ct);
            if (mr?.DiffRefs is not { BaseSha: { } baseSha, HeadSha: { } headSha, StartSha: { } startSha }
                || string.IsNullOrWhiteSpace(baseSha)
                || string.IsNullOrWhiteSpace(headSha)
                || string.IsNullOrWhiteSpace(startSha))
            {
                _host.Logger.LogInformation(
                    "GitLab: MR !{Number} exposes no diff refs; declining file-anchored comment",
                    number);
                return null;
            }

            using var discussion = await SendGitLabAsync(
                config, HttpMethod.Post, config.ProjectPath($"merge_requests/{number}/discussions"), token, ct,
                new
                {
                    body = comment.Body,
                    position = new
                    {
                        position_type = "text",
                        base_sha = baseSha,
                        head_sha = headSha,
                        start_sha = startSha,
                        new_path = comment.FilePath,
                        new_line = comment.Line,
                    },
                });
            if (discussion.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(discussion, "create discussion", ct);
            var created = await DeserializeAsync<GitLabDiscussion>(discussion, ct);
            var note = created?.Notes?.FirstOrDefault();
            if (note is null)
                throw new GitLabUpstreamException("GitLab returned an unusable discussion after posting.");
            return new UpstreamComment
            {
                Id = note.Id.ToString(CultureInfo.InvariantCulture),
                Author = note.Author?.DisplayName ?? "unknown",
                Body = note.Body ?? comment.Body,
                FilePath = comment.FilePath,
                Line = comment.Line,
                CreatedAt = note.CreatedAt,
            };
        }

        using var response = await SendGitLabAsync(
            config, HttpMethod.Post, config.ProjectPath($"merge_requests/{number}/notes"), token, ct,
            new { body = comment.Body });
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "post comment", ct);
        var plain = await DeserializeAsync<GitLabNote>(response, ct);
        if (plain is null)
            throw new GitLabUpstreamException("GitLab returned an unusable note after posting.");
        return new UpstreamComment
        {
            Id = plain.Id.ToString(CultureInfo.InvariantCulture),
            Author = plain.Author?.DisplayName ?? "unknown",
            Body = plain.Body ?? comment.Body,
            CreatedAt = plain.CreatedAt,
        };
    }

    /// <summary>
    /// Webhook subscriptions at the requested scope. Scope travels as an
    /// opaque string: <c>repository</c> maps to project hooks,
    /// <c>organization</c> to group hooks (resolved through the project's
    /// namespace; null when the project lives in a user namespace),
    /// <c>system</c> to instance system hooks. GitLab has no user-level
    /// hooks, so <c>user</c> — and any unknown scope — returns null
    /// (unsupported) rather than a coerced subscription.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(
        CancellationToken ct = default)
        => await ListWebhookSubscriptionsAsync(UpstreamWebhookScopes.Repository, ct);

    /// <summary>
    /// Lists webhook subscriptions for an explicit scope. Kept separate from
    /// the contract member so the scope mapping stays testable without
    /// ambient state.
    /// </summary>
    internal async Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(
        string scope, CancellationToken ct = default)
    {
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);
        var path = await ResolveHookPathAsync(config, token, scope, ct);
        if (path is null)
            return null;

        using var response = await SendGitLabAsync(config, HttpMethod.Get, path, token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "list webhook subscriptions", ct);
        var hooks = await DeserializeAsync<IReadOnlyList<GitLabHook>>(response, ct) ?? [];

        var result = new List<UpstreamWebhookSubscription>();
        foreach (var hook in hooks)
        {
            if (string.IsNullOrWhiteSpace(hook.Url))
            {
                _host.Logger.LogDebug("GitLab: skipping hook id {Id} without a delivery URL", hook.Id);
                continue;
            }
            result.Add(new UpstreamWebhookSubscription
            {
                Id = hook.Id.ToString(CultureInfo.InvariantCulture),
                Scope = scope,
                Events = hook.EnabledEvents,
                TargetUrl = hook.Url,
            });
        }
        return result;
    }

    /// <summary>
    /// Creates a webhook subscription at the requested scope. Event names are
    /// GitLab's native trigger names (see <see cref="TryMapHookEvents"/>);
    /// names with no GitLab equivalent return null (unsupported) rather than
    /// a subscription that silently drops them. The contract carries no
    /// secret input, so hooks are created without a secret token — front the
    /// target URL accordingly.
    /// </summary>
    public async Task<UpstreamWebhookSubscription?> CreateWebhookSubscriptionAsync(
        NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);
        var path = await ResolveHookPathAsync(config, token, subscription.Scope, ct);
        if (path is null)
        {
            _host.Logger.LogInformation(
                "GitLab: webhook scope '{Scope}' is not supported",
                subscription.Scope);
            return null;
        }
        if (!TryMapHookEvents(subscription.Events, out var flags))
        {
            _host.Logger.LogInformation(
                "GitLab: webhook event set has no GitLab equivalent; declining creation");
            return null;
        }

        using var response = await SendGitLabAsync(
            config, HttpMethod.Post, path, token, ct,
            new Dictionary<string, object>
            {
                ["url"] = subscription.TargetUrl,
                ["enable_ssl_verification"] = true,
                ["push_events"] = flags.Contains("push"),
                ["tag_push_events"] = flags.Contains("tag_push"),
                ["merge_requests_events"] = flags.Contains("merge_request"),
                ["pipeline_events"] = flags.Contains("pipeline"),
                ["job_events"] = flags.Contains("job"),
                ["note_events"] = flags.Contains("note"),
                ["confidential_note_events"] = flags.Contains("confidential_note"),
                ["issues_events"] = flags.Contains("issues"),
                ["confidential_issues_events"] = flags.Contains("confidential_issues"),
                ["wiki_page_events"] = flags.Contains("wiki_page"),
                ["deployment_events"] = flags.Contains("deployment"),
                ["member_events"] = flags.Contains("member"),
                ["subgroup_events"] = flags.Contains("subgroup"),
                ["feature_flag_events"] = flags.Contains("feature_flag"),
                ["release_events"] = flags.Contains("release"),
            });
        await EnsureSuccessAsync(response, "create webhook subscription", ct);
        var created = await DeserializeAsync<GitLabHook>(response, ct);
        if (created is null || string.IsNullOrWhiteSpace(created.Url))
            throw new GitLabUpstreamException("GitLab returned an unusable hook after creation.");
        return new UpstreamWebhookSubscription
        {
            Id = created.Id.ToString(CultureInfo.InvariantCulture),
            Scope = subscription.Scope,
            Events = created.EnabledEvents,
            TargetUrl = created.Url,
        };
    }

    /// <summary>
    /// Deletes a webhook subscription at the repository (project) scope. Null
    /// when the plugin has no scoped project, true when removed, false when
    /// the id is unknown.
    /// </summary>
    public async Task<bool?> DeleteWebhookSubscriptionAsync(
        string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id must be non-empty", nameof(id));
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        using var response = await SendGitLabAsync(
            config, HttpMethod.Delete,
            config.ProjectPath($"hooks/{Uri.EscapeDataString(id.Trim())}"), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, "delete webhook subscription", ct);
        return true;
    }

    private async Task<string?> ResolveHookPathAsync(
        GitLabEndpointConfig config, string? token, string scope, CancellationToken ct)
    {
        if (string.Equals(scope, UpstreamWebhookScopes.Repository, StringComparison.OrdinalIgnoreCase))
            return config.ProjectPath("hooks");

        if (string.Equals(scope, UpstreamWebhookScopes.Organization, StringComparison.OrdinalIgnoreCase))
        {
            var groupId = await ResolveGroupIdAsync(config, token, ct);
            if (groupId is null)
            {
                _host.Logger.LogInformation(
                    "GitLab: project has no group namespace; declining organization-scoped webhook request");
                return null;
            }
            return $"groups/{groupId}/hooks";
        }

        if (string.Equals(scope, UpstreamWebhookScopes.System, StringComparison.OrdinalIgnoreCase))
            return "hooks";

        return null;
    }

    private async Task<long?> ResolveGroupIdAsync(
        GitLabEndpointConfig config, string? token, CancellationToken ct)
    {
        using var response = await SendGitLabAsync(
            config, HttpMethod.Get, config.ProjectPath(string.Empty).TrimEnd('/'), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "read project", ct);
        var project = await DeserializeAsync<GitLabProject>(response, ct);
        if (project?.Namespace is { Kind: "group", Id: > 0 })
            return project.Namespace.Id;
        return null;
    }

    /// <summary>
    /// Default branch, visibility and branch protection rules for the scoped
    /// project. Protection rules degrade honestly: instances without the
    /// endpoint (404) yield metadata with no rules, not an error.
    /// </summary>
    public async Task<UpstreamRepositoryMetadata?> GetRepositoryMetadataAsync(
        CancellationToken ct = default)
    {
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        GitLabProject project;
        using (var projectResponse = await SendGitLabAsync(
                   config, HttpMethod.Get, config.ProjectPath(string.Empty).TrimEnd('/'), token, ct))
        {
            if (projectResponse.StatusCode == HttpStatusCode.NotFound)
                return null;
            await EnsureSuccessAsync(projectResponse, "read project", ct);
            project = await DeserializeAsync<GitLabProject>(projectResponse, ct)
                ?? throw new GitLabUpstreamException("GitLab returned an unusable project object.");
        }

        var protections = new List<UpstreamBranchProtection>();
        using (var branchesResponse = await SendGitLabAsync(
                   config, HttpMethod.Get, config.ProjectPath("protected_branches"), token, ct))
        {
            if (branchesResponse.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(branchesResponse, "list protected branches", ct);
                var branches = await DeserializeAsync<IReadOnlyList<GitLabProtectedBranch>>(
                    branchesResponse, ct) ?? [];
                var quorum = await GetApprovalsBeforeMergeAsync(config, token, ct);
                foreach (var branch in branches)
                {
                    if (string.IsNullOrWhiteSpace(branch.Name))
                        continue;
                    protections.Add(new UpstreamBranchProtection(
                        branch.Name,
                        quorum,
                        project.OnlyAllowMergeIfPipelineSucceeds));
                }
            }
            else
            {
                _host.Logger.LogDebug(
                    "GitLab: protected_branches endpoint unavailable; returning metadata without rules");
            }
        }

        return new UpstreamRepositoryMetadata
        {
            DefaultBranch = string.IsNullOrWhiteSpace(project.DefaultBranch) ? null : project.DefaultBranch,
            Visibility = MapVisibility(project.Visibility),
            BranchProtections = protections,
        };
    }

    private async Task<int> GetApprovalsBeforeMergeAsync(
        GitLabEndpointConfig config, string? token, CancellationToken ct)
    {
        using var response = await SendGitLabAsync(
            config, HttpMethod.Get, config.ProjectPath("approvals"), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return 0;
        await EnsureSuccessAsync(response, "read project approvals", ct);
        var approvals = await DeserializeAsync<GitLabProjectApprovals>(response, ct);
        return Math.Max(0, approvals?.ApprovalsBeforeMerge ?? 0);
    }

    // ------------------------------------------------------------------
    // GitLab-to-contract mappings (pure)
    // ------------------------------------------------------------------

    internal static bool IsMergeStatusKnown(string? detailedMergeStatus) =>
        !string.IsNullOrWhiteSpace(detailedMergeStatus)
        && !string.Equals(detailedMergeStatus, "checking", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(detailedMergeStatus, "unknown", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMergeConflict(string? detailedMergeStatus) =>
        string.Equals(detailedMergeStatus, "conflict", StringComparison.OrdinalIgnoreCase)
        || string.Equals(detailedMergeStatus, "need_rebase", StringComparison.OrdinalIgnoreCase);

    internal static UpstreamCheckState MapCheckState(string? status) =>
        status?.ToLowerInvariant() switch
        {
            "success" => UpstreamCheckState.Passing,
            "failed" => UpstreamCheckState.Failing,
            "canceled" => UpstreamCheckState.Cancelled,
            "skipped" => UpstreamCheckState.Skipped,
            "manual" => UpstreamCheckState.Neutral,
            "created" or "waiting_for_resource" or "preparing" or "pending" or "running" or "scheduled"
                => UpstreamCheckState.Pending,
            // Unknown strings carry no outcome: pending, not neutral.
            _ => UpstreamCheckState.Pending,
        };

    internal static string? MapVisibility(string? visibility) =>
        visibility?.ToLowerInvariant() switch
        {
            "public" => UpstreamRepositoryVisibility.Public,
            "internal" => UpstreamRepositoryVisibility.Internal,
            "private" => UpstreamRepositoryVisibility.Private,
            _ => null,
        };

    internal static bool TryMapHookEvents(IReadOnlyList<string> events, out HashSet<string> flags)
    {
        flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in events)
        {
            var flag = name.Trim().ToLowerInvariant() switch
            {
                "push" or "push_events" => "push",
                "tag_push" or "tag_push_events" => "tag_push",
                "merge_request" or "merge_requests_events" => "merge_request",
                "pipeline" or "pipeline_events" => "pipeline",
                "job" or "job_events" or "build" => "job",
                "note" or "note_events" or "comment" => "note",
                "confidential_note" or "confidential_note_events" => "confidential_note",
                "issue" or "issues" or "issues_events" => "issues",
                "confidential_issues" or "confidential_issues_events" => "confidential_issues",
                "wiki_page" or "wiki_page_events" => "wiki_page",
                "deployment" or "deployment_events" => "deployment",
                "member" or "member_events" => "member",
                "subgroup" or "subgroup_events" => "subgroup",
                "feature_flag" or "feature_flag_events" => "feature_flag",
                "release" or "release_events" => "release",
                _ => (string?)null,
            };
            if (flag is null)
            {
                flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return false;
            }
            flags.Add(flag);
        }
        return true;
    }
}

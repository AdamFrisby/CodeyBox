using System.Globalization;
using System.Net;
using CodeyBox.Core;
using Microsoft.Extensions.Logging;

namespace CodeyBox.ForgejoUpstreamPlugin;

// Extended IUpstreamRemote surfaces genuinely supported by Forgejo API v1:
// review state, commit checks, plain issue comments, repository webhooks and
// repository metadata. Capability stays discoverable: unsupported returns
// null (or null-vs-empty per the contract), "supported and empty" returns a
// non-null empty result. Anything Forgejo cannot represent stays
// unimplemented rather than translated into another forge's shape:
// file-anchored and threaded comments, non-repository webhook scopes, and
// releases (see README.md).
public sealed partial class ForgejoUpstreamRemote
{
    /// <summary>
    /// Open PRs whose head branch starts with <paramref name="branchPrefix"/>
    /// and whose mergeability Forgejo has computed. PRs with unknown
    /// mergeability are skipped so the sweeper reconsiders them next tick.
    /// Empty when the plugin has no scoped repository or nothing matches.
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

        var pulls = await GetPagedAsync<ForgejoPull>(config, token, "pulls?state=open", ct);
        var result = new List<UpstreamPullRequest>();
        foreach (var pull in pulls)
        {
            if (pull.EffectiveNumber <= 0 || pull.EffectiveNumber > int.MaxValue)
                continue;
            if (string.Equals(pull.State, "closed", StringComparison.OrdinalIgnoreCase))
                continue;
            var headBranch = BranchNameOf(pull.Head);
            if (headBranch is null || !headBranch.StartsWith(branchPrefix, StringComparison.Ordinal))
                continue;
            if (pull.Mergeable is null)
                continue;
            var headSha = pull.Head?.Sha;
            var baseBranch = BranchNameOf(pull.Base);
            if (string.IsNullOrEmpty(headSha) || string.IsNullOrEmpty(baseBranch))
                continue;
            result.Add(new UpstreamPullRequest
            {
                Number = (int)pull.EffectiveNumber,
                Url = pull.HtmlUrl ?? config.PullUrl(pull.EffectiveNumber),
                HeadBranch = headBranch,
                HeadSha = headSha,
                BaseBranch = baseBranch,
                HasMergeConflict = pull.Mergeable == false,
            });
        }
        return result;
    }

    /// <summary>
    /// Reads a PR by number. Null when the plugin has no scoped repository
    /// or the PR is unavailable. Forgejo reports <c>merged</c> explicitly,
    /// so merged PRs are not misread as merely closed.
    /// </summary>
    public async Task<UpstreamPullRequestState?> GetPullRequestAsync(
        int number, CancellationToken ct = default)
    {
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var pull = await GetPullAsync(config, ResolveToken(null), number, ct);
        if (pull is null)
            return null;
        var status = pull.Merged ? PullRequestStatus.Merged
            : string.Equals(pull.State, "closed", StringComparison.OrdinalIgnoreCase) ? PullRequestStatus.Closed
            : PullRequestStatus.Open;
        return new UpstreamPullRequestState(
            (int)pull.EffectiveNumber,
            pull.HtmlUrl ?? config.PullUrl(pull.EffectiveNumber),
            status,
            pull.MergeCommitSha);
    }

    /// <summary>
    /// Review state from <c>/pulls/{n}/reviews</c> plus the still-requested
    /// reviewers from the PR itself. <c>RequirementsMet</c> counts
    /// non-dismissed, non-stale approvals against the scoped repository's
    /// branch-protection quorum for the PR's base branch, and treats an
    /// outstanding change request as blocking — the common Forgejo
    /// configuration, documented in README.md.
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

        using var reviewsResponse = await SendForgejoAsync(
            HttpMethod.Get, config.ReposPath($"pulls/{number}/reviews"), token, ct);
        if (reviewsResponse.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(reviewsResponse, "list pull request reviews", ct);
        var forgeReviews = await DeserializeAsync<IReadOnlyList<ForgejoReview>>(reviewsResponse, ct) ?? [];

        var pull = await GetPullAsync(config, token, number, ct);
        if (pull is null)
            return null;

        var reviews = forgeReviews
            .Select(r => new UpstreamReview
            {
                Reviewer = r.User?.Login ?? "unknown",
                Verdict = MapReviewVerdict(r.State, r.Dismissed),
                SubmittedAt = r.SubmittedAt,
            })
            .ToList();

        var requiredReviewers = (pull.RequestedReviewers ?? [])
            .Select(u => u.Login)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .ToList();

        var baseBranch = BranchNameOf(pull.Base);
        var requiredApprovals = await GetRequiredApprovalsAsync(config, token, baseBranch, ct);

        var approvers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = false;
        foreach (var (review, verdict) in forgeReviews.Zip(reviews, (f, m) => (f, m.Verdict)))
        {
            if (review.Dismissed || verdict == UpstreamReviewVerdict.Dismissed)
                continue;
            // A stale approval predates the latest push; with
            // dismiss-stale-approvals protections it no longer counts.
            if (verdict == UpstreamReviewVerdict.Approved)
            {
                if (!review.Stale && review.User?.Login is { } login)
                    approvers.Add(login);
            }
            else if (verdict == UpstreamReviewVerdict.ChangesRequested)
            {
                blocked = true;
            }
        }

        return new UpstreamReviewState
        {
            Reviews = reviews,
            RequiredReviewers = requiredReviewers,
            RequiredApprovalCount = requiredApprovals,
            RequirementsMet = approvers.Count >= requiredApprovals && !blocked,
        };
    }

    /// <summary>
    /// Commit checks from <c>/commits/{sha}/status</c> (combined) with
    /// fallback to the <c>/statuses</c> list on instances without the
    /// combined endpoint. <c>RequiredChecksPassed</c> follows the forge's
    /// combined state when present; otherwise no failing check means pass.
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
        var encoded = Uri.EscapeDataString(headSha.Trim());

        IReadOnlyList<ForgejoCommitStatus> statuses;
        string? combinedState;
        using (var combined = await SendForgejoAsync(
                   HttpMethod.Get, config.ReposPath($"commits/{encoded}/status"), token, ct))
        {
            if (combined.StatusCode == HttpStatusCode.NotFound)
            {
                // Old instances lack the combined endpoint: fall back to the
                // statuses list. A 404 there means the sha itself is unknown,
                // which is "unavailable" (null), not infrastructure.
                try
                {
                    statuses = await GetPagedAsync<ForgejoCommitStatus>(
                        config, token, $"commits/{encoded}/statuses", ct);
                }
                catch (ForgejoUpstreamException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                combinedState = null;
            }
            else
            {
                await EnsureSuccessAsync(combined, "read combined commit status", ct);
                var body = await DeserializeAsync<ForgejoCombinedStatus>(combined, ct);
                statuses = body?.Statuses ?? [];
                combinedState = body?.State;
            }
        }
        var checks = statuses
            .Select(s => new UpstreamCheckResult
            {
                Name = string.IsNullOrWhiteSpace(s.Context) ? "unknown" : s.Context,
                State = MapCheckState(s.Status),
                DetailsUrl = string.IsNullOrWhiteSpace(s.TargetUrl) ? null : s.TargetUrl,
                Description = string.IsNullOrWhiteSpace(s.Description) ? null : s.Description,
            })
            .ToList();

        var requiredPassed = combinedState is not null
            ? string.Equals(combinedState, "success", StringComparison.OrdinalIgnoreCase)
            : checks.All(c => c.State != UpstreamCheckState.Failing);

        return new UpstreamCheckSummary { Checks = checks, RequiredChecksPassed = requiredPassed };
    }

    /// <summary>
    /// Plain issue comments on the PR (pulls and issues share Forgejo's
    /// numbering), oldest first via pagination. Forgejo has no code-anchored
    /// equivalent on this endpoint, so returned comments never carry a file
    /// or line — see <see cref="PostCommentAsync"/>.
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

        IReadOnlyList<ForgejoComment> comments;
        try
        {
            comments = await GetPagedAsync<ForgejoComment>(
                config, token, $"issues/{number}/comments", ct);
        }
        catch (ForgejoUpstreamException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return comments
            .Select(c => new UpstreamComment
            {
                Id = c.Id.ToString(CultureInfo.InvariantCulture),
                Author = c.User?.Login ?? "unknown",
                Body = c.Body ?? string.Empty,
                CreatedAt = c.CreatedAt,
            })
            .ToList();
    }

    /// <summary>
    /// Posts a plain top-level comment. File-anchored threads and replies
    /// have no Forgejo equivalent on this endpoint, so those shapes return
    /// null (unsupported) rather than being mislabelled as plain comments.
    /// </summary>
    public async Task<UpstreamComment?> PostCommentAsync(
        int number, NewUpstreamComment comment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comment);
        if (number <= 0)
            throw new ArgumentOutOfRangeException(nameof(number), "Pull request number must be positive.");
        if (comment.FilePath is not null || comment.Line is not null || comment.ReplyToId is not null)
        {
            _host.Logger.LogInformation(
                "Forgejo: file-anchored and threaded comments are not supported; declining post on PR #{Number}",
                number);
            return null;
        }
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        using var response = await SendForgejoAsync(
            HttpMethod.Post, config.ReposPath($"issues/{number}/comments"), token, ct,
            new { body = comment.Body });
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "post comment", ct);
        var created = await DeserializeAsync<ForgejoComment>(response, ct);
        if (created is null)
            throw new ForgejoUpstreamException("Forgejo returned an unusable comment after posting.");
        return new UpstreamComment
        {
            Id = created.Id.ToString(CultureInfo.InvariantCulture),
            Author = created.User?.Login ?? "unknown",
            Body = created.Body ?? comment.Body,
            CreatedAt = created.CreatedAt,
        };
    }

    /// <summary>
    /// Repository webhooks (<c>forgejo</c>-type hooks). Only the repository
    /// scope exists at this endpoint; other scopes are separate Forgejo
    /// resources an operator manages, not this provider.
    /// </summary>
    public async Task<IReadOnlyList<UpstreamWebhookSubscription>?> ListWebhookSubscriptionsAsync(
        CancellationToken ct = default)
    {
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        using var response = await SendForgejoAsync(HttpMethod.Get, config.ReposPath("hooks"), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, "list webhook subscriptions", ct);
        var hooks = await DeserializeAsync<IReadOnlyList<ForgejoHook>>(response, ct) ?? [];

        var result = new List<UpstreamWebhookSubscription>();
        foreach (var hook in hooks)
        {
            var target = hook.TargetUrl;
            if (string.IsNullOrWhiteSpace(target))
            {
                _host.Logger.LogDebug("Forgejo: skipping hook id {Id} without a delivery URL", hook.Id);
                continue;
            }
            result.Add(new UpstreamWebhookSubscription
            {
                Id = hook.Id.ToString(CultureInfo.InvariantCulture),
                Scope = UpstreamWebhookScopes.Repository,
                Events = hook.Events ?? [],
                TargetUrl = target,
            });
        }
        return result;
    }

    /// <summary>
    /// Creates a repository webhook. Scope travels as an opaque string; only
    /// <c>repository</c> maps to this endpoint, anything else returns null
    /// (unsupported) rather than being coerced. Event names are forge-native
    /// and passed through untouched.
    /// </summary>
    public async Task<UpstreamWebhookSubscription?> CreateWebhookSubscriptionAsync(
        NewUpstreamWebhookSubscription subscription, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (!string.Equals(subscription.Scope, UpstreamWebhookScopes.Repository, StringComparison.OrdinalIgnoreCase))
        {
            _host.Logger.LogInformation(
                "Forgejo: webhook scope '{Scope}' is not supported; only '{Repository}' is",
                subscription.Scope, UpstreamWebhookScopes.Repository);
            return null;
        }
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        using var response = await SendForgejoAsync(
            HttpMethod.Post, config.ReposPath("hooks"), token, ct,
            new
            {
                type = "forgejo",
                active = true,
                config = new { url = subscription.TargetUrl, content_type = "json" },
                events = subscription.Events,
            });
        await EnsureSuccessAsync(response, "create webhook subscription", ct);
        var created = await DeserializeAsync<ForgejoHook>(response, ct);
        var target = created?.TargetUrl;
        if (created is null || string.IsNullOrWhiteSpace(target))
            throw new ForgejoUpstreamException("Forgejo returned an unusable hook after creation.");
        return new UpstreamWebhookSubscription
        {
            Id = created.Id.ToString(CultureInfo.InvariantCulture),
            Scope = UpstreamWebhookScopes.Repository,
            Events = created.Events ?? subscription.Events,
            TargetUrl = target,
        };
    }

    /// <summary>
    /// Deletes a repository webhook. Null when the plugin has no scoped
    /// repository, true when removed, false when the id is unknown.
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

        using var response = await SendForgejoAsync(
            HttpMethod.Delete, config.ReposPath($"hooks/{Uri.EscapeDataString(id.Trim())}"), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(response, "delete webhook subscription", ct);
        return true;
    }

    /// <summary>
    /// Default branch, visibility and branch protection rules for the scoped
    /// repository. Protection rules degrade honestly: instances without the
    /// endpoint (404) yield metadata with no rules, not an error.
    /// </summary>
    public async Task<UpstreamRepositoryMetadata?> GetRepositoryMetadataAsync(
        CancellationToken ct = default)
    {
        var config = ResolveScopedConfig();
        if (config is null)
            return null;
        var token = ResolveToken(null);

        using var repoResponse = await SendForgejoAsync(HttpMethod.Get, config.ReposPath(string.Empty).TrimEnd('/'), token, ct);
        if (repoResponse.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(repoResponse, "read repository", ct);
        var repo = await DeserializeAsync<ForgejoRepository>(repoResponse, ct);
        if (repo is null)
            throw new ForgejoUpstreamException("Forgejo returned an unusable repository object.");

        var protections = new List<UpstreamBranchProtection>();
        using (var protectionsResponse = await SendForgejoAsync(
                   HttpMethod.Get, config.ReposPath("branch_protections"), token, ct))
        {
            if (protectionsResponse.StatusCode != HttpStatusCode.NotFound)
            {
                await EnsureSuccessAsync(protectionsResponse, "list branch protections", ct);
                var rules = await DeserializeAsync<IReadOnlyList<ForgejoBranchProtection>>(
                    protectionsResponse, ct) ?? [];
                foreach (var rule in rules)
                {
                    if (string.IsNullOrWhiteSpace(rule.Pattern))
                        continue;
                    protections.Add(new UpstreamBranchProtection(
                        rule.Pattern,
                        Math.Max(0, rule.RequiredApprovals),
                        rule.EnableStatusCheck));
                }
            }
            else
            {
                _host.Logger.LogDebug(
                    "Forgejo: branch_protections endpoint unavailable; returning metadata without rules");
            }
        }

        return new UpstreamRepositoryMetadata
        {
            DefaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? null : repo.DefaultBranch,
            Visibility = repo.Private ? UpstreamRepositoryVisibility.Private
                : repo.Internal ? UpstreamRepositoryVisibility.Internal
                : UpstreamRepositoryVisibility.Public,
            BranchProtections = protections,
        };
    }

    // ------------------------------------------------------------------
    // Forgejo-to-contract mappings (pure)
    // ------------------------------------------------------------------

    internal static string? BranchNameOf(ForgejoBranchInfo? branch)
    {
        if (branch is null)
            return null;
        if (!string.IsNullOrWhiteSpace(branch.Ref))
            return branch.Ref;
        // label is "owner:branch"; the suffix is the branch in this repo.
        if (!string.IsNullOrWhiteSpace(branch.Label))
        {
            var label = branch.Label;
            var colon = label.LastIndexOf(':');
            var name = colon >= 0 ? label[(colon + 1)..] : label;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        return null;
    }

    internal static UpstreamReviewVerdict MapReviewVerdict(string? state, bool dismissed)
    {
        if (dismissed)
            return UpstreamReviewVerdict.Dismissed;
        return state?.ToUpperInvariant() switch
        {
            "APPROVED" => UpstreamReviewVerdict.Approved,
            "CHANGES_REQUESTED" or "REQUEST_CHANGES" or "REJECTED" => UpstreamReviewVerdict.ChangesRequested,
            "COMMENT" or "COMMENTED" => UpstreamReviewVerdict.Commented,
            "PENDING" or "REQUEST_REVIEW" or null or "" => UpstreamReviewVerdict.Pending,
            // A review that neither approves nor blocks is surfaced as a
            // comment rather than dropped; the raw state is forge-native.
            _ => UpstreamReviewVerdict.Commented,
        };
    }

    internal static UpstreamCheckState MapCheckState(string? state) =>
        state?.ToLowerInvariant() switch
        {
            "success" => UpstreamCheckState.Passing,
            "pending" => UpstreamCheckState.Pending,
            "failure" or "error" => UpstreamCheckState.Failing,
            "warning" or "skipped" => UpstreamCheckState.Neutral,
            // Unknown strings carry no outcome: pending, not neutral.
            _ => UpstreamCheckState.Pending,
        };

    private async Task<int> GetRequiredApprovalsAsync(
        ForgejoEndpointConfig config, string? token, string? baseBranch, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(baseBranch))
            return 0;
        using var response = await SendForgejoAsync(
            HttpMethod.Get, config.ReposPath("branch_protections"), token, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return 0;
        await EnsureSuccessAsync(response, "list branch protections", ct);
        var rules = await DeserializeAsync<IReadOnlyList<ForgejoBranchProtection>>(response, ct) ?? [];
        var quorum = 0;
        foreach (var rule in rules)
        {
            if (rule.Pattern is null || !ProtectionMatchesBranch(rule.Pattern, baseBranch))
                continue;
            quorum = Math.Max(quorum, Math.Max(0, rule.RequiredApprovals));
        }
        return quorum;
    }

    internal static bool ProtectionMatchesBranch(string pattern, string branch)
    {
        if (string.Equals(pattern, branch, StringComparison.Ordinal))
            return true;
        // Forgejo rule names are usually exact; tolerate a trailing wildcard.
        if (pattern.EndsWith('*'))
            return branch.StartsWith(pattern[..^1], StringComparison.Ordinal);
        return false;
    }
}

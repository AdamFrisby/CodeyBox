using System.Text.Json.Serialization;

namespace CodeyBox.GiteaUpstreamPlugin;

/// <summary>
/// Minimal Gitea API v1 DTOs for the fields this provider reads. Shapes were
/// verified against the Gitea 1.22 swagger (<c>templates/swagger/v1_json.tmpl</c>):
/// property names below are the wire names Gitea actually emits
/// (<c>number</c> carries the PR index, <c>html_url</c> the web URL).
/// Unknown wire fields are ignored so older and newer instances both parse.
/// </summary>
internal sealed record GiteaPullRequest(
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("merged")] bool Merged,
    [property: JsonPropertyName("mergeable")] bool? Mergeable,
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha,
    [property: JsonPropertyName("head")] GiteaBranchInfo? Head,
    [property: JsonPropertyName("base")] GiteaBranchInfo? Base);

internal sealed record GiteaBranchInfo(
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("sha")] string? Sha);

internal sealed record GiteaPullReview(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("dismissed")] bool Dismissed,
    [property: JsonPropertyName("stale")] bool Stale,
    [property: JsonPropertyName("submitted_at")] DateTimeOffset? SubmittedAt,
    [property: JsonPropertyName("user")] GiteaUser? User);

internal sealed record GiteaReviewComment(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("position")] long? Position,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("user")] GiteaUser? User);

internal sealed record GiteaUser(
    [property: JsonPropertyName("login")] string? Login);

internal sealed record GiteaCommitStatus(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("target_url")] string? TargetUrl,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);

internal sealed record GiteaCombinedStatus(
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("sha")] string? Sha,
    [property: JsonPropertyName("statuses")] IReadOnlyList<GiteaCommitStatus>? Statuses);

internal sealed record GiteaIssueComment(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("user")] GiteaUser? User);

internal sealed record GiteaHook(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("events")] IReadOnlyList<string>? Events,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("config")] Dictionary<string, string>? Config);

internal sealed record GiteaRepository(
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("private")] bool Private,
    [property: JsonPropertyName("internal")] bool Internal,
    [property: JsonPropertyName("html_url")] string? HtmlUrl);

internal sealed record GiteaBranchProtection(
    [property: JsonPropertyName("rule_name")] string? RuleName,
    [property: JsonPropertyName("required_approvals")] long RequiredApprovals,
    [property: JsonPropertyName("enable_status_check")] bool EnableStatusCheck,
    [property: JsonPropertyName("status_check_contexts")] IReadOnlyList<string>? StatusCheckContexts,
    [property: JsonPropertyName("block_on_rejected_reviews")] bool BlockOnRejectedReviews);

internal sealed record GiteaRelease(
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("tag_name")] string? TagName);

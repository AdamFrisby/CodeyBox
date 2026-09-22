using System.Text.Json.Serialization;

namespace CodeyBox.ForgejoUpstreamPlugin;

// JSON shapes for Forgejo API v1, verified against the live swagger
// (https://try.next.forgejo.org/swagger.v1.json, Forgejo 15 / API v1).
// Only the fields this provider reads are modelled; unknown fields are
// ignored so newer instances stay compatible. All ids stay numeric here
// and are rendered as strings at the contract boundary (forges differ).

internal sealed record ForgejoUser(
    [property: JsonPropertyName("login")] string? Login);

internal sealed record ForgejoBranchInfo(
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("ref")] string? Ref,
    [property: JsonPropertyName("sha")] string? Sha);

internal sealed record ForgejoPull(
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("index")] long Index,
    [property: JsonPropertyName("html_url")] string? HtmlUrl,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("merged")] bool Merged,
    [property: JsonPropertyName("mergeable")] bool? Mergeable,
    [property: JsonPropertyName("merge_commit_sha")] string? MergeCommitSha,
    [property: JsonPropertyName("head")] ForgejoBranchInfo? Head,
    [property: JsonPropertyName("base")] ForgejoBranchInfo? Base,
    [property: JsonPropertyName("requested_reviewers")] IReadOnlyList<ForgejoUser>? RequestedReviewers)
{
    // Pulls and issues share one numbering space; either field identifies the PR.
    public long EffectiveNumber => Number > 0 ? Number : Index;
}

internal sealed record ForgejoReview(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("user")] ForgejoUser? User,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("submitted_at")] DateTimeOffset? SubmittedAt,
    [property: JsonPropertyName("dismissed")] bool Dismissed,
    [property: JsonPropertyName("stale")] bool Stale);

internal sealed record ForgejoCommitStatus(
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("target_url")] string? TargetUrl,
    [property: JsonPropertyName("description")] string? Description);

internal sealed record ForgejoCombinedStatus(
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("statuses")] IReadOnlyList<ForgejoCommitStatus>? Statuses);

internal sealed record ForgejoComment(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("user")] ForgejoUser? User,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt);

internal sealed record ForgejoHook(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("events")] IReadOnlyList<string>? Events,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("config")] Dictionary<string, string>? Config)
{
    public string? TargetUrl =>
        !string.IsNullOrWhiteSpace(Url) ? Url
        : Config is not null && Config.TryGetValue("url", out var u) && !string.IsNullOrWhiteSpace(u) ? u
        : null;
}

internal sealed record ForgejoRepository(
    [property: JsonPropertyName("default_branch")] string? DefaultBranch,
    [property: JsonPropertyName("private")] bool Private,
    [property: JsonPropertyName("internal")] bool Internal);

internal sealed record ForgejoBranchProtection(
    [property: JsonPropertyName("rule_name")] string? RuleName,
    [property: JsonPropertyName("branch_name")] string? BranchName,
    [property: JsonPropertyName("required_approvals")] int RequiredApprovals,
    [property: JsonPropertyName("enable_status_check")] bool EnableStatusCheck)
{
    // rule_name is current; branch_name is the deprecated equivalent kept for old instances.
    public string? Pattern => !string.IsNullOrWhiteSpace(RuleName) ? RuleName : BranchName;
}

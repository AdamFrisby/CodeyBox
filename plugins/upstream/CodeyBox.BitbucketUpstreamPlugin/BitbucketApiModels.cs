using System.Text.Json.Serialization;

namespace CodeyBox.BitbucketUpstreamPlugin;

/// <summary>
/// Minimal Bitbucket Cloud API 2.0 DTOs for the fields this provider reads.
/// Shapes follow the published 2.0 reference
/// (<c>api.bitbucket.org/2.0</c>): pull requests carry <c>id</c> (not
/// <c>number</c>), web links live under <c>links.html.href</c>, collections
/// are <c>{values, next, page, pagelen, size}</c> envelopes, commit statuses
/// are "builds" with a <c>state</c> of <c>SUCCESSFUL/FAILED/INPROGRESS/
/// STOPPED</c>, and webhooks are repository <c>hooks</c> keyed by
/// <c>uuid</c>. Unknown wire fields are ignored so the provider keeps
/// parsing when Atlassian adds fields.
/// </summary>
internal sealed record BitbucketPaged<T>(
    [property: JsonPropertyName("values")] IReadOnlyList<T>? Values,
    [property: JsonPropertyName("next")] string? Next,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pagelen")] int PageLength,
    [property: JsonPropertyName("size")] int? Size);

internal sealed record BitbucketPullRequest(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("links")] BitbucketLinks? Links,
    [property: JsonPropertyName("source")] BitbucketRef? Source,
    [property: JsonPropertyName("destination")] BitbucketRef? Destination,
    [property: JsonPropertyName("merge_commit")] BitbucketCommit? MergeCommit,
    [property: JsonPropertyName("author")] BitbucketUser? Author,
    [property: JsonPropertyName("participants")] IReadOnlyList<BitbucketParticipant>? Participants);

internal sealed record BitbucketLinks(
    [property: JsonPropertyName("html")] BitbucketHref? Html);

internal sealed record BitbucketHref(
    [property: JsonPropertyName("href")] string? Href);

internal sealed record BitbucketRef(
    [property: JsonPropertyName("branch")] BitbucketBranch? Branch,
    [property: JsonPropertyName("commit")] BitbucketCommit? Commit);

internal sealed record BitbucketBranch(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record BitbucketCommit(
    [property: JsonPropertyName("hash")] string? Hash);

internal sealed record BitbucketUser(
    [property: JsonPropertyName("display_name")] string? DisplayName,
    [property: JsonPropertyName("nickname")] string? Nickname);

internal sealed record BitbucketParticipant(
    [property: JsonPropertyName("user")] BitbucketUser? User,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("approved")] bool Approved,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("participated_on")] DateTimeOffset? ParticipatedOn);

internal sealed record BitbucketBuildStatus(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("created_on")] DateTimeOffset? CreatedOn);

internal sealed record BitbucketCommentContent(
    [property: JsonPropertyName("raw")] string? Raw);

internal sealed record BitbucketCommentInline(
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("to")] int? To,
    [property: JsonPropertyName("from")] int? From);

internal sealed record BitbucketCommentRef(
    [property: JsonPropertyName("id")] long Id);

internal sealed record BitbucketComment(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("content")] BitbucketCommentContent? Content,
    [property: JsonPropertyName("user")] BitbucketUser? User,
    [property: JsonPropertyName("created_on")] DateTimeOffset? CreatedOn,
    [property: JsonPropertyName("parent")] BitbucketCommentRef? Parent,
    [property: JsonPropertyName("inline")] BitbucketCommentInline? Inline);

internal sealed record BitbucketHook(
    [property: JsonPropertyName("uuid")] string? Uuid,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("events")] IReadOnlyList<string>? Events);

internal sealed record BitbucketMainBranch(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record BitbucketRepository(
    [property: JsonPropertyName("mainbranch")] BitbucketMainBranch? MainBranch,
    [property: JsonPropertyName("is_private")] bool IsPrivate);

internal sealed record BitbucketBranchRestriction(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("pattern")] string? Pattern,
    [property: JsonPropertyName("value")] int? Value);

internal sealed record BitbucketErrorDetail(
    [property: JsonPropertyName("message")] string? Message);

internal sealed record BitbucketError(
    [property: JsonPropertyName("error")] BitbucketErrorDetail? Error,
    [property: JsonPropertyName("message")] string? Message);

using System.Text.Json.Serialization;

namespace CodeyBox.AzureDevOpsUpstreamPlugin;

// Internal DTOs — Azure DevOps REST (api-version 7.1) serialisation shapes only,
// never exposed. Field names follow the real API; see the plugin README for
// links and the recorded-shape fixtures exercised by the test suite.

internal sealed record AdoPullRequest(
    [property: JsonPropertyName("pullRequestId")] int PullRequestId,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("mergeStatus")] string? MergeStatus = null,
    [property: JsonPropertyName("sourceRefName")] string? SourceRefName = null,
    [property: JsonPropertyName("targetRefName")] string? TargetRefName = null,
    [property: JsonPropertyName("lastMergeSourceCommit")] AdoCommitRef? LastMergeSourceCommit = null,
    [property: JsonPropertyName("lastMergeCommit")] AdoCommitRef? LastMergeCommit = null,
    [property: JsonPropertyName("url")] string? Url = null);

internal sealed record AdoCommitRef(
    [property: JsonPropertyName("commitId")] string? CommitId = null);

internal sealed record AdoPullRequestList(
    [property: JsonPropertyName("value")] AdoPullRequest[]? Value = null,
    [property: JsonPropertyName("count")] int Count = 0);

internal sealed record AdoRepository(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("defaultBranch")] string? DefaultBranch = null,
    [property: JsonPropertyName("remoteUrl")] string? RemoteUrl = null);

internal sealed record AdoProject(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("visibility")] string? Visibility = null);

internal sealed record AdoReviewer(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("uniqueName")] string? UniqueName = null,
    [property: JsonPropertyName("vote")] int Vote = 0,
    [property: JsonPropertyName("isRequired")] bool IsRequired = false,
    [property: JsonPropertyName("isFlagged")] bool IsFlagged = false);

internal sealed record AdoReviewerList(
    [property: JsonPropertyName("value")] AdoReviewer[]? Value = null,
    [property: JsonPropertyName("count")] int Count = 0);

internal sealed record AdoThread(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("threadContext")] AdoThreadContext? ThreadContext = null,
    [property: JsonPropertyName("comments")] AdoComment[]? Comments = null);

internal sealed record AdoThreadContext(
    [property: JsonPropertyName("filePath")] string? FilePath = null,
    [property: JsonPropertyName("rightFileStart")] AdoLineRange? RightFileStart = null);

internal sealed record AdoLineRange(
    [property: JsonPropertyName("line")] int Line = 0);

internal sealed record AdoComment(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("author")] AdoIdentity? Author = null,
    [property: JsonPropertyName("publishedDate")] DateTimeOffset? PublishedDate = null);

internal sealed record AdoIdentity(
    [property: JsonPropertyName("displayName")] string? DisplayName = null,
    [property: JsonPropertyName("uniqueName")] string? UniqueName = null);

internal sealed record AdoThreadList(
    [property: JsonPropertyName("value")] AdoThread[]? Value = null,
    [property: JsonPropertyName("count")] int Count = 0);

internal sealed record AdoCommitStatus(
    [property: JsonPropertyName("id")] int Id = 0,
    [property: JsonPropertyName("state")] string? State = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("context")] AdoStatusContext? Context = null,
    [property: JsonPropertyName("targetUrl")] string? TargetUrl = null);

internal sealed record AdoStatusContext(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("genre")] string? Genre = null);

internal sealed record AdoCommitStatusList(
    [property: JsonPropertyName("value")] AdoCommitStatus[]? Value = null,
    [property: JsonPropertyName("count")] int Count = 0);

internal sealed record AdoBuild(
    [property: JsonPropertyName("id")] int Id = 0,
    [property: JsonPropertyName("buildNumber")] string? BuildNumber = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("result")] string? Result = null,
    [property: JsonPropertyName("definition")] AdoBuildDefinition? Definition = null,
    [property: JsonPropertyName("_links")] AdoLinks? Links = null);

internal sealed record AdoBuildDefinition(
    [property: JsonPropertyName("id")] int Id = 0,
    [property: JsonPropertyName("name")] string? Name = null);

internal sealed record AdoLinks(
    [property: JsonPropertyName("web")] AdoLink? Web = null);

internal sealed record AdoLink(
    [property: JsonPropertyName("href")] string? Href = null);

internal sealed record AdoBuildList(
    [property: JsonPropertyName("value")] AdoBuild[]? Value = null,
    [property: JsonPropertyName("count")] int Count = 0);

internal sealed record AdoPolicyConfiguration(
    [property: JsonPropertyName("id")] int Id = 0,
    [property: JsonPropertyName("isEnabled")] bool IsEnabled = false,
    [property: JsonPropertyName("isBlocking")] bool IsBlocking = false,
    [property: JsonPropertyName("type")] AdoPolicyType? Type = null,
    [property: JsonPropertyName("settings")] AdoPolicySettings? Settings = null,
    [property: JsonPropertyName("scope")] AdoPolicyScope[]? Scope = null);

internal sealed record AdoPolicyType(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("displayName")] string? DisplayName = null);

internal sealed record AdoPolicySettings(
    [property: JsonPropertyName("minimumApproverCount")] int MinimumApproverCount = 0,
    [property: JsonPropertyName("creatorVoteCounts")] bool CreatorVoteCounts = false,
    [property: JsonPropertyName("scope")] AdoPolicySettingsScope[]? Scope = null);

internal sealed record AdoPolicySettingsScope(
    [property: JsonPropertyName("refName")] string? RefName = null,
    [property: JsonPropertyName("repositoryId")] string? RepositoryId = null);

internal sealed record AdoPolicyScope(
    [property: JsonPropertyName("refName")] string? RefName = null,
    [property: JsonPropertyName("repositoryId")] string? RepositoryId = null);

internal sealed record AdoPolicyConfigurationList(
    [property: JsonPropertyName("value")] AdoPolicyConfiguration[]? Value = null,
    [property: JsonPropertyName("count")] int Count = 0);

internal sealed record AdoSubscription(
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("publisherId")] string? PublisherId = null,
    [property: JsonPropertyName("eventType")] string? EventType = null,
    [property: JsonPropertyName("resourceVersion")] string? ResourceVersion = null,
    [property: JsonPropertyName("consumerId")] string? ConsumerId = null,
    [property: JsonPropertyName("consumerActionId")] string? ConsumerActionId = null,
    [property: JsonPropertyName("publisherInputs")] Dictionary<string, string>? PublisherInputs = null,
    [property: JsonPropertyName("consumerInputs")] Dictionary<string, string>? ConsumerInputs = null);

internal sealed record AdoSubscriptionList(
    [property: JsonPropertyName("value")] AdoSubscription[]? Value = null,
    [property: JsonPropertyName("count")] int Count = 0);

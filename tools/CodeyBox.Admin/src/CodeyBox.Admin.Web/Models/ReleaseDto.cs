namespace CodeyBox.Admin.Web.Models;

public sealed record ReleaseDto(
    string Id,
    string ProjectId,
    string Name,
    string? Description,
    string State,
    string? BranchName,
    string? BaseCommitSha,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? ReviewStartedAt,
    DateTimeOffset? ReleasedAt,
    string? FailedReason,
    string? TargetTag);

public sealed record CreateReleaseRequest(
    string ProjectId,
    string Name,
    string? Description = null);

public sealed record ReopenReleaseRequest(string Reason = "");

namespace CodeyBox.Build.GitHubActions;

/// <summary>
/// Adapter-owned view of the GitHub Actions REST surface this task implements
/// against (https://docs.github.com/en/rest/actions/workflows,
/// /workflow-runs, /artifacts, API version <c>2022-11-28</c>). Only the
/// endpoints needed for dispatch, status, jobs, cancellation, and artifact
/// retrieval are modeled. Log and annotation endpoints are deliberately NOT
/// modeled: no route in this adapter reads GitHub logs or annotations during
/// implementation or validation.
/// </summary>
public sealed record GitHubActionsRun(
    long Id,
    int RunAttempt,
    string Status,
    string? Conclusion,
    string HeadSha,
    string HeadBranch,
    string HeadRepository,
    string BaseRepository,
    string WorkflowPath,
    string Event,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool IsTerminalStatus => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One workflow job (status/conclusion vocabulary only, no logs).</summary>
public sealed record GitHubActionsJob(
    long Id,
    string Name,
    string Status,
    string? Conclusion);

/// <summary>
/// One run artifact listing entry. The API carries no content digest, so the
/// provider downloads (bounded) and digests bytes itself before vouching for
/// a ref. Expired artifacts are never ingested.
/// </summary>
public sealed record GitHubActionsArtifactInfo(
    long Id,
    string Name,
    long SizeBytes,
    bool Expired,
    string ArchiveUrl,
    DateTimeOffset ExpiresAt);

/// <summary>Run-status vocabulary echoed neutrally (no behavior attached).</summary>
public static class GitHubActionsStatuses
{
    public const string Queued = "queued";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
}

/// <summary>Terminal-conclusion vocabulary handled exhaustively by the mapper.</summary>
public static class GitHubActionsConclusions
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";
    public const string TimedOut = "timed_out";
    public const string ActionRequired = "action_required";
    public const string Neutral = "neutral";
    public const string Stale = "stale";
}

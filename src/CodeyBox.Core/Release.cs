namespace CodeyBox.Core;

/// <summary>Stable identifier for a release. A GUID wrapped as a value type.</summary>
public readonly record struct ReleaseId(Guid Value)
{
    public static ReleaseId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
    public static ReleaseId Parse(string s) => new(Guid.Parse(s));
    public static bool TryParse(string? s, out ReleaseId id)
    {
        if (Guid.TryParse(s, out var g)) { id = new ReleaseId(g); return true; }
        id = default; return false;
    }
}

/// <summary>
/// Lifecycle state of a release.
/// Valid transitions:
///   open → closed | abandoned
///   closed → in_review (automatic when all work items terminal) | abandoned
///   in_review → released | failed | abandoned
///   failed → open (re-open for remediation) | abandoned
/// </summary>
public enum ReleaseState
{
    Open,
    Closed,
    InReview,
    Released,
    Failed,
    Abandoned,
}

/// <summary>
/// A named release grouping work items whose PRs target a shared release branch.
/// When all work items for a closed release complete, the orchestrator runs a
/// codebase-wide deep audit before merging the release branch into main.
/// </summary>
public sealed record Release
{
    public required ReleaseId Id { get; init; }
    public required ProjectId ProjectId { get; init; }

    /// <summary>Operator-chosen label, e.g. "v1.4.0" or "Q2-2026". Unique per project.</summary>
    public required string Name { get; init; }

    public string? Description { get; init; }
    public required ReleaseState State { get; init; }

    /// <summary>SHA of main at the time the release branch was created.</summary>
    public string? BaseCommitSha { get; init; }

    /// <summary>Release branch name, e.g. "release/v1.4.0". Null until the first work item triggers creation.</summary>
    public string? BranchName { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public DateTimeOffset? ReviewStartedAt { get; init; }
    public DateTimeOffset? ReleasedAt { get; init; }

    /// <summary>Set when the deep audit did not converge within the configured max iterations.</summary>
    public string? FailedReason { get; init; }

    /// <summary>Optional GitHub release tag, e.g. "v1.4.0".</summary>
    public string? TargetTag { get; init; }

    /// <summary>Per-release deep-audit config overrides as JSON. "{}" = use project defaults.</summary>
    public string ConfigJson { get; init; } = "{}";
}

/// <summary>Context passed to deep auditors during the in_review phase.</summary>
public sealed record DeepAuditContext(
    ReleaseId ReleaseId,
    ProjectId ProjectId,
    string BranchName,
    int Iteration,
    IAgentRunner? AuditRunner = null,
    /// <summary>
    /// Optional callback invoked per stdout chunk as the deep-audit LLM agent
    /// emits output. Set by the release orchestrator when stream capture is
    /// active; null otherwise.
    /// </summary>
    Action<string>? StdoutChunkCallback = null,
    bool CaptureStructuredStream = false);

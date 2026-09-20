namespace CodeyBox.Core;

/// <summary>
/// Outbound contract: pushes progress, questions, resulting commits and pull
/// requests, and completion back to the originating external item. Most
/// providers implement this and <see cref="IWorkSource"/> against one
/// backend; the interfaces stay separate so a read-only or write-only
/// integration is possible.
/// </summary>
public interface IWorkTracker
{
    /// <summary>
    /// Namespace this tracker writes to. Matches the originating
    /// <see cref="IWorkSource.Namespace"/>; the external id is read from
    /// <see cref="WorkItem.ExternalIds"/> under this namespace.
    /// </summary>
    string Namespace { get; }

    /// <summary>Declares what this tracker can do. Unsupported operations
    /// report <see cref="TrackerPostOutcome.Unsupported"/> — never silently
    /// degrade.</summary>
    WorkTrackerCapabilities Capabilities { get; }

    /// <summary>
    /// Posts a progress update for a meaningful state transition. The body
    /// is already loop-guard-marked by the caller. Must be idempotent for
    /// repeated calls with the same <see cref="TrackerProgressUpdate.Body"/>.
    /// </summary>
    Task<TrackerPostResult> PostProgressAsync(
        TrackerProgressUpdate update, CancellationToken ct = default);

    /// <summary>
    /// Surfaces an open <see cref="WorkItemQuestion"/> as an upstream
    /// comment so the operator can reply in the external tool. Requires
    /// <see cref="WorkTrackerCapabilities.CanPostComments"/>.
    /// </summary>
    Task<TrackerPostResult> PostQuestionAsync(
        TrackerQuestionPost post, CancellationToken ct = default);

    /// <summary>
    /// Reports resulting commits and pull request links (<see
    /// cref="TrackerOutcomeReport.IsComplete"/> false), or completion /
    /// failure (<see cref="TrackerOutcomeReport.IsComplete"/> true).
    /// </summary>
    Task<TrackerPostResult> PostOutcomeAsync(
        TrackerOutcomeReport report, CancellationToken ct = default);
}

/// <summary>Declares what a work tracker can do.</summary>
public sealed record WorkTrackerCapabilities(
    /// <summary>True when the tracker can post comments (progress, questions, outcomes).</summary>
    bool CanPostComments,
    /// <summary>True when the tracker can set a native status/state on the external item.</summary>
    bool CanSetStatus);

/// <summary>Outcome of a single upstream write attempt.</summary>
public enum TrackerPostOutcome
{
    /// <summary>The update was written upstream.</summary>
    Posted,
    /// <summary>The tracker cannot perform this operation (see capabilities). Explicit, not silent.</summary>
    Unsupported,
    /// <summary>No write attempted: the work item state has no declared external mapping.</summary>
    UnmappedState,
    /// <summary>No write attempted: identical content was already posted.</summary>
    SkippedDuplicate,
    /// <summary>The upstream write failed. Recorded and retried; the work item is unaffected.</summary>
    Failed,
    /// <summary>No write attempted: the work item carries no external id under this tracker's namespace.</summary>
    NotTracked,
}

/// <summary>Result of a tracker write, including the remote id for the audit trail.</summary>
public sealed record TrackerPostResult(
    TrackerPostOutcome Outcome,
    /// <summary>Upstream comment/status id when posted; null otherwise.</summary>
    string? RemoteId = null,
    /// <summary>Human-readable detail for failures and skips.</summary>
    string? Detail = null);

/// <summary>Progress update for a meaningful work-item state transition.</summary>
public sealed record TrackerProgressUpdate
{
    /// <summary>The work item that transitioned.</summary>
    public required WorkItemId WorkItemId { get; init; }

    /// <summary>Provider namespace; the external id is resolved under it.</summary>
    public required string Namespace { get; init; }

    /// <summary>External id of the originating item.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Current CodeyBox state.</summary>
    public required WorkItemState State { get; init; }

    /// <summary>Declared external status for <see cref="State"/> (never guessed).</summary>
    public required string ExternalStatus { get; init; }

    /// <summary>Update body. Already loop-guard-marked by the caller.</summary>
    public required string Body { get; init; }
}

/// <summary>An open question surfaced as an upstream comment.</summary>
public sealed record TrackerQuestionPost
{
    /// <summary>The work item the question belongs to.</summary>
    public required WorkItemId WorkItemId { get; init; }

    /// <summary>Provider namespace; the external id is resolved under it.</summary>
    public required string Namespace { get; init; }

    /// <summary>External id of the originating item.</summary>
    public required string ExternalId { get; init; }

    /// <summary>Agent-supplied stable question id (e.g. <c>q-001</c>).</summary>
    public required string QuestionId { get; init; }

    /// <summary>Question text. Already loop-guard-marked by the caller.</summary>
    public required string Body { get; init; }
}

/// <summary>Resulting commits / pull request links, or completion / failure.</summary>
public sealed record TrackerOutcomeReport
{
    /// <summary>The work item the outcome belongs to.</summary>
    public required WorkItemId WorkItemId { get; init; }

    /// <summary>Provider namespace; the external id is resolved under it.</summary>
    public required string Namespace { get; init; }

    /// <summary>External id of the originating item.</summary>
    public required string ExternalId { get; init; }

    /// <summary>True for terminal completion/failure, false for interim commit/PR links.</summary>
    public required bool IsComplete { get; init; }

    /// <summary>True when the terminal outcome is success.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Declared external status (never guessed; empty when unmapped and not written).</summary>
    public string ExternalStatus { get; init; } = string.Empty;

    /// <summary>Report body. Already loop-guard-marked by the caller.</summary>
    public required string Body { get; init; }

    /// <summary>Resulting commit SHAs to link, when known.</summary>
    public IReadOnlyList<string> CommitShas { get; init; } = [];

    /// <summary>Pull request URL to link, when known.</summary>
    public string? PullRequestUrl { get; init; }
}

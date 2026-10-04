namespace CodeyBox.Core;

/// <summary>
/// Lifecycle of an audit check run, in forge-neutral terms. Maps 1:1 onto
/// the GitHub Checks states <c>queued</c>, <c>in_progress</c> and
/// <c>completed</c> so the mapping cannot silently drift.
/// </summary>
public enum AuditCheckLifecycle
{
    Queued,
    InProgress,
    Completed,
}

/// <summary>
/// Honest outcome of an audit check run. The <c>NotRun</c> case covers every
/// way an audit can fail to produce coverage — missing, skipped, unsupported
/// forge, infrastructure failure, or cancellation — and must never be
/// published as successful coverage. The reason travels with the verdict so
/// the publisher maps it to a non-success conclusion.
/// </summary>
public enum AuditCheckVerdict
{
    /// <summary>The audit ran to completion and its findings stand as published.</summary>
    Passed,

    /// <summary>The audit ran and reports blocking findings.</summary>
    Failed,

    /// <summary>
    /// No audit coverage exists for this scope. The accompanying
    /// <see cref="AuditCheckUnavailabilityReason"/> says why.
    /// </summary>
    NotRun,
}

/// <summary>Why an audit scope has no coverage to publish.</summary>
public enum AuditCheckUnavailabilityReason
{
    Missing,
    Skipped,
    UnsupportedForge,
    InfrastructureFailed,
    Cancelled,
}

/// <summary>
/// Forge conclusions for a completed audit check run. Names mirror the GitHub
/// Checks <c>conclusion</c> vocabulary; other forges map onto the nearest
/// member. There is deliberately no silent-success member for missing audits:
/// use <see cref="Skipped"/>, <see cref="Cancelled"/>, or
/// <see cref="ActionRequired"/> with an explanatory summary.
/// </summary>
public enum AuditCheckConclusion
{
    Success,
    Failure,
    Neutral,
    Cancelled,
    Skipped,
    TimedOut,
    ActionRequired,
}

/// <summary>
/// Maps an audit-side verdict plus lifecycle to the forge status/conclusion
/// pair. This is the single decision point both the GitHub publisher and its
/// tests route through, so an empty findings list can never imply coverage:
/// the verdict is an explicit input, never inferred from finding counts.
/// </summary>
public static class AuditCheckConclusionMapper
{
    /// <summary>Forge status for a lifecycle value (GitHub: queued/in_progress/completed).</summary>
    public static string ToStatusString(AuditCheckLifecycle lifecycle) => lifecycle switch
    {
        AuditCheckLifecycle.Queued => "queued",
        AuditCheckLifecycle.InProgress => "in_progress",
        AuditCheckLifecycle.Completed => "completed",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
    };

    /// <summary>
    /// Forge conclusion for a completed run, or <c>null</c> while still queued
    /// or in progress (GitHub rejects a conclusion on unfinished runs).
    /// </summary>
    public static string? ToConclusionString(
        AuditCheckLifecycle lifecycle,
        AuditCheckVerdict verdict,
        AuditCheckUnavailabilityReason? reason)
    {
        if (lifecycle != AuditCheckLifecycle.Completed)
            return null;

        if (verdict == AuditCheckVerdict.Passed)
            return "success";
        if (verdict == AuditCheckVerdict.Failed)
            return "failure";

        return (reason ?? AuditCheckUnavailabilityReason.Missing) switch
        {
            AuditCheckUnavailabilityReason.Cancelled => "cancelled",
            AuditCheckUnavailabilityReason.InfrastructureFailed => "action_required",
            AuditCheckUnavailabilityReason.Missing => "skipped",
            AuditCheckUnavailabilityReason.Skipped => "skipped",
            AuditCheckUnavailabilityReason.UnsupportedForge => "skipped",
            _ => "skipped",
        };
    }

    /// <summary>Parses a forge conclusion string back to the neutral enum.</summary>
    public static AuditCheckConclusion ParseConclusion(string? conclusion) =>
        conclusion?.ToLowerInvariant() switch
        {
            "success" => AuditCheckConclusion.Success,
            "failure" => AuditCheckConclusion.Failure,
            "neutral" => AuditCheckConclusion.Neutral,
            "cancelled" => AuditCheckConclusion.Cancelled,
            "skipped" => AuditCheckConclusion.Skipped,
            "timed_out" => AuditCheckConclusion.TimedOut,
            "action_required" => AuditCheckConclusion.ActionRequired,
            _ => AuditCheckConclusion.Neutral,
        };
}

/// <summary>
/// One line-anchored audit annotation destined for a forge check run.
/// Built only from structured <see cref="AuditReportFinding"/> data with a
/// validated repository-relative path — never from truncated raw output.
/// </summary>
public sealed record AuditCheckAnnotation
{
    public required string Path { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    /// <summary>GitHub annotation_level: notice, warning, or failure.</summary>
    public required string Level { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// Request to publish structured audit results as a forge check run for the
/// exact audited commit. The verdict is an explicit input describing what the
/// audit actually did — it is never inferred from the findings count.
/// </summary>
public sealed record AuditCheckPublicationRequest
{
    public required string Owner { get; init; }
    public required string Repository { get; init; }
    /// <summary>Exact 40-hex audited commit the check run is attached to.</summary>
    public required string HeadSha { get; init; }
    public required string WorkItemId { get; init; }
    public required AuditTarget Target { get; init; }
    public required int Iteration { get; init; }
    /// <summary>Publication attempt for this iteration; increments on retry/reconcile.</summary>
    public int Attempt { get; init; } = 1;
    /// <summary>Auditor name, or <c>"aggregate"</c> for the combined verdict.</summary>
    public required string Scope { get; init; }
    public required string CheckName { get; init; }
    /// <summary>
    /// Correlation value sent as GitHub <c>external_id</c>. Used to find the
    /// intended check run again after a lost create response — it is not an
    /// idempotency key and never a uniqueness guarantee.
    /// </summary>
    public required string ExternalId { get; init; }
    public required AuditCheckLifecycle Lifecycle { get; init; }
    public required AuditCheckVerdict Verdict { get; init; }
    public AuditCheckUnavailabilityReason? UnavailabilityReason { get; init; }
    public IReadOnlyList<AuditReportFinding> Findings { get; init; } = [];
    /// <summary>
    /// Authenticated URL of the complete report. Appended to the summary so
    /// bounded annotation batches never lose findings silently; the link must
    /// preserve the host's authorization (no anonymous token passthrough).
    /// </summary>
    public string? DetailsUrl { get; init; }
    /// <summary>CodeyBox source revision that produced the audit (provenance).</summary>
    public string? SourceRevision { get; init; }
}

/// <summary>Whether a forge remote can publish audit check runs.</summary>
public sealed record AuditCheckPublicationSupport(bool Supported, string Reason)
{
    public static AuditCheckPublicationSupport Yes { get; } = new(true, "supported");
    public static AuditCheckPublicationSupport No(string reason) => new(false, reason);
}

/// <summary>Outcome of a publish/reconcile operation against the forge.</summary>
public sealed record AuditCheckPublicationResult
{
    public required long CheckRunId { get; init; }
    public string? HtmlUrl { get; init; }
    public required string Status { get; init; }
    public string? Conclusion { get; init; }
    public required int AnnotationsPublished { get; init; }
    public required int AnnotationsOmitted { get; init; }
    public required int BatchesSent { get; init; }
    /// <summary>
    /// True when an ambiguous batch write (timeout/lost response) could not be
    /// reconciled by re-reading forge state. Findings may be duplicated or
    /// missing; the summary discloses the uncertainty instead of claiming success.
    /// </summary>
    public bool BatchUncertain { get; init; }
}

/// <summary>Base class for typed audit-check publication failures.</summary>
public class AuditCheckPublicationException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// The forge rejected the request shape or the target (bad SHA, unknown
/// repo/ref, validation error). Retrying without changing the request is
/// pointless; the service records this as failed, not retryable.
/// </summary>
public sealed class AuditCheckValidationException(string message, Exception? inner = null)
    : AuditCheckPublicationException(message, inner);

/// <summary>
/// Authentication or permission denial (HTTP 401/403 without a rate-limit
/// signal). An actionable blocked state: the service stops retrying and
/// surfaces the missing scope, never requesting broader grants silently.
/// </summary>
public sealed class AuditCheckAuthException(string message, Exception? inner = null)
    : AuditCheckPublicationException(message, inner);

/// <summary>
/// Rate-limited or transient transport failure. Retryable with bounded
/// backoff; carries the server's <c>Retry-After</c> hint when present.
/// </summary>
public sealed class AuditCheckRateLimitedException(string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : AuditCheckPublicationException(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Transient 5xx or network failure. Retryable with bounded backoff.
/// </summary>
public sealed class AuditCheckTransientException(string message, Exception? inner = null)
    : AuditCheckPublicationException(message, inner);

/// <summary>The forge kind cannot publish audit check runs.</summary>
public sealed class AuditCheckUnsupportedException(string message)
    : AuditCheckPublicationException(message);

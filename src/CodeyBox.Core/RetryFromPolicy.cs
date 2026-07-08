namespace CodeyBox.Core;

/// <summary>
/// Shared contract for operator-facing retry-from values and the lifecycle
/// state each value resumes from.
/// </summary>
public static class RetryFromPolicy
{
    public const string Planning = "planning";
    public const string PlanReview = "plan_review";
    public const string PlanApproved = "plan_approved";
    public const string Work = "work";
    public const string Rework = "rework";
    public const string Audit = "audit";
    public const string ConflictRework = "conflict_rework";
    public const string Merge = "merge";
    public const string Upstream = "upstream";

    /// <summary>
    /// Normalizes a retry-from value. Returns false for null, blank, or
    /// unsupported values so manual retry callers can reject typos explicitly.
    /// </summary>
    public static bool TryNormalize(string? retryFrom, out string normalized)
    {
        normalized = retryFrom?.Trim().ToLowerInvariant() switch
        {
            Planning => Planning,
            PlanReview => PlanReview,
            PlanApproved => PlanApproved,
            Work => Work,
            Rework => Rework,
            Audit => Audit,
            ConflictRework => ConflictRework,
            Merge => Merge,
            Upstream => Upstream,
            _ => string.Empty,
        };

        return normalized.Length > 0;
    }

    /// <summary>
    /// Normalizes a persisted retry-from value, falling back to <see cref="Work"/>
    /// for unknown historical values.
    /// </summary>
    public static string NormalizeOrWork(string? retryFrom) =>
        TryNormalize(retryFrom, out var normalized) ? normalized : Work;

    public static bool TryGetResumeState(string? retryFrom, out WorkItemState resumeState)
    {
        if (!TryNormalize(retryFrom, out var normalized))
        {
            resumeState = WorkItemState.Queued;
            return false;
        }

        resumeState = ResumeStateForNormalized(normalized);
        return true;
    }

    /// <summary>
    /// Maps persisted retry-from values to their resume state, falling back to
    /// <see cref="WorkItemState.Queued"/> for unknown historical values.
    /// </summary>
    public static WorkItemState ResumeStateForRetryFrom(string? retryFrom) =>
        TryNormalize(retryFrom, out var normalized)
            ? ResumeStateForNormalized(normalized)
            : WorkItemState.Queued;

    public static WorkItemState ResumeStateForNormalized(string normalized) => normalized switch
    {
        Planning => WorkItemState.Queued,
        PlanReview => WorkItemState.PlanReview,
        PlanApproved => WorkItemState.PlanApproved,
        Work => WorkItemState.Queued,
        Rework => WorkItemState.WorkComplete,
        Audit => WorkItemState.WorkComplete,
        ConflictRework => WorkItemState.ReworkingForConflict,
        Merge => WorkItemState.AuditPassed,
        Upstream => WorkItemState.Merged,
        _ => WorkItemState.Queued,
    };
}

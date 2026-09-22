using CodeyBox.Core;

namespace CodeyBox.Majordomo;

/// <summary>
/// Pipeline phase a <c>retry_work_item</c> call resumes from. A closed enum
/// mirror of <see cref="RetryFromPolicy"/>: a mistyped or out-of-vocabulary
/// retry-from is unrepresentable here rather than a failed string parse at
/// the orchestrator.
/// </summary>
public enum WorkItemRetryFrom
{
    Planning,
    PlanReview,
    PlanApproved,
    Work,
    Rework,
    Audit,
    Delegation,
    ConflictRework,
    Merge,
    Upstream,
}

public static class WorkItemRetryFromExtensions
{
    /// <summary>Maps to the canonical <see cref="RetryFromPolicy"/> wire value.</summary>
    public static string ToPolicyValue(this WorkItemRetryFrom from) => from switch
    {
        WorkItemRetryFrom.Planning => RetryFromPolicy.Planning,
        WorkItemRetryFrom.PlanReview => RetryFromPolicy.PlanReview,
        WorkItemRetryFrom.PlanApproved => RetryFromPolicy.PlanApproved,
        WorkItemRetryFrom.Work => RetryFromPolicy.Work,
        WorkItemRetryFrom.Rework => RetryFromPolicy.Rework,
        WorkItemRetryFrom.Audit => RetryFromPolicy.Audit,
        WorkItemRetryFrom.Delegation => RetryFromPolicy.Delegation,
        WorkItemRetryFrom.ConflictRework => RetryFromPolicy.ConflictRework,
        WorkItemRetryFrom.Merge => RetryFromPolicy.Merge,
        WorkItemRetryFrom.Upstream => RetryFromPolicy.Upstream,
        _ => throw new ArgumentOutOfRangeException(nameof(from), from, "unknown retry-from phase"),
    };
}

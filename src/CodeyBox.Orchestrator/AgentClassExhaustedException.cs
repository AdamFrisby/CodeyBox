using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised by the in-iteration quota fallback wrapper when every eligible
/// member of the work item's agent class has classified as
/// <see cref="AgentFailureKind.QuotaExhausted"/> in this pickup.
///
/// <para>Both pipeline phases route the exception to the same top-level
/// <c>RunAsync</c> catch, which parks the item in
/// <see cref="WorkItemState.WaitingForQuotaReset"/> rather than transitioning
/// to <see cref="WorkItemState.Failed"/> — quota windows reset on their own
/// schedule and the retry scheduler re-enqueues automatically.</para>
/// <list type="bullet">
///   <item>Work-phase: the wrapper raises this once every spill candidate is
///         exhausted; the caller parks and resumes on quota reset.</item>
///   <item>Audit-phase: the per-auditor task body captures the exception so
///         sibling auditors can finish their in-flight work, then re-surfaces
///         it after Task.WhenAll. A Pass verdict requires every configured
///         auditor to have produced a verdict, so a quota-exhausted auditor
///         parks the item rather than silently skipping (which would let a
///         Pass emerge with an incomplete review set). The iteration is NOT
///         counted toward the rework budget — when quota returns, the same
///         iteration runs again.</item>
/// </list>
/// </summary>
public sealed class AgentClassExhaustedException : Exception
{
    public string ClassId { get; }
    public string Phase { get; }
    public int MemberCount { get; }
    public DateTimeOffset? EarliestResetAt { get; }

    public AgentClassExhaustedException(
        string classId,
        string phase,
        int memberCount,
        DateTimeOffset? earliestResetAt,
        string message)
        : base(message)
    {
        ClassId = classId;
        Phase = phase;
        MemberCount = memberCount;
        EarliestResetAt = earliestResetAt;
    }
}

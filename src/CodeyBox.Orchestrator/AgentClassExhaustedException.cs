using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised by the in-iteration quota fallback wrapper when every eligible
/// member of the work item's agent class has classified as
/// <see cref="AgentFailureKind.QuotaExhausted"/> in this pickup.
///
/// The pipeline catches this and parks the item in
/// <see cref="WorkItemState.WaitingForQuotaReset"/> rather than transitioning
/// it to <see cref="WorkItemState.Failed"/> — quota windows reset on their own
/// schedule and the retry scheduler re-enqueues automatically.
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

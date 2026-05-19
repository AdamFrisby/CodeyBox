using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Raised by the in-iteration quota fallback wrapper when every eligible
/// member of the work item's agent class has classified as
/// <see cref="AgentFailureKind.QuotaExhausted"/> in this pickup.
///
/// <para>Two consumers catch this exception with different recovery semantics:</para>
/// <list type="bullet">
///   <item>Work-phase: top-level <c>RunAsync</c> parks the item in
///         <see cref="WorkItemState.WaitingForQuotaReset"/> rather than
///         transitioning it to <see cref="WorkItemState.Failed"/> — quota
///         windows reset on their own schedule and the retry scheduler
///         re-enqueues automatically.</item>
///   <item>Audit-phase: the per-auditor task body catches the exception and
///         skips that LLM auditor for the current iteration (warning-and-skip).
///         The remaining auditors still run and the work item keeps
///         progressing rather than parking on a single auditor's exhaustion.</item>
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

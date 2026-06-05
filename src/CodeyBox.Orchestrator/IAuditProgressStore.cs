using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Workflow-owned durable audit progress history. Unlike audit reports, these
/// rows are control-plane state and are not subject to diagnostic report
/// retention sweeps.
/// </summary>
public interface IAuditProgressStore
{
    Task RecordAuditProgressAsync(
        WorkItemId workItemId,
        DateTimeOffset? workAttemptStartedAt,
        AuditProgressRecord progress,
        DateTimeOffset recordedAt,
        CancellationToken ct = default);

    Task<IReadOnlyList<AuditProgressRecord>> GetAuditProgressAsync(
        WorkItemId workItemId,
        DateTimeOffset? workAttemptStartedAt,
        CancellationToken ct = default);
}

public sealed record AuditProgressRecord(
    int Iteration,
    int MaxIterations,
    int BlockingFindings,
    int NonBlockingFindings,
    IReadOnlyList<string> BlockingFindingIds,
    IReadOnlyList<AuditProgressFinding> BlockingFindingsDetails,
    IReadOnlyList<AuditProgressFinding> Findings,
    string? WorkBranchTip);

/// <summary>
/// Workflow-domain snapshot of an audit finding persisted for retry/escalation
/// control. Webhook payload DTOs are derived from this only at publish time.
/// </summary>
public sealed record AuditProgressFinding(
    string AuditorName,
    AuditSeverity Severity,
    string Title,
    string Description,
    string? Location = null);

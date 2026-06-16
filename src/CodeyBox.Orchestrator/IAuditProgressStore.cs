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
    string? WorkBranchTip,
    string Status = AuditProgressStatuses.Complete,
    IReadOnlyList<string>? ScheduledAuditors = null,
    IReadOnlyList<string>? CompletedAuditors = null);

public static class AuditProgressStatuses
{
    public const string InProgress = "in_progress";
    public const string Incomplete = "incomplete";
    public const string Complete = "complete";

    public static bool IsComplete(string? status)
        => string.Equals(status ?? Complete, Complete, StringComparison.OrdinalIgnoreCase);
}

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

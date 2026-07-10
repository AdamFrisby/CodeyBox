namespace CodeyBox.Core;

/// <summary>
/// A single finding within an <see cref="AuditReport"/>.
/// </summary>
/// <param name="Id">
/// Stable ID derived from auditor name, normalised title, and sorted file list via
/// <see cref="FindingIdComputer.Compute"/>. Same underlying issue across iterations
/// produces the same ID (best-effort; LLM phrasing variance may still differ).
/// </param>
/// <param name="Files">Files mentioned by the auditor, or empty if the auditor did not surface a location.</param>
/// <param name="LineHints">Optional line numbers from the auditor's location hint.</param>
public sealed record AuditReportFinding(
    string Id,
    string Severity,
    string Title,
    string Message,
    IReadOnlyList<string> Files,
    IReadOnlyList<int> LineHints);

/// <summary>
/// Persisted record of a single auditor invocation for one work item iteration.
/// </summary>
public sealed record AuditReport
{
    public required string Id { get; init; }
    public required string WorkItemId { get; init; }
    public required int Iteration { get; init; }
    /// <summary>
    /// Artifact reviewed by this invocation. Defaults to code for reports
    /// created by legacy callers and rows written before target persistence.
    /// </summary>
    public AuditTarget Target { get; init; } = AuditTarget.Code;
    public required string AuditorName { get; init; }
    public required string AuditorKind { get; init; }
    public required string WorstSeverity { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset EndedAt { get; init; }
    public required long DurationMs { get; init; }
    public required IReadOnlyList<AuditReportFinding> Findings { get; init; }

    /// <summary>
    /// Full auditor output capped at 256 KB after redaction.
    /// A <c>[...truncated]</c> suffix is appended when the original exceeded the cap.
    /// Null when the auditor produced no capturable output.
    /// </summary>
    public string? RawOutput { get; init; }
}

/// <summary>
/// Persistence abstraction for per-auditor findings reports.
/// </summary>
public interface IAuditReportStore
{
    Task CreateAsync(AuditReport report, CancellationToken ct = default);

    /// <summary>Returns all reports for a work item ordered by (target, iteration, auditor_name).</summary>
    Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(string workItemId, CancellationToken ct = default);

    /// <summary>Returns reports for one target ordered by (iteration, auditor_name).</summary>
    async Task<IReadOnlyList<AuditReport>> GetByWorkItemAsync(
        string workItemId,
        AuditTarget target,
        CancellationToken ct = default)
        => (await GetByWorkItemAsync(workItemId, ct).ConfigureAwait(false))
            .Where(report => report.Target == target)
            .ToList();

    /// <summary>Returns only the raw_output column for one target-specific auditor call.</summary>
    Task<string?> GetRawOutputAsync(
        string workItemId,
        AuditTarget target,
        int iteration,
        string auditorName,
        CancellationToken ct = default);

    /// <summary>Deletes rows whose started_at is strictly before <paramref name="cutoff"/>.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}

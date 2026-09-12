using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Immutable input bundle of persisted state used to compose a convergence brief.
/// </summary>
public sealed record ConvergenceBriefInput
{
    public required WorkItem WorkItem { get; init; }
    public IReadOnlyList<StoredAuditProgress> AuditProgress { get; init; } = [];
    public IReadOnlyList<AuditReport> AuditReports { get; init; } = [];
    public IReadOnlyList<FailureEventRecord> FailureEvents { get; init; } = [];
    public IReadOnlyList<AgentInvolvement> AgentInvolvements { get; init; } = [];
    public IReadOnlyList<AgentFallbackRecord> FallbackHistory { get; init; } = [];
    public IReadOnlyList<AgentStreamSummaryRow> StreamSummaries { get; init; } = [];
    public IReadOnlyList<AgentStreamExcerpt> StreamExcerpts { get; init; } = [];
}

/// <summary>
/// An excerpt extracted from an agent stream file.
/// </summary>
public sealed record AgentStreamExcerpt(
    string FileName,
    string Phase,
    int? Iteration,
    string ExcerptText);

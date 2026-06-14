using System.Collections.Generic;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Names of the pipeline stages that the transition-health classifier groups
/// transitions under in the per-stage breakdown. The taxonomy is deliberately
/// coarse: operators use this to localise "audits dying" vs. "merges dying" vs.
/// "work-phase agents crashing"; sub-step granularity (e.g.
/// <c>git.clone_into_sandbox</c>) belongs in the timings view, not here.
/// </summary>
public static class TransitionStage
{
    public const string Work = "Work";
    public const string Rework = "Rework";
    public const string Audit = "Audit";
    public const string Merge = "Merge";
    public const string Terminal = "Terminal";

    public static readonly IReadOnlyList<string> AllOrdered = new[]
    {
        Work, Rework, Audit, Merge, Terminal,
    };
}

/// <summary>
/// Whether a single stage transition counts as healthy infrastructure
/// (<see cref="Legitimate"/>) or unhealthy (<see cref="InfraFailure"/>) when
/// computing the transition-health score. A transition that does not bear on
/// infrastructure health (operator cancellation, an unfinalised in-flight row,
/// a quota-park that did not fail an agent run, …) is classified as
/// <see cref="Skipped"/> and contributes to neither the numerator nor the
/// denominator.
/// </summary>
public enum TransitionClassification
{
    Skipped = 0,
    Legitimate = 1,
    InfraFailure = 2,
}

/// <summary>
/// One classified stage transition. The records come from three sources:
/// <list type="bullet">
/// <item>Agent involvement rows (Work / Rework / Audit / Merge agent runs).</item>
/// <item>Audit report rows (the auditor-died vs. real-findings discriminator).</item>
/// <item>Terminal failed work items (Failed / MergeConflictResolutionFailed).</item>
/// </list>
/// </summary>
/// <param name="Stage">One of the constants on <see cref="TransitionStage"/>.</param>
/// <param name="Classification">Healthy, unhealthy, or excluded from scoring.</param>
/// <param name="InfraFailureKind">
/// When <see cref="Classification"/> is <see cref="TransitionClassification.InfraFailure"/>,
/// a short kind label (<c>quota</c>, <c>timeout</c>, <c>agent</c>,
/// <c>auditor_failed</c>, <c>infrastructure</c>, <c>build</c>, <c>configuration</c>,
/// <c>agent_unavailable</c>, <c>merge_conflict_resolution_failed</c>) used in
/// the breakdown's <c>infraByKind</c> tally. Null for Legitimate / Skipped.
/// </param>
/// <param name="OccurredAt">
/// Completion timestamp used for windowing and the "most recent N" cap.
/// </param>
public sealed record TransitionRecord(
    string Stage,
    TransitionClassification Classification,
    string? InfraFailureKind,
    DateTimeOffset OccurredAt);

/// <summary>
/// Aggregated counts for one pipeline stage in the transition-health report.
/// </summary>
public sealed record TransitionStageBreakdown
{
    public required string Stage { get; init; }
    public required int Legitimate { get; init; }
    public required int InfraFailure { get; init; }

    /// <summary>
    /// <see cref="Legitimate"/> + <see cref="InfraFailure"/>. Excludes
    /// <see cref="TransitionClassification.Skipped"/>.
    /// </summary>
    public int Total => Legitimate + InfraFailure;

    public required double Score { get; init; }
    public required IReadOnlyDictionary<string, int> InfraByKind { get; init; }
}

/// <summary>
/// Final read-model returned by <c>GET /fleet/transition-health</c>.
/// </summary>
public sealed record TransitionHealthReport
{
    public required double Score { get; init; }
    public required double InfraFailureRate { get; init; }
    public required int TotalTransitions { get; init; }
    public required int LegitimateTransitions { get; init; }
    public required int InfraFailureTransitions { get; init; }
    public required DateTimeOffset WindowStart { get; init; }
    public required DateTimeOffset WindowEnd { get; init; }
    public required TimeSpan WindowDuration { get; init; }
    public required int? MaxTransitions { get; init; }
    public required string? WorstStage { get; init; }
    public required IReadOnlyList<TransitionStageBreakdown> Stages { get; init; }
    public required IReadOnlyDictionary<string, int> InfraByKind { get; init; }
}

/// <summary>
/// Raw row shapes the data source returns to the classifier. The classifier
/// is pure (no DB I/O) so it is straightforward to unit-test by feeding in
/// hand-authored snapshots.
/// </summary>
public sealed record TransitionDataSnapshot(
    IReadOnlyList<TransitionInvolvementRow> Involvements,
    IReadOnlyList<TransitionAuditReportRow> AuditReports,
    IReadOnlyList<TransitionTerminalFailureRow> TerminalFailures);

public sealed record TransitionInvolvementRow(
    string WorkItemId,
    string Phase,
    int? Iteration,
    string? Outcome,
    DateTimeOffset EndedAt);

public sealed record TransitionAuditReportRow(
    string WorkItemId,
    int Iteration,
    string AuditorName,
    string WorstSeverity,
    DateTimeOffset EndedAt,
    IReadOnlyList<string> FindingTitles);

public sealed record TransitionTerminalFailureRow(
    string WorkItemId,
    int State,
    string? FailureKind,
    DateTimeOffset UpdatedAt);

using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Deterministic, config-driven re-weighting of the CODE audit for PLANNED
/// items. A planned item already had its subjective/architectural approach
/// judged at the plan-review stage, and the <c>plan:adherence</c> reviewer
/// verifies the code followed that approved approach. So during the code audit
/// of a planned item we DE-EMPHASISE re-litigating the approach: blocking
/// findings from the configured "approach" reviewers are demoted to advisory —
/// still recorded and surfaced to operators, but no longer forcing another
/// rework cycle. The objective gates (build, tests, security, cheating,
/// completeness, plan-adherence, deterministic patterns) keep full blocking
/// authority. The thesis is fewer, cheaper code cycles for planned items.
///
/// <para>This NEVER removes an auditor: every auditor still runs and every
/// finding is still recorded. It only reweights which findings block, and only
/// for planned items, and only for the operator-configured auditor names. For an
/// unplanned item, or when rebalancing is disabled, it is exactly the normal
/// severity filter.</para>
/// </summary>
public static class PlannedItemAuditRebalance
{
    /// <summary>
    /// Selects the blocking findings for a code-audit iteration.
    ///
    /// <para>When <paramref name="itemWasPlanned"/> and
    /// <paramref name="rebalanceEnabled"/> are both true, a finding whose
    /// <see cref="AuditFinding.AuditorName"/> matches (case-insensitively) an
    /// entry in <paramref name="advisoryAuditorNames"/> is excluded from the
    /// blocking set regardless of severity (demoted to advisory). Every other
    /// finding blocks when its severity is at or above
    /// <paramref name="failingSeverity"/>, exactly as normal.</para>
    /// </summary>
    /// <returns>
    /// The subset of <paramref name="findings"/> that should block the merge and
    /// drive rework. Advisory (demoted or sub-threshold) findings are excluded
    /// but remain in the caller's full findings list for reporting.
    /// </returns>
    public static IReadOnlyList<AuditFinding> SelectBlocking(
        IReadOnlyList<AuditFinding> findings,
        AuditSeverity failingSeverity,
        bool itemWasPlanned,
        bool rebalanceEnabled,
        IEnumerable<string> advisoryAuditorNames)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(advisoryAuditorNames);

        var advisory = itemWasPlanned && rebalanceEnabled
            ? new HashSet<string>(advisoryAuditorNames, StringComparer.OrdinalIgnoreCase)
            : null;
        if (advisory is { Count: 0 })
            advisory = null;

        var blocking = new List<AuditFinding>(findings.Count);
        foreach (var finding in findings)
        {
            if (finding.Severity < failingSeverity)
                continue;
            if (advisory is not null && advisory.Contains(finding.AuditorName))
                continue;
            blocking.Add(finding);
        }

        return blocking;
    }
}

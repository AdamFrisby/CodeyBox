using System.Text;
using CodeyBox.Core;

namespace CodeyBox.Audit.Llm.PlanAudit;

/// <summary>
/// Pure mapping from a structured <see cref="PlanAuditVerdict"/> to the
/// pipeline's <see cref="AuditResult"/>. This is the independent hard gate:
/// <see cref="AuditResult.Passed"/> is false iff the verdict carries a BLOCKER
/// finding — computed only from this auditor's own findings, with no aggregate
/// or cross-auditor "good enough" balancing. MAJOR/MINOR/INFO findings are
/// recorded (so the re-plan sees them) but never flip the gate.
/// </summary>
public static class PlanAuditVerdictMapper
{
    /// <summary>
    /// Maps <paramref name="verdict"/> to an <see cref="AuditResult"/> attributed
    /// to <paramref name="auditorName"/>. Each plan-audit finding becomes an
    /// <see cref="AuditFinding"/> whose description embeds the grounding class,
    /// the cited plan evidence, and the required plan edit; open questions become
    /// non-blocking Info findings so unknowns stay visible in the durable audit
    /// record. Not-applicable criteria are not emitted as findings (there is
    /// nothing wrong to report). <paramref name="rawOutput"/> is carried through
    /// verbatim for replay/observability.
    /// </summary>
    public static AuditResult ToAuditResult(
        PlanAuditVerdict verdict,
        string auditorName,
        string? rawOutput = null,
        string? agentStderr = null,
        string? agentSummary = null,
        string? agentStdout = null)
    {
        ArgumentNullException.ThrowIfNull(verdict);

        var findings = new List<AuditFinding>(verdict.Findings.Count + verdict.OpenQuestions.Count);

        foreach (var f in verdict.Findings)
        {
            findings.Add(new AuditFinding(
                AuditorName: auditorName,
                Severity: PlanAuditVocabulary.ToAuditSeverity(f.Severity),
                Title: f.Title,
                Description: FormatDescription(f),
                Location: $"PLAN:{f.Criterion}"));
        }

        foreach (var question in verdict.OpenQuestions)
        {
            if (string.IsNullOrWhiteSpace(question))
                continue;
            findings.Add(new AuditFinding(
                AuditorName: auditorName,
                Severity: AuditSeverity.Info,
                Title: "open question",
                Description: question,
                Location: "PLAN"));
        }

        // Gate is derived from the verdict, not from any model self-report:
        // Passed iff no BLOCKER finding.
        return new AuditResult(
            Passed: !verdict.HasBlocker,
            Findings: findings,
            RawOutput: rawOutput,
            AgentStderr: agentStderr,
            AgentSummary: agentSummary,
            AgentStdout: agentStdout);
    }

    private static string FormatDescription(PlanAuditFinding f)
    {
        var sb = new StringBuilder();
        sb.Append(f.Description);
        sb.Append("\nGrounding: ").Append(f.Grounding);
        if (!string.IsNullOrWhiteSpace(f.EvidenceFromPlan))
            sb.Append("\nEvidence from plan: ").Append(f.EvidenceFromPlan);
        if (!string.IsNullOrWhiteSpace(f.RequiredFix))
            sb.Append("\nRequired fix: ").Append(f.RequiredFix);
        return sb.ToString();
    }
}

using CodeyBox.Core;

namespace CodeyBox.Audit.Llm.PlanAudit;

/// <summary>
/// How an artifact a plan references is grounded in the supplied context. This
/// is the evidence-classification vocabulary shared by every test in the
/// plan-audit chain (grounding is a cross-cutting reviewer concern, not
/// specific to TEST 01).
/// </summary>
public enum PlanEvidenceClass
{
    /// <summary>Directly present in the supplied context (repo file, prompt, schema).</summary>
    Observed,

    /// <summary>Reasonably deduced from observed facts, but not stated outright.</summary>
    Inferred,

    /// <summary>A change the plan proposes to make (does not exist yet).</summary>
    Proposed,

    /// <summary>Asserted without support in the supplied context — a hallucination risk.</summary>
    Unsupported,
}

/// <summary>
/// Finding severity in the plan-audit chain. Distinct from
/// <see cref="AuditSeverity"/>: only <see cref="Blocker"/> fails the plan
/// (an independent hard gate). <see cref="Major"/>/<see cref="Minor"/>/
/// <see cref="Info"/> are recorded for the re-plan but never block, and are
/// never netted across auditors into a compromise verdict.
/// </summary>
public enum PlanAuditSeverity
{
    /// <summary>A blocking problem — this alone FAILs the plan.</summary>
    Blocker,

    /// <summary>A significant but non-blocking problem for the implementer to address.</summary>
    Major,

    /// <summary>A minor, non-blocking observation.</summary>
    Minor,

    /// <summary>Informational only.</summary>
    Info,
}

/// <summary>
/// The overall per-run status of a plan-audit test. Derived mechanically from
/// the findings and not-applicable set — never trusted from a model's
/// self-report — so a chatty or prompt-injected reviewer cannot label itself
/// PASS while emitting a blocking finding.
/// </summary>
public enum PlanAuditStatus
{
    /// <summary>No findings; the plan meets this test's Pass criteria.</summary>
    Pass,

    /// <summary>Non-blocking findings only (MAJOR/MINOR/INFO).</summary>
    Partial,

    /// <summary>At least one BLOCKER finding — the plan FAILs this test.</summary>
    Fail,

    /// <summary>Every criterion this test defines was self-skipped for this plan.</summary>
    NotApplicable,
}

/// <summary>
/// Parses the plan-audit vocabulary tokens produced by the reviewer model.
/// Severity parsing is fail-closed: an absent or unrecognized token maps to
/// <see cref="PlanAuditSeverity.Blocker"/> so a garbled severity can never
/// silently downgrade a blocking finding past the gate (mirrors
/// <see cref="AuditSeverityParser"/> defaulting unknown input to Error).
/// Grounding parsing is fail-cautious: unknown maps to
/// <see cref="PlanEvidenceClass.Unsupported"/>, the most skeptical class.
/// </summary>
public static class PlanAuditVocabulary
{
    public static PlanAuditSeverity ParseSeverity(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "blocker" => PlanAuditSeverity.Blocker,
        "major" => PlanAuditSeverity.Major,
        "minor" => PlanAuditSeverity.Minor,
        "info" => PlanAuditSeverity.Info,
        _ => PlanAuditSeverity.Blocker,
    };

    public static PlanEvidenceClass ParseGrounding(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "observed" => PlanEvidenceClass.Observed,
        "inferred" => PlanEvidenceClass.Inferred,
        "proposed" => PlanEvidenceClass.Proposed,
        _ => PlanEvidenceClass.Unsupported,
    };

    /// <summary>Maps a plan-audit severity onto the pipeline's audit severity.</summary>
    /// <remarks>
    /// Only <see cref="PlanAuditSeverity.Blocker"/> becomes
    /// <see cref="AuditSeverity.Error"/> (the blocking level); MAJOR becomes a
    /// non-blocking Warning; MINOR/INFO become Info. This is the single point
    /// where the "only a BLOCKER blocks" calibration is encoded.
    /// </remarks>
    public static AuditSeverity ToAuditSeverity(PlanAuditSeverity severity) => severity switch
    {
        PlanAuditSeverity.Blocker => AuditSeverity.Error,
        PlanAuditSeverity.Major => AuditSeverity.Warning,
        _ => AuditSeverity.Info,
    };
}

/// <summary>A single plan-audit finding with its evidence classification.</summary>
public sealed record PlanAuditFinding(
    string Criterion,
    PlanAuditSeverity Severity,
    PlanEvidenceClass Grounding,
    string Title,
    string Description,
    string? EvidenceFromPlan,
    string? RequiredFix);

/// <summary>A criterion this test self-skipped for the plan under review, with a reason.</summary>
public sealed record PlanAuditNotApplicable(string Criterion, string Reason);

/// <summary>
/// The structured verdict a plan-audit test emits for one plan. Carries the
/// shared reviewer-framework output fields: FINDINGS (with SEVERITY, grounding,
/// EVIDENCE_FROM_PLAN, REQUIRED_FIXES), the self-skipped criteria, and
/// OPEN_QUESTIONS. STATUS and overall SEVERITY are derived, not stored, so they
/// cannot disagree with the findings.
/// </summary>
public sealed record PlanAuditVerdict(
    IReadOnlyList<PlanAuditFinding> Findings,
    IReadOnlyList<PlanAuditNotApplicable> NotApplicable,
    IReadOnlyList<string> OpenQuestions)
{
    /// <summary>True iff at least one finding is a BLOCKER — the independent hard gate.</summary>
    public bool HasBlocker => Findings.Any(f => f.Severity == PlanAuditSeverity.Blocker);

    /// <summary>The mechanically-derived overall status (never a model self-report).</summary>
    public PlanAuditStatus Status
    {
        get
        {
            if (HasBlocker)
                return PlanAuditStatus.Fail;
            if (Findings.Count > 0)
                return PlanAuditStatus.Partial;
            if (NotApplicable.Count > 0)
                return PlanAuditStatus.NotApplicable;
            return PlanAuditStatus.Pass;
        }
    }

    /// <summary>
    /// The most severe finding severity present, or null when there are no
    /// findings. Labels the run for the re-plan; does not feed a cross-auditor
    /// compromise verdict.
    /// </summary>
    public PlanAuditSeverity? OverallSeverity => Findings.Count == 0
        ? null
        : Findings.Min(f => f.Severity); // enum order: Blocker=0 is most severe.
}

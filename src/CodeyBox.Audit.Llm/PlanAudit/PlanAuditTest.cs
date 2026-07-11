namespace CodeyBox.Audit.Llm.PlanAudit;

/// <summary>
/// A single test in the plan-audit chain, described as data. Every test in the
/// chain is one <see cref="PlanAuditTest"/> plugged into the same
/// <see cref="PlanAuditChainAuditor"/> and the same
/// <see cref="PlanAuditChainFramework"/> — so the shared reviewer framework is
/// implemented once and reused, and each test contributes only its verbatim
/// objective, review questions, pass/fail lines, automatic-blocker conditions,
/// required fixes, and the criterion keys a plan may self-skip as NOT_APPLICABLE.
/// </summary>
public sealed record PlanAuditTest
{
    /// <summary>Two-digit chain index, e.g. <c>"01"</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Stable auditor name for logs, findings, and per-project toggling.</summary>
    public required string AuditorName { get; init; }

    /// <summary>Human-facing test title.</summary>
    public required string Title { get; init; }

    /// <summary>The test's Objective line (verbatim from the suite).</summary>
    public required string Objective { get; init; }

    /// <summary>The Review questions the reviewer must answer against the plan.</summary>
    public required string ReviewGuidance { get; init; }

    /// <summary>What a passing plan looks like for this test.</summary>
    public required string PassCriteria { get; init; }

    /// <summary>What a failing plan looks like for this test.</summary>
    public required string FailCriteria { get; init; }

    /// <summary>Conditions that are an automatic BLOCKER regardless of anything else.</summary>
    public required string AutomaticBlocker { get; init; }

    /// <summary>The required fixes a failing plan must apply.</summary>
    public required string RequiredFixes { get; init; }

    /// <summary>
    /// The criterion keys this test evaluates. A specific plan that genuinely
    /// does not touch a criterion self-skips it as NOT_APPLICABLE with a
    /// one-line reason; the reviewer is told to draw N/A entries from this list.
    /// Never bake project-specific N/A into the test — per-project relevance is
    /// the auditor on/off toggle, per-plan relevance is a NOT_APPLICABLE entry.
    /// </summary>
    public required IReadOnlyList<string> Criteria { get; init; }
}

/// <summary>
/// The built-in plan-audit chain tests. TEST 01 is the foundation
/// grounding / anti-hallucination gate; later tests in the chain assume a
/// grounded plan and are added as additional <see cref="PlanAuditTest"/> values.
/// </summary>
public static class PlanAuditTests
{
    /// <summary>Stable name of the TEST 01 auditor (referenced by DI + composition).</summary>
    public const string Test01AuditorName = "plan:integrity-evidence";

    /// <summary>
    /// TEST 01 — PLAN INTEGRITY AND EVIDENCE CLASSIFICATION. Determines whether
    /// the plan is grounded in the actual system rather than hallucinated
    /// structure, generic assumptions, or fake precision.
    /// </summary>
    public static PlanAuditTest Test01 { get; } = new()
    {
        Id = "01",
        AuditorName = Test01AuditorName,
        Title = "PLAN INTEGRITY AND EVIDENCE CLASSIFICATION",
        Objective =
            "Determine whether the plan is grounded in the actual system rather than " +
            "hallucinated structure, generic assumptions, or fake precision.",
        ReviewGuidance = """
            - Does the plan distinguish existing code, inferred behavior, and proposed changes?
            - Does it name the relevant files, modules, services, data stores, APIs, jobs, queues,
              permissions, and external systems it depends on or changes?
            - Are the named files / APIs / schemas / services / commands / dependencies supported by
              the supplied context (repo excerpts, prompt, attached artifacts)?
            - Does the plan identify missing context and its own assumptions explicitly?
            - Does it avoid inventing implementation details not present in the codebase or prompt?
            - Does it avoid line-level or file-level precision unless justified by inspected code?
            """,
        PassCriteria =
            "The plan clearly separates observed facts from proposed changes and assumptions; " +
            "important architectural claims are grounded in the supplied context; unknowns are " +
            "explicitly called out.",
        FailCriteria =
            "The plan invents files / APIs / services / data-models / conventions; treats " +
            "assumptions as established facts; or proposes implementation steps before establishing " +
            "what exists.",
        AutomaticBlocker = """
            Treat as an automatic BLOCKER when the plan:
            - relies on unsupported claims about security, data ownership, public contracts,
              migrations, or production behavior; or
            - makes changes to unidentified or hallucinated components (a file, service, table, or
              API that the supplied context does not show to exist).
            """,
        RequiredFixes = """
            - Classify every referenced artifact as OBSERVED / INFERRED / PROPOSED / UNSUPPORTED.
            - Replace each unsupported implementation claim with an explicit verification step.
            - Add the missing context-gathering steps the plan needs before implementation begins.
            """,
        Criteria =
        [
            "evidence-classification",   // observed vs inferred vs proposed separation
            "artifact-naming",           // names concrete files/modules/services/etc.
            "context-support",           // named artifacts are supported by supplied context
            "assumptions-and-unknowns",  // missing context and assumptions are explicit
            "no-invention",              // no invented implementation details
            "justified-precision",       // no unjustified line/file-level precision
        ],
    };
}

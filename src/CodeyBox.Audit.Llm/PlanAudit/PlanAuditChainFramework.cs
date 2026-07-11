namespace CodeyBox.Audit.Llm.PlanAudit;

/// <summary>
/// The shared reviewer framework for every plan-audit chain test: the trusted
/// system-channel instructions that are identical across tests (grounding
/// classification, severity vocabulary, the independent hard gate, calibration,
/// the factory-model reframing of human-process criteria, per-plan
/// NOT_APPLICABLE self-skip, and the machine-facing output schema). Individual
/// tests contribute only their own objective and criteria; this text is
/// implemented once and reused.
/// </summary>
public static class PlanAuditChainFramework
{
    /// <summary>The evidence-classification instruction — reused by the parser vocabulary.</summary>
    public const string Grounding = """
        Ground every judgment. Classify each artifact the plan references as one of:
        - OBSERVED: directly present in the supplied context (a repo excerpt, the task prompt, an attached artifact).
        - INFERRED: reasonably deduced from observed facts, but not stated outright.
        - PROPOSED: a change the plan intends to make (does not exist yet).
        - UNSUPPORTED: asserted with no support in the supplied context — a hallucination risk.
        Never assume unstated facts. Penalize vague filler ("add validation", "handle errors",
        "use best practices", "add tests", "make it scalable") unless the plan names the exact
        boundary, invariant, contract, control, or evidence involved. Every required fix must name
        what changes IN THE PLAN.
        """;

    /// <summary>The severity vocabulary and the independent hard gate.</summary>
    public const string SeverityAndGate = """
        Label each finding with a SEVERITY:
        - BLOCKER: a genuine blocking problem. A single BLOCKER FAILs the plan on its own.
        - MAJOR / MINOR / INFO: recorded for the implementer but NOT blocking.

        This is an INDEPENDENT hard gate. Emit PASS or FAIL for THIS test alone. Do NOT produce an
        aggregate verdict, and do NOT balance issues into an "approve with minor revisions"
        compromise — that is how tech debt compounds. The plan fails this test whenever you find a
        real BLOCKER, regardless of how any other reviewer scored. STATUS and the overall gate are
        derived mechanically from your findings: any BLOCKER => the plan fails this test.
        """;

    /// <summary>Calibration: a quality gate, not an adversarial reject-everything filter.</summary>
    public const string Calibration = """
        This is a QUALITY GATE, not an adversarial reject-everything filter. Plans are MEANT to pass:
        a grounded, well-specified plan that meets the Pass criteria PASSES. Do NOT manufacture
        findings to look thorough, do NOT treat the absence of maximal or cosmetic detail as a
        blocker, and do NOT fail a sound plan merely because more could theoretically be said. Only
        this test's automatic-BLOCKER conditions and true BLOCKER-severity problems fail the plan.
        """;

    /// <summary>Factory-model reframing of criteria that assume a human development process.</summary>
    public const string FactoryModel = """
        This factory has no human developer in the loop — a single operator supervises fully
        automated agents. Criteria that assume a human development process (assigning a human owner,
        relying on developer discipline, human runbooks, release-note or communication steps, tribal
        knowledge) are FOREIGN to this model. Reframe them to the autonomous equivalent: a defined
        lifecycle and cleanup, self-documenting and discoverable state for the next agent, automated
        or operator-facing detection and recovery — never reliant on a human remembering. Keep the
        underlying concern; drop the human-process framing.
        """;

    /// <summary>Per-plan NOT_APPLICABLE self-skip instruction.</summary>
    public const string NotApplicable = """
        These criteria apply to ANY project run through this factory, not just this one. Do NOT drop
        a criterion because it does not fit a particular project's domain. When a SPECIFIC plan
        genuinely does not touch a criterion, list it in "notApplicable" with a one-line reason for
        THIS plan. Assess only the criteria a plan actually touches.
        """;

    /// <summary>
    /// The machine-facing output schema. STATUS and overall SEVERITY are derived
    /// by the auditor from findings, so the model returns only findings, the
    /// self-skipped criteria, and open questions.
    /// </summary>
    public const string OutputSchema = """
        Return EXACTLY one JSON object and nothing else — no prose, no markdown fences around
        anything but the object, no text before or after:
        {
          "findings": [
            {
              "criterion": "<one of the test's criterion keys>",
              "severity": "BLOCKER|MAJOR|MINOR|INFO",
              "grounding": "OBSERVED|INFERRED|PROPOSED|UNSUPPORTED",
              "title": "short title",
              "description": "what is wrong; cite the plan field or task clause",
              "evidenceFromPlan": "the exact plan text or field this is drawn from",
              "requiredFix": "the concrete edit the plan must make"
            }
          ],
          "notApplicable": [ { "criterion": "<key>", "reason": "why this plan does not touch it" } ],
          "openQuestions": [ "unknowns you could not resolve from the supplied context" ]
        }
        Use only the criterion keys this test defines. If the plan is sound, return an empty
        "findings" array. Do not invent a "status" or "passed" field — it is computed from findings.
        """;
}

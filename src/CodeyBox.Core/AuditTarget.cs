using System.Collections.Frozen;

namespace CodeyBox.Core;

/// <summary>
/// Identifies WHAT an auditor reviews. Treated as an opaque string so new
/// review targets can be added later without recompiling consumers — the same
/// extensibility ethos as <see cref="AgentKind"/> and the knob framework.
/// Construction trims whitespace and canonicalises to lower-case invariant;
/// that canonical value is the persisted identity.
///
/// <para>The framework ships with <see cref="Plan"/> (the reviewable planning
/// artifact) and <see cref="Code"/> (the work-phase diff), but the set is
/// intentionally open: future targets (e.g. a released changelog, a migration
/// script) can be introduced as additional values without changing this
/// type.</para>
/// </summary>
public readonly record struct AuditTarget
{
    private readonly string? _value;

    /// <summary>
    /// The CLR default has no target value. Treat it as unset at nullable
    /// boundaries and reject it in declared target sets so a default struct
    /// cannot silently publish a non-runnable target.
    /// </summary>
    public bool IsDefault => string.IsNullOrWhiteSpace(_value);

    public AuditTarget(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audit target value must be non-empty.", nameof(value));
        _value = value.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Canonical lower-case value, or an empty string for the CLR-default
    /// (unset) struct.
    /// </summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Reviews the structured PLAN artifact before implementation.</summary>
    public static AuditTarget Plan { get; } = new("plan");

    /// <summary>Reviews the work-phase diff (the default target).</summary>
    public static AuditTarget Code { get; } = new("code");

    public override string ToString() => Value;
}

/// <summary>
/// Shared, immutable <see cref="AuditTarget"/> sets. Auditors return one of
/// these from <see cref="IAuditor.Targets"/> to declare where they run; the
/// composer filters by target per phase.
/// </summary>
public static class AuditTargets
{
    /// <summary>The default: an auditor reviews code only.</summary>
    public static IReadOnlySet<AuditTarget> CodeOnly { get; } =
        FrozenSet.ToFrozenSet([AuditTarget.Code]);

    /// <summary>An auditor that reviews plans only.</summary>
    public static IReadOnlySet<AuditTarget> PlanOnly { get; } =
        FrozenSet.ToFrozenSet([AuditTarget.Plan]);

    /// <summary>An auditor that reviews both plans and code.</summary>
    public static IReadOnlySet<AuditTarget> PlanAndCode { get; } =
        FrozenSet.ToFrozenSet([AuditTarget.Plan, AuditTarget.Code]);

    /// <summary>
    /// Builds an immutable target set from the supplied targets. Empty input is
    /// rejected — an auditor that targets nothing would silently never run.
    /// </summary>
    public static IReadOnlySet<AuditTarget> Of(params AuditTarget[] targets)
    {
        if (targets is null || targets.Length == 0)
            throw new ArgumentException("An auditor must declare at least one target.", nameof(targets));
        if (targets.Any(t => t.IsDefault))
            throw new ArgumentException("Audit target values must be non-empty.", nameof(targets));
        return targets.ToFrozenSet();
    }

    /// <summary>
    /// The single source of truth for turning a raw configured target-string
    /// list into a typed target set: an empty/absent list means the default
    /// <see cref="CodeOnly"/>, otherwise each string is canonicalised through
    /// <see cref="AuditTarget(string)"/>. Preset loading and custom-auditor
    /// composition both route through here so the empty-means-code default and
    /// the string-to-target conversion cannot drift apart.
    /// </summary>
    public static IReadOnlySet<AuditTarget> ParseOrCodeOnly(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return CodeOnly;

        var targets = new AuditTarget[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            // AuditTarget's constructor trims and lower-cases; it throws on an
            // empty/whitespace value, which is exactly the rejection we want.
            targets[i] = new AuditTarget(values[i]);
        }

        return Of(targets);
    }
}

/// <summary>
/// The concrete review strategy the pipeline and auditors implement for a given
/// <see cref="AuditTarget"/>. Every module that must branch on a target routes
/// through <see cref="AuditTargetSemantics"/> so the Plan/Code strategy cannot
/// fork across modules, and an as-yet-unhandled future target fails loudly
/// instead of being silently treated as code.
/// </summary>
public enum AuditReviewStrategy
{
    /// <summary>Review the structured PLAN artifact before implementation.</summary>
    PlanReview,

    /// <summary>Review the work-phase diff and enforce build/test gates.</summary>
    CodeReview,
}

/// <summary>
/// Maps an <see cref="AuditTarget"/> to its <see cref="AuditReviewStrategy"/>.
/// This is the single decision point that keeps every auditor and the pipeline
/// agreeing on what a target means; adding a new <see cref="AuditTarget"/>
/// deliberately breaks here until an explicit strategy is defined for it, so no
/// module silently mis-classifies an unhandled target as code.
/// </summary>
public static class AuditTargetSemantics
{
    public static AuditReviewStrategy Classify(AuditTarget target)
    {
        if (target == AuditTarget.Plan)
            return AuditReviewStrategy.PlanReview;
        if (target == AuditTarget.Code)
            return AuditReviewStrategy.CodeReview;

        throw new NotSupportedException(
            $"No audit review strategy is defined for target '{target.Value}'. Add an " +
            "explicit case in AuditTargetSemantics (and every auditor/pipeline branch it " +
            "drives) before composing auditors for it.");
    }

    /// <summary>True when the target reviews the PLAN artifact.</summary>
    public static bool IsPlanReview(AuditTarget target) =>
        Classify(target) == AuditReviewStrategy.PlanReview;

    /// <summary>
    /// True when the target reviews the work-phase diff, which is also the phase
    /// that enforces deterministic build/test gates before dependent auditors.
    /// </summary>
    public static bool IsCodeReview(AuditTarget target) =>
        Classify(target) == AuditReviewStrategy.CodeReview;
}

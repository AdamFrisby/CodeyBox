using System.Collections.Frozen;

namespace CodeyBox.Core;

/// <summary>
/// Identifies WHAT an auditor reviews. Treated as an opaque string so new
/// review targets can be added later without recompiling consumers — the same
/// extensibility ethos as <see cref="AgentKind"/> and the knob framework.
///
/// <para>The framework ships with <see cref="Plan"/> (the reviewable planning
/// artifact) and <see cref="Code"/> (the work-phase diff), but the set is
/// intentionally open: future targets (e.g. a released changelog, a migration
/// script) can be introduced as additional values without changing this
/// type.</para>
/// </summary>
public readonly record struct AuditTarget(string Value)
{
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
        return targets.ToFrozenSet();
    }
}

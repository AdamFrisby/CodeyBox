namespace CodeyBox.Core;

/// <summary>
/// Opt-in capability for auditors that can review a PLAN artifact as text —
/// without a sandbox or a code diff. The plan-review gate composes
/// <see cref="AuditTarget.Plan"/>-target auditors and drives those that
/// implement this seam over the (small, cheap) plan artifact via a text-only
/// model call.
///
/// <para>This is deliberately separate from <see cref="IAuditor.RunAsync"/>:
/// code auditors inspect a working tree, whereas a plan is a short structured
/// document with no repository state to mount. Reusing the same
/// <see cref="AuditTarget"/> declaration keeps "which reviewers run where"
/// driven by a single seam.</para>
/// </summary>
public interface IPlanTextReviewer
{
    /// <summary>
    /// Reviews the plan artifact carried on <paramref name="context"/>
    /// (<see cref="AuditContext.PlanArtifact"/>, with
    /// <see cref="AuditContext.EffectiveTarget"/> == <see cref="AuditTarget.Plan"/>)
    /// using the supplied text-only runner. Returns the auditor's verdict as an
    /// <see cref="AuditResult"/>; blocking findings are surfaced as
    /// <see cref="AuditSeverity.Error"/>.
    /// </summary>
    Task<AuditResult> ReviewPlanAsync(
        AuditContext context,
        ITextOnlyAgentRunner runner,
        AgentCredential? credential,
        CancellationToken ct = default);
}

namespace CodeyBox.Core;

/// <summary>
/// Pure policy for the deployment stage of the cost-ordered audit ladder.
/// The audit loop runs code-stage auditors first; only when that stage is
/// clean does it lazily provision ONE verification deployment from the
/// project's recipe and run deployment-targeted auditors against the live
/// endpoint. All decisions here are pure functions of declared configuration
/// so they are trivially unit-testable; the orchestrator owns the IO
/// (provision, run, teardown).
/// </summary>
public static class DeploymentAuditPolicy
{
    /// <summary>
    /// Fallback deployment lifetime when a hand-built recipe carries a
    /// non-positive <see cref="DeploymentRecipe.MaxLifetime"/>. The config
    /// binder rejects such recipes, but programmatically constructed ones
    /// bypass it — the pipeline must still bound the deployment.
    /// </summary>
    public static readonly TimeSpan FallbackMaxLifetime = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Decides whether the current audit iteration provisions a verification
    /// deployment. Every leg is a cheap-fail gate so a blocking code-stage
    /// failure provisions zero deployments.
    /// </summary>
    /// <param name="deploymentAuditEnabled">
    /// Per-project/per-item toggle (<see cref="ProjectAudit.DeploymentAuditEnabled"/>,
    /// selectable per item through the item's audit profile). False skips the
    /// phase entirely — nothing is provisioned, no recipe config needed.
    /// </param>
    /// <param name="recipe">The project's deployment recipe. Null skips the phase.</param>
    /// <param name="hasDeploymentAuditors">
    /// True when at least one enabled auditor declares
    /// <see cref="AuditTarget.Deployment"/> in its <see cref="IAuditor.Targets"/>.
    /// </param>
    /// <param name="codeStageClean">
    /// True when the code stage reached a complete verdict with zero blocking
    /// findings. Any blocking fail — including the short-circuit gates —
    /// skips deployment provisioning for this iteration.
    /// </param>
    public static DeploymentAuditDecision ShouldProvision(
        bool deploymentAuditEnabled,
        DeploymentRecipe? recipe,
        bool hasDeploymentAuditors,
        bool codeStageClean)
    {
        if (!codeStageClean)
            return new DeploymentAuditDecision(Provision: false, Reason: "code-stage-blocking");
        if (!deploymentAuditEnabled)
            return new DeploymentAuditDecision(Provision: false, Reason: "deployment-auditing-disabled");
        if (recipe is null)
            return new DeploymentAuditDecision(Provision: false, Reason: "no-deployment-recipe");
        if (!hasDeploymentAuditors)
            return new DeploymentAuditDecision(Provision: false, Reason: "no-deployment-auditors");
        return new DeploymentAuditDecision(Provision: true, Reason: "provision");
    }

    /// <summary>
    /// The enforced lifetime cap for a provisioned deployment: the recipe's
    /// <see cref="DeploymentRecipe.MaxLifetime"/> when positive, otherwise
    /// <see cref="FallbackMaxLifetime"/>. The deployment scope disposes the
    /// handle no later than start + this value on every exit path.
    /// </summary>
    public static TimeSpan EffectiveLifetime(DeploymentRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return recipe.MaxLifetime > TimeSpan.Zero ? recipe.MaxLifetime : FallbackMaxLifetime;
    }

    /// <summary>
    /// Absolute deadline for a deployment started at <paramref name="startedAt"/>.
    /// </summary>
    public static DateTimeOffset DeadlineFor(DeploymentRecipe recipe, DateTimeOffset startedAt) =>
        startedAt + EffectiveLifetime(recipe);
}

/// <summary>Outcome of <see cref="DeploymentAuditPolicy.ShouldProvision"/>.</summary>
/// <param name="Provision">True when the iteration must provision exactly one deployment.</param>
/// <param name="Reason">
/// Machine-readable reason: <c>provision</c>, <c>code-stage-blocking</c>,
/// <c>deployment-auditing-disabled</c>, <c>no-deployment-recipe</c>, or
/// <c>no-deployment-auditors</c>.
/// </param>
public sealed record DeploymentAuditDecision(bool Provision, string Reason);

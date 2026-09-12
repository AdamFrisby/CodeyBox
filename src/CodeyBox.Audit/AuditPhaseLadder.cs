using CodeyBox.Core;

namespace CodeyBox.Audit;

/// <summary>
/// The cost-ordered audit ladder: code-stage auditors first (mechanical/tool
/// gates, then LLM reviewers), then — only when the code stage is clean and
/// deployment-targeted auditors are enabled — exactly one lazily-provisioned
/// deployment stage. Phase membership derives from each auditor's declared
/// <see cref="IAuditor.Targets"/> and cost class derives from declared
/// capabilities (<see cref="IAuditor.Role"/>, <see cref="IAuditor.Required"/>,
/// <see cref="IAuditor.Kind"/>): no concrete-type switches anywhere, so a new
/// auditor opts into a phase or cost class by declaration alone.
/// </summary>
public static class AuditPhaseLadder
{
    /// <summary>
    /// Declared cost class of an auditor. Mechanical gates are cheapest
    /// (deterministic, no model spend), tool auditors are cheap (local
    /// execution), reviewers are expensive (model quota / network).
    /// </summary>
    public static AuditCostClass CostClassOf(IAuditor auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        if (auditor.Role == AuditorRole.BuildTestGate)
            return AuditCostClass.MechanicalGate;
        if (auditor.Required.HasFlag(AuditCapabilities.AgentCredentials)
            || string.Equals(auditor.Kind, "llm", StringComparison.OrdinalIgnoreCase))
            return AuditCostClass.Reviewer;
        return AuditCostClass.Tool;
    }

    /// <summary>
    /// True when the auditor declares <see cref="AuditTarget.Code"/> and
    /// therefore runs in the code stage.
    /// </summary>
    public static bool RunsInCodeStage(IAuditor auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        return auditor.Targets.Contains(AuditTarget.Code);
    }

    /// <summary>
    /// True when the auditor declares <see cref="AuditTarget.Deployment"/>
    /// and therefore runs in the deployment stage against the live endpoint.
    /// An auditor declaring both code and deployment runs in both stages.
    /// </summary>
    public static bool RunsInDeploymentStage(IAuditor auditor)
    {
        ArgumentNullException.ThrowIfNull(auditor);
        return auditor.Targets.Contains(AuditTarget.Deployment);
    }

    /// <summary>
    /// Code-stage auditors in stable registration order. The pipeline applies
    /// <see cref="AuditorOrdering.TierOf"/> per iteration for cheap-first
    /// execution; the split itself is purely target-based.
    /// </summary>
    public static IReadOnlyList<IAuditor> SelectCodeStage(IEnumerable<IAuditor> auditors)
    {
        ArgumentNullException.ThrowIfNull(auditors);
        return auditors.Where(RunsInCodeStage).ToList();
    }

    /// <summary>
    /// Deployment-stage auditors in stable registration order. The pipeline
    /// applies <see cref="AuditorOrdering.TierOf"/> so tool smoke/health
    /// probes (cheap) run before LLM/CUA exploration (expensive) within the
    /// stage, mirroring the code-stage cost order.
    /// </summary>
    public static IReadOnlyList<IAuditor> SelectDeploymentStage(IEnumerable<IAuditor> auditors)
    {
        ArgumentNullException.ThrowIfNull(auditors);
        return auditors.Where(RunsInDeploymentStage).ToList();
    }

    /// <summary>
    /// Deployment-stage auditors ordered cheap-first via
    /// <see cref="AuditorOrdering.TierOf"/> (stable within a tier). This is
    /// the deployment-stage analogue of the code-stage short-circuit
    /// ordering: smoke/health probes run before quota-spending exploration.
    /// </summary>
    public static IReadOnlyList<IAuditor> OrderDeploymentStage(IEnumerable<IAuditor> auditors)
    {
        ArgumentNullException.ThrowIfNull(auditors);
        return auditors
            .Select((auditor, index) => new { Auditor = auditor, Index = index })
            .OrderBy(x => AuditorOrdering.TierOf(x.Auditor))
            .ThenBy(x => x.Index)
            .Select(x => x.Auditor)
            .ToList();
    }
}

/// <summary>
/// Declared cost class of an auditor in the audit ladder. Derived from
/// declared capabilities only — never from the auditor's concrete type.
/// </summary>
public enum AuditCostClass
{
    /// <summary>Deterministic build/test gates. Cheapest: no model spend.</summary>
    MechanicalGate,

    /// <summary>Local tool execution (linters, scanners, smoke probes). Cheap.</summary>
    Tool,

    /// <summary>Model-backed reviewers (LLM/CUA exploration). Expensive.</summary>
    Reviewer,
}

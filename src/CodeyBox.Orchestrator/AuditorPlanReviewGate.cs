using CodeyBox.Core;
using CodeyBox.Projects;
using Microsoft.Extensions.Logging;

namespace CodeyBox.Orchestrator;

/// <summary>
/// Compatibility plan-review gate registered by production DI. The actual
/// auditor-backed plan review loop runs inside <see cref="PipelineRunner"/>,
/// where the selected audit profile, audit-agent routing, member credentials,
/// smoke gates, quota gates, and sandbox execution path already live. This
/// gate keeps the public <see cref="IPlanReviewGate"/> hook available for
/// structural validation and for tests/custom deployments that inject a
/// different gate.
/// </summary>
public sealed class AuditorPlanReviewGate : IPlanReviewGate
{
    private readonly ILogger<AuditorPlanReviewGate> _log;

    public AuditorPlanReviewGate(
        ProjectAuditorComposer composer,
        IProjectRepository projects,
        IAgentRegistry agents,
        ICredentialProvider credentials,
        ILogger<AuditorPlanReviewGate> log)
    {
        _ = composer;
        _ = projects;
        _ = agents;
        _ = credentials;
        _log = log;
    }

    public ValueTask<PlanReviewDecision> ReviewAsync(
        PlanReviewRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = PlanArtifactDocument.ParseCanonical(request.PlanArtifact);

        _log.LogDebug(
            "Compatibility plan-review gate validated plan artifact for work item {WorkItemId}; Plan-target auditors are executed by PipelineRunner.",
            request.WorkItemId);
        return ValueTask.FromResult(new PlanReviewDecision(
            true,
            "Plan-review compatibility gate approved on artifact validity."));
    }
}

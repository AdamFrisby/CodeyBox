using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public sealed class AlwaysPassPlanReviewGate : IPlanReviewGate
{
    public ValueTask<PlanReviewDecision> ReviewAsync(
        PlanReviewRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _ = PlanArtifactDocument.ParseCanonical(request.PlanArtifact);
        return ValueTask.FromResult(new PlanReviewDecision(true, "Placeholder plan review approved."));
    }
}

using CodeyBox.Core.ExternalBuilds;

namespace CodeyBox.Orchestrator.ExternalBuilds;

/// <summary>
/// Merge/audit gate over neutral external-build evidence. Pass requires
/// sufficient authoritative evidence for the exact merge-result tree and the
/// current expected base; the existing merge-queue compare-and-set behavior
/// is preserved by taking both digests as explicit inputs. Missing, skipped,
/// unavailable, or uncertain evidence never counts as pass.
/// </summary>
public static class ExternalBuildEvidenceGate
{
    public sealed record GateResult(bool Passed, string Reason);

    public static GateResult Check(
        ExternalBuildRecord? build,
        string expectedMergeTreeDigest,
        string currentBaseDigest)
    {
        if (build is null)
            return new GateResult(false, "no external build bound to this candidate");
        if (ExternalBuildParkCoordinator.IsEvidenceOutstanding(build))
            return new GateResult(false, $"required evidence outstanding (state {build.State})");
        var verdict = ExternalBuildEvidenceEvaluator.Evaluate(
            build.Evidence, build, expectedMergeTreeDigest, currentBaseDigest);
        return new GateResult(verdict.Passed, verdict.Reason);
    }
}

/// <summary>Options validation entry point (hot-reload guards call this).</summary>
public static class ExternalBuildOptionsValidation
{
    public static string? Validate(ExternalBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ExternalBuildOptions.IsValid(options))
            return $"{ExternalBuildOptions.SectionName} has out-of-range values.";
        foreach (var (name, approval) in options.ApprovedTargets)
        {
            if (string.IsNullOrWhiteSpace(approval.ProviderId)
                || string.IsNullOrWhiteSpace(approval.TargetId))
                return $"Approved target '{name}' is missing provider or target identity.";
            if (!approval.AllowGitPublication && !approval.AllowSnapshotUpload)
                return $"Approved target '{name}' allows neither git nor snapshot delivery.";
        }
        return null;
    }
}

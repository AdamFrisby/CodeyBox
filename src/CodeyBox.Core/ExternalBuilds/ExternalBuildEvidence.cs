namespace CodeyBox.Core.ExternalBuilds;

/// <summary>Outcome of one neutral evidence dimension.</summary>
public enum ExternalBuildDimensionOutcome
{
    Unknown = 0,
    Passed = 1,
    Failed = 2,
    NotRun = 3,
}

/// <summary>
/// Independently represented compile, test and package/artifact outcomes plus
/// the authoritative identity the evidence was captured from. Language- and
/// ecosystem-neutral: no .NET/MSBuild/Unity fields.
/// </summary>
public sealed record ExternalBuildEvidence
{
    public required ExternalBuildDimensionOutcome Compile { get; init; }
    public required ExternalBuildDimensionOutcome Tests { get; init; }
    public required ExternalBuildDimensionOutcome Package { get; init; }
    /// <summary>Exact source digest the provider built.</summary>
    public required string SourceDigestSha256 { get; init; }
    /// <summary>Provider-assigned run identity.</summary>
    public required string ProviderRunId { get; init; }
    /// <summary>Approved workflow/target identity the provider executed.</summary>
    public required string WorkflowIdentity { get; init; }
    /// <summary>Approved target name selected at dispatch.</summary>
    public required string ApprovedTargetName { get; init; }
    /// <summary>Opaue toolchain descriptor (adapter-owned, neutral here).</summary>
    public string Toolchain { get; init; } = string.Empty;
    /// <summary>Opaque platform descriptor (adapter-owned, neutral here).</summary>
    public string Platform { get; init; } = string.Empty;
    /// <summary>Configuration profile executed.</summary>
    public string Configuration { get; init; } = string.Empty;
    /// <summary>Artifact digests (name -&gt; sha256).</summary>
    public IReadOnlyDictionary<string, string> ArtifactDigests { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    /// <summary>True when the provider attests this evidence is authoritative (not a model summary).</summary>
    public bool Authoritative { get; init; }
    public DateTimeOffset CapturedAt { get; init; }
}

/// <summary>Verdict of the neutral evidence gate.</summary>
public sealed record ExternalBuildEvidenceVerdict(bool Passed, string Reason);

/// <summary>
/// Pure neutral evidence evaluator. Pass requires sufficient authoritative
/// evidence: compile passed, tests passed (compilation masquerading as tests
/// never counts), package outcome known, exact source/run/workflow identity
/// match, and artifact digests present when package passed. Missing, stale,
/// substituted, or model-summarized evidence never passes.
/// </summary>
public static class ExternalBuildEvidenceEvaluator
{
    public static ExternalBuildEvidenceVerdict Evaluate(
        ExternalBuildEvidence? evidence,
        ExternalBuildRecord build,
        string expectedMergeTreeDigest,
        string currentBaseDigest)
    {
        if (evidence is null)
            return new(false, "no evidence captured");
        if (!evidence.Authoritative)
            return new(false, "evidence is not provider-authoritative");
        if (!string.Equals(evidence.ProviderRunId, build.ProviderRunId, StringComparison.Ordinal))
            return new(false, "evidence run identity does not match dispatched run");
        if (!string.Equals(evidence.SourceDigestSha256, build.Source.SourceDigestSha256, StringComparison.Ordinal))
            return new(false, "evidence source digest does not match frozen snapshot (stale or substituted)");
        if (!string.Equals(evidence.SourceDigestSha256, expectedMergeTreeDigest, StringComparison.Ordinal))
            return new(false, "evidence is not for the exact merge-result tree");
        if (!string.Equals(build.Source.BaseDigestSha256, currentBaseDigest, StringComparison.Ordinal))
            return new(false, "base moved since dispatch; rebase and rebuild");
        if (evidence.Compile != ExternalBuildDimensionOutcome.Passed)
            return new(false, "compile outcome is not Passed");
        if (evidence.Tests != ExternalBuildDimensionOutcome.Passed)
            return new(false, "test outcome is not Passed (absent tests never count as pass)");
        if (evidence.Package is ExternalBuildDimensionOutcome.Unknown)
            return new(false, "package outcome unknown");
        if (evidence.Package == ExternalBuildDimensionOutcome.Passed && evidence.ArtifactDigests.Count == 0)
            return new(false, "package passed but no artifact digests captured");
        return new(true, "authoritative compile+test evidence for the exact merge tree");
    }

    /// <summary>
    /// Exploratory results are reusable only under exact identity/policy match:
    /// same source digest, same approved target, same configuration, and the
    /// result must itself be authoritative and passing.
    /// </summary>
    public static bool IsExploratoryReusable(
        ExternalBuildEvidence exploratory,
        ExternalBuildRecord build)
    {
        ArgumentNullException.ThrowIfNull(exploratory);
        ArgumentNullException.ThrowIfNull(build);
        return exploratory.Authoritative
            && exploratory.Compile == ExternalBuildDimensionOutcome.Passed
            && exploratory.Tests == ExternalBuildDimensionOutcome.Passed
            && string.Equals(exploratory.SourceDigestSha256, build.Source.SourceDigestSha256, StringComparison.Ordinal)
            && string.Equals(exploratory.ApprovedTargetName, build.Target.TargetId, StringComparison.Ordinal)
            && string.Equals(exploratory.Configuration, build.Target.Configuration, StringComparison.Ordinal);
    }

    /// <summary>Any edit, configuration change, or rework attempt invalidates prior evidence.</summary>
    public static bool IsInvalidatedByEdit(string evidenceSourceDigest, string currentSourceDigest) =>
        !string.Equals(evidenceSourceDigest, currentSourceDigest, StringComparison.Ordinal);
}

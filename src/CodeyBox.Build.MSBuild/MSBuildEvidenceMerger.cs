using CodeyBox.Core;

namespace CodeyBox.Build.MSBuild;

/// <summary>
/// Pure merge for per-file binlog evidence into one attempt-level
/// <see cref="BuildDiagnosticsEvidence"/>. One build attempt yields one
/// binlog per built target; the repair loop needs a single causal view.
/// Re-applies the producer bounds (diagnostic and artifact-ref caps) and
/// re-links causes across the merged set. All inputs must carry the same
/// source binding; a mismatch is a programmer error and fails fast.
/// </summary>
public static class MSBuildEvidenceMerger
{
    private const int MaxArtifactRefs = 16;

    public static BuildDiagnosticsEvidence Merge(
        IReadOnlyList<BuildDiagnosticsEvidence> parts,
        BuildDiagnosticsSourceBinding binding,
        MSBuildDiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(options);

        if (parts.Count == 0)
            return BuildDiagnosticsEvidence.Insufficient(
                MSBuildDiagnosticsOptions.ProviderId, binding, "missing: no binary log was collected from the build");

        foreach (var part in parts)
        {
            if (!binding.Matches(part.SourceBinding))
                throw new ArgumentException(
                    $"Refusing to merge diagnostics evidence bound to {part.SourceBinding.Describe()} " +
                    $"into attempt {binding.Describe()}: stale or substituted evidence.",
                    nameof(parts));
        }

        var enriched = parts.Where(static p => p.Status == BuildDiagnosticsStatus.Enriched).ToArray();
        if (enriched.Length == 0)
        {
            var reason = parts.FirstOrDefault(static p => !string.IsNullOrWhiteSpace(p.Reason))?.Reason
                ?? "no usable diagnostics in any collected log";
            return BuildDiagnosticsEvidence.Insufficient(MSBuildDiagnosticsOptions.ProviderId, binding, reason);
        }

        var errors = new List<BuildDiagnostic>();
        var warnings = new List<BuildDiagnostic>();
        var truncated = false;
        foreach (var part in enriched)
        {
            truncated |= part.Truncated;
            errors.AddRange(part.Diagnostics.Where(static d => d.Severity == BuildDiagnosticSeverity.Error));
            warnings.AddRange(part.Diagnostics.Where(static d => d.Severity != BuildDiagnosticSeverity.Error));
        }

        var retained = new List<BuildDiagnostic>(options.MaxDiagnostics);
        foreach (var error in errors)
        {
            if (retained.Count >= options.MaxDiagnostics) break;
            retained.Add(error);
        }
        foreach (var warning in warnings)
        {
            if (retained.Count >= options.MaxDiagnostics) break;
            retained.Add(warning);
        }
        if (errors.Count + warnings.Count > retained.Count)
            truncated = true;

        MSBuildBinlogParser.LinkCauses(retained);

        var artifacts = enriched
            .SelectMany(static p => p.ArtifactRefs)
            .Take(MaxArtifactRefs)
            .ToArray();
        truncated |= enriched.Sum(static p => p.ArtifactRefs.Count) > artifacts.Length;

        return new BuildDiagnosticsEvidence
        {
            ProviderId = MSBuildDiagnosticsOptions.ProviderId,
            SourceBinding = binding,
            Status = BuildDiagnosticsStatus.Enriched,
            Diagnostics = retained,
            RootCauseIds = retained.Where(static d => d.IsRootCause).Select(static d => d.Id).ToArray(),
            TotalErrorCount = enriched.Sum(static p => p.TotalErrorCount),
            TotalWarningCount = enriched.Sum(static p => p.TotalWarningCount),
            Truncated = truncated,
            ArtifactRefs = artifacts,
        };
    }
}

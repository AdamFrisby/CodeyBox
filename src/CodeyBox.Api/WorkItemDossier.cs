using CodeyBox.Core;

namespace CodeyBox.Api;

/// <summary>
/// Outcome vocabulary for dossier artifacts. A result that never ran is
/// <c>not_run</c> — never <c>pass</c>. Skipped, cancelled and inconclusive
/// results keep their own names so a reviewer can tell them apart from green.
/// </summary>
public static class DossierOutcomes
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string NotRun = "not_run";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";
    public const string Inconclusive = "inconclusive";
}

/// <summary>One named deliverable-or-check in the dossier.</summary>
public sealed record DossierArtifactDto(
    string Name,
    string Phase,
    string ProducedBy,
    string Outcome,
    bool IsStale,
    string Summary);

/// <summary>Diff summary for the change itself.</summary>
public sealed record DossierChangeDto(
    string? BaseBranch,
    string? WorkBranch,
    string? BaseCommitSha,
    string? WorkCommitSha,
    int FilesChanged,
    long LinesAdded,
    long LinesRemoved,
    IReadOnlyList<string> ChangedFiles,
    bool Truncated,
    string Outcome);

/// <summary>
/// Publication state as three distinct facts. A local merge is not a
/// published change: each leg carries its own state.
/// </summary>
public sealed record DossierPublicationDto(
    DossierLocalMergeDto LocalMerge,
    DossierOpenPrDto OpenPr,
    DossierMergedPrDto MergedPr);

public sealed record DossierLocalMergeDto(string State, string? Sha);

public sealed record DossierOpenPrDto(string State, int? Number, string? Url);

public sealed record DossierMergedPrDto(string State, int? Number, string? Url, string? MergeSha);

/// <summary>Build/test gate roll-up. Absent evidence is <c>not_run</c>.</summary>
public sealed record DossierGateDto(string Name, string Outcome, bool IsStale, string Summary);

public sealed record DossierCostDto(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    double EstimatedUsd,
    long ElapsedMs,
    int InvocationCount);

/// <summary>
/// Per-item delivery dossier. <see cref="DossierLink"/> is stable:
/// <c>/workitems/{id}/dossier</c> for every revision of the item.
/// </summary>
public sealed record WorkItemDossierDto(
    string WorkItemId,
    string Title,
    string State,
    string ProjectId,
    string DossierLink,
    int PromptRevision,
    string OverallStatus,
    DossierChangeDto Change,
    DossierPublicationDto Publication,
    IReadOnlyList<DossierGateDto> Gates,
    IReadOnlyList<DossierArtifactDto> Artifacts,
    DossierCostDto Costs,
    long DurationMs);

/// <summary>
/// Pure assembler for the per-item dossier. All inputs are plain values so
/// the mapping is unit-testable without stores or git.
/// </summary>
internal static class WorkItemDossierBuilder
{
    internal const int MaxChangedFilesListed = 200;

    internal static string BuildLink(WorkItemId id) => $"/workitems/{id}/dossier";

    internal static WorkItemDossierDto Build(
        WorkItem item,
        IReadOnlyList<AuditReport> reports,
        IReadOnlyList<WorkItemCost> costs,
        IReadOnlyList<TimingRecord> timings,
        IReadOnlyList<WorkItemIteration> iterations,
        DossierDiffInput? diff,
        string? upstreamPrStatus,
        string? upstreamPrMergeSha)
    {
        var currentRevision = item.PromptRevision;
        var workAgent = item.Agent?.Value
            ?? item.AgentInstanceId
            ?? "unknown-agent";

        var artifacts = new List<DossierArtifactDto>();

        var changeOutcome = diff is null
            ? DossierOutcomes.NotRun
            : diff.FilesChanged == 0
                ? DossierOutcomes.Inconclusive
                : DossierOutcomes.Pass;
        var change = new DossierChangeDto(
            item.BaseBranch,
            item.WorkBranch,
            diff?.BaseCommitSha,
            diff?.WorkCommitSha,
            diff?.FilesChanged ?? 0,
            diff?.LinesAdded ?? 0,
            diff?.LinesRemoved ?? 0,
            diff?.ChangedFiles ?? Array.Empty<string>(),
            diff?.Truncated ?? false,
            changeOutcome);
        artifacts.Add(new DossierArtifactDto(
            "change:diff-summary",
            "work",
            workAgent,
            changeOutcome,
            IsStale: false,
            changeOutcome == DossierOutcomes.Pass
                ? $"{change.FilesChanged} files, +{change.LinesAdded}/-{change.LinesRemoved}"
                : "No diff available yet."));

        var orderedReports = reports
            .OrderBy(r => r.Target.Value, StringComparer.Ordinal)
            .ThenBy(r => r.Iteration)
            .ThenBy(r => r.AuditorName, StringComparer.Ordinal)
            .ToList();

        foreach (var report in orderedReports)
        {
            var outcome = report.WorstSeverity.Equals("Error", StringComparison.OrdinalIgnoreCase)
                ? DossierOutcomes.Fail
                : report.WorstSeverity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
                    ? DossierOutcomes.Pass
                    : report.Findings.Count == 0
                        ? DossierOutcomes.Pass
                        : DossierOutcomes.Inconclusive;
            var stale = IsStale(report.StartedAt, iterations, currentRevision);
            artifacts.Add(new DossierArtifactDto(
                $"audit:{report.Target.Value}:iter-{report.Iteration}:{report.AuditorName}",
                "audit",
                $"{report.AuditorName} ({report.AuditorKind})",
                outcome,
                stale,
                $"{report.Findings.Count} findings, worst={report.WorstSeverity}."));
        }

        var buildGate = BuildGate("build", orderedReports, iterations, currentRevision);
        var testGate = BuildGate("test", orderedReports, iterations, currentRevision);
        artifacts.Add(new DossierArtifactDto(
            "gate:build", buildGate.Phase, buildGate.ProducedBy,
            buildGate.Outcome, buildGate.IsStale, buildGate.Summary));
        artifacts.Add(new DossierArtifactDto(
            "gate:test", testGate.Phase, testGate.ProducedBy,
            testGate.Outcome, testGate.IsStale, testGate.Summary));

        foreach (var cost in costs.OrderBy(c => c.StartedAt))
        {
            artifacts.Add(new DossierArtifactDto(
                $"cost:{cost.Phase}:{cost.Id}",
                cost.Phase,
                string.IsNullOrWhiteSpace(cost.ModelId)
                    ? cost.AgentKind
                    : $"{cost.AgentKind}/{cost.ModelId}",
                DossierOutcomes.Pass,
                IsStale(cost.StartedAt, iterations, currentRevision),
                $"{cost.InputTokens + cost.CachedInputTokens} in / {cost.OutputTokens} out, ${cost.EstimatedUsd:F4}."));
        }

        foreach (var timing in timings
                     .Where(t => t.DurationMs.HasValue)
                     .OrderBy(t => t.StartedAt))
        {
            artifacts.Add(new DossierArtifactDto(
                $"timing:{timing.Phase}:{timing.Step}",
                timing.Phase,
                "pipeline-clock",
                DossierOutcomes.Pass,
                IsStale(timing.StartedAt, iterations, currentRevision),
                $"{timing.DurationMs}ms."));
        }

        var localState = item.MergeSha is not null || item.LocalSquashSha is not null
            ? "merged"
            : DossierOutcomes.NotRun;
        var openState = upstreamPrStatus is not null &&
                        upstreamPrStatus.Equals("open", StringComparison.OrdinalIgnoreCase)
            ? "open"
            : "none";
        var mergedState = upstreamPrStatus is not null &&
                          upstreamPrStatus.Equals("merged", StringComparison.OrdinalIgnoreCase)
            ? "merged"
            : item.MergedPrNumber is > 0 && upstreamPrMergeSha is not null
                ? "merged"
                : "none";
        var publication = new DossierPublicationDto(
            new DossierLocalMergeDto(localState, item.MergeSha ?? item.LocalSquashSha),
            new DossierOpenPrDto(openState, item.MergedPrNumber, item.MergedPrUrl),
            new DossierMergedPrDto(mergedState, item.MergedPrNumber, item.MergedPrUrl, upstreamPrMergeSha));
        artifacts.Add(new DossierArtifactDto(
            "publication:local-merge", "merge", "merge-phase",
            localState == "merged" ? DossierOutcomes.Pass : DossierOutcomes.NotRun,
            IsStale: false,
            localState == "merged" ? $"Local merge {publication.LocalMerge.Sha}." : "Not merged locally yet."));
        artifacts.Add(new DossierArtifactDto(
            "publication:open-pr", "upstream", "upstream-remote",
            openState == "open" ? DossierOutcomes.Pass : DossierOutcomes.NotRun,
            IsStale: false,
            openState == "open" ? $"Open PR #{item.MergedPrNumber}." : "No open PR."));
        artifacts.Add(new DossierArtifactDto(
            "publication:merged-pr", "upstream", "upstream-remote",
            mergedState == "merged" ? DossierOutcomes.Pass : DossierOutcomes.NotRun,
            IsStale: false,
            mergedState == "merged" ? $"Merged PR #{item.MergedPrNumber}." : "No merged PR."));

        var gates = new List<DossierGateDto>
        {
            new("build", buildGate.Outcome, buildGate.IsStale, buildGate.Summary),
            new("test", testGate.Outcome, testGate.IsStale, testGate.Outcome == DossierOutcomes.NotRun
                ? "Test gate did not run."
                : testGate.Summary),
        };

        var totalInput = costs.Sum(c => (long)c.InputTokens + c.CachedInputTokens);
        var totalCached = costs.Sum(c => (long)c.CachedInputTokens);
        var totalOutput = costs.Sum(c => (long)c.OutputTokens);
        var totalUsd = costs.Sum(c => c.EstimatedUsd);
        var totalElapsed = costs.Sum(c => (long)Math.Max(0, (c.EndedAt - c.StartedAt).TotalMilliseconds));
        var costDto = new DossierCostDto(totalInput, totalCached, totalOutput, totalUsd, totalElapsed, costs.Count);

        var durationMs = timings
            .Where(t => t.DurationMs.HasValue)
            .Sum(t => t.DurationMs!.Value);

        var overall = ComputeOverall(changeOutcome, gates, orderedReports, iterations, currentRevision);

        return new WorkItemDossierDto(
            item.Id.ToString(),
            item.Title,
            item.State.ToString(),
            item.ProjectId.Value,
            BuildLink(item.Id),
            currentRevision,
            overall,
            change,
            publication,
            gates,
            artifacts,
            costDto,
            durationMs);
    }

    private static string ComputeOverall(
        string changeOutcome,
        IReadOnlyList<DossierGateDto> gates,
        IReadOnlyList<AuditReport> reports,
        IReadOnlyList<WorkItemIteration> iterations,
        int currentRevision)
    {
        // Only fresh evidence speaks for the current candidate: a superseded
        // failure is not proof the current revision fails, and a superseded
        // pass is not proof it is sound. Stale results stay visible as
        // artifacts; they just cannot carry the overall verdict.
        var freshReports = reports
            .Where(r => !IsStale(r.StartedAt, iterations, currentRevision))
            .ToList();
        if (freshReports.Any(r => r.WorstSeverity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            return "failing";
        if (changeOutcome == DossierOutcomes.NotRun)
            return "incomplete";
        if (gates.Any(g => g.Outcome is DossierOutcomes.NotRun or DossierOutcomes.Skipped
                or DossierOutcomes.Cancelled or DossierOutcomes.Inconclusive))
            return "incomplete";
        if (gates.Any(g => g.IsStale))
            return "incomplete";
        if (reports.Any(r => r.WorstSeverity.Equals("Error", StringComparison.OrdinalIgnoreCase)))
            return "incomplete";
        return "fully_passing";
    }

    private sealed record GateParts(string Phase, string ProducedBy, string Outcome, bool IsStale, string Summary);

    private static GateParts BuildGate(
        string gate,
        IReadOnlyList<AuditReport> reports,
        IReadOnlyList<WorkItemIteration> iterations,
        int currentRevision)
    {
        var matching = reports.Where(r => NameMatchesGate(r.AuditorName, gate)).ToList();
        if (matching.Count == 0)
        {
            return new GateParts(
                "audit",
                $"{gate}-gate (no matching auditor ran)",
                DossierOutcomes.NotRun,
                IsStale: false,
                $"No {gate} auditor has reported for this item.");
        }

        var worst = matching.Any(r => r.WorstSeverity.Equals("Error", StringComparison.OrdinalIgnoreCase))
            ? DossierOutcomes.Fail
            : DossierOutcomes.Pass;
        var stale = matching.All(r => IsStale(r.StartedAt, iterations, currentRevision));
        var names = string.Join(", ", matching.Select(r => r.AuditorName).Distinct(StringComparer.Ordinal));
        return new GateParts(
            "audit",
            names,
            worst,
            stale,
            $"{matching.Count} {gate} report(s), worst={worst}.");
    }

    /// <summary>
    /// Matches an auditor to a build/test gate on the <c>{scope}:{role}</c>
    /// naming convention (<c>csharp:test-pass</c>, <c>csharp:build-WaE</c>,
    /// <c>repo-build:test-pass</c>). A colon-separated segment matches when it
    /// is the gate word or carries it before/after a dash, so a bare
    /// substring cannot false-positive on names like <c>latest</c> or
    /// <c>contest</c> (both merely end in "test").
    /// </summary>
    private static bool NameMatchesGate(string auditorName, string gate) =>
        auditorName
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(segment =>
                segment.Equals(gate, StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith(gate + "-", StringComparison.OrdinalIgnoreCase)
                || segment.EndsWith("-" + gate, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A result is stale when it started before the dispatch of the current
    /// prompt revision: it verified a superseded candidate, not the current one.
    /// With no dispatch history the revision is unknown, so nothing is stale.
    /// </summary>
    internal static bool IsStale(
        DateTimeOffset producedAt,
        IReadOnlyList<WorkItemIteration> iterations,
        int currentRevision)
    {
        var covering = iterations
            .Where(i => i.DispatchedAt <= producedAt)
            .OrderByDescending(i => i.DispatchedAt)
            .FirstOrDefault();
        if (covering is null)
            return false;
        return covering.PromptRevisionAtDispatch < currentRevision;
    }
}

/// <summary>Bounded diff summary input for the dossier builder.</summary>
public sealed record DossierDiffInput(
    string? BaseCommitSha,
    string? WorkCommitSha,
    int FilesChanged,
    long LinesAdded,
    long LinesRemoved,
    IReadOnlyList<string> ChangedFiles,
    bool Truncated);

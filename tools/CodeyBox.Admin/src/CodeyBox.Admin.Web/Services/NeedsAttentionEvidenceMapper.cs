using CodeyBox.Admin.Model;
using CodeyBox.Admin.Web.Models;

namespace CodeyBox.Admin.Web.Services;

/// <summary>
/// Maps orchestrator REST DTOs onto the pure needs-attention model at the
/// edge. Only reads existing endpoints (<c>GET /workitems</c>,
/// <c>/audit-reports</c>, <c>/audit-progress</c>, <c>/questions</c>,
/// <c>/dependents</c>); adds no new ones. Pure and total: null-tolerant,
/// bounded, never throws on odd input.
/// </summary>
public static class NeedsAttentionEvidenceMapper
{
    private const int MaxFindings = 200;
    private const int MaxIterations = 128;

    /// <summary>
    /// Builds the attention scores for the whole fleet (package 2's
    /// <see cref="AttentionRanker"/>), keyed by item id. Terminal items are
    /// kept: unlike the fleet map, this queue ranks them.
    /// </summary>
    public static Dictionary<string, double> ScoreAll(
        IReadOnlyList<WorkItemDto>? items,
        DateTimeOffset now)
    {
        var mapped = new List<AdminWorkItem>();
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item is null || string.IsNullOrEmpty(item.Id))
                {
                    continue;
                }
                mapped.Add(new AdminWorkItem
                {
                    Id = item.Id,
                    Title = item.Title ?? string.Empty,
                    State = item.State ?? string.Empty,
                    Agent = item.Agent ?? string.Empty,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                    DependsOn = (item.DependsOn ?? [])
                        .Where(d => !string.IsNullOrEmpty(d))
                        .Distinct(StringComparer.Ordinal)
                        .Take(64)
                        .ToList(),
                    DependsOnSatisfied = item.DependsOnSatisfied,
                });
                if (mapped.Count >= FleetSnapshot.MaxItems)
                {
                    break;
                }
            }
        }
        var snapshot = new FleetSnapshot { Now = now, Items = mapped };
        var activities = ActivityAnalyzer.AnalyzeAll(snapshot);
        return AttentionRanker.RankItems(snapshot, activities)
            .ToDictionary(s => s.ItemId, s => s.Score, StringComparer.Ordinal);
    }

    /// <summary>
    /// Blocking findings from the latest audit iteration, with the auditor
    /// that raised each. Blocking means severity <c>Error</c> (ordinal
    /// ignore-case) — the same rule the orchestrator's audit-reports
    /// endpoint uses for its blocking counts.
    /// </summary>
    public static List<AttentionFinding> LatestBlockingFindings(AuditReportsDto? reports)
    {
        var result = new List<AttentionFinding>();
        var iterations = reports?.Iterations;
        if (iterations is null || iterations.Count == 0)
        {
            return result;
        }
        var scoped = iterations.Where(i => i is not null && string.Equals(i.Target, "code", StringComparison.Ordinal)).ToList();
        if (scoped.Count == 0)
        {
            scoped = iterations.Where(i => i is not null).ToList();
        }
        var latest = scoped.Max(i => i!.Iteration);
        foreach (var iteration in scoped.Where(i => i!.Iteration == latest))
        {
            foreach (var auditor in iteration!.Auditors ?? [])
            {
                if (auditor is null)
                {
                    continue;
                }
                foreach (var finding in auditor.Findings ?? [])
                {
                    if (finding is null
                        || !string.Equals(finding.Severity, "Error", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    result.Add(new AttentionFinding
                    {
                        Id = finding.Id ?? string.Empty,
                        Auditor = auditor.Name ?? string.Empty,
                        Severity = finding.Severity ?? string.Empty,
                        Title = finding.Title ?? string.Empty,
                    });
                    if (result.Count >= MaxFindings)
                    {
                        return result;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Stable blocking-id sets per complete audit iteration, oldest first,
    /// for the latest work-attempt partition only — rows from older attempts
    /// reviewed different diffs and would fake a trend. Incomplete rows are
    /// infra interruptions, never rework iterations.
    /// </summary>
    public static List<IReadOnlyList<string>> BlockingHistory(AuditProgressListDto? progress)
    {
        var rows = progress?.Progress;
        if (rows is null || rows.Count == 0)
        {
            return [];
        }
        var complete = rows
            .Where(r => r is not null && string.Equals(r.Status, "complete", StringComparison.Ordinal))
            .ToList();
        if (complete.Count == 0)
        {
            return [];
        }
        var latestKey = complete
            .OrderByDescending(r => r!.RecordedAt)
            .First()!.WorkAttemptKey ?? string.Empty;
        return complete
            .Where(r => string.Equals(r!.WorkAttemptKey ?? string.Empty, latestKey, StringComparison.Ordinal))
            .OrderBy(r => r!.Iteration)
            .Take(MaxIterations)
            .Select(r => (IReadOnlyList<string>)(r!.BlockingFindingIds ?? [])
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .Take(NeedsAttentionItem.MaxEntries)
                .ToList())
            .ToList();
    }

    /// <summary>Complete audit iterations in the latest partition (attempt evidence).</summary>
    public static int CompleteIterations(AuditProgressListDto? progress) =>
        BlockingHistory(progress).Count;

    /// <summary>
    /// Assembles one model input from the item row plus its per-item
    /// evidence. Open questions and dependants are passed through bounded.
    /// </summary>
    public static NeedsAttentionItem ToModelItem(
        WorkItemDto item,
        IReadOnlyList<AttentionFinding>? findings,
        IReadOnlyList<IReadOnlyList<string>>? history,
        int completeIterations,
        IReadOnlyList<string>? dependantIds,
        IReadOnlyList<string>? openQuestions)
    {
        return new NeedsAttentionItem
        {
            Id = item.Id,
            Title = item.Title ?? string.Empty,
            State = item.State ?? string.Empty,
            ProjectId = item.ProjectId ?? string.Empty,
            FailureKind = item.FailureKind,
            LastError = item.LastError,
            AttemptsMade = completeIterations
                + Math.Max(0, item.DelegationAttempts)
                + Math.Max(0, item.ConflictReworkAttempts)
                + Math.Max(0, item.UpstreamPushAttempts),
            BlockingFindings = (findings ?? []).Take(MaxFindings).ToList(),
            BlockingHistory = (history ?? []).Take(MaxIterations).ToList(),
            DependantIds = (dependantIds ?? [])
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct(StringComparer.Ordinal)
                .Take(256)
                .ToList(),
            OpenQuestions = (openQuestions ?? [])
                .Where(q => !string.IsNullOrEmpty(q))
                .Distinct(StringComparer.Ordinal)
                .Take(64)
                .ToList(),
        };
    }
}

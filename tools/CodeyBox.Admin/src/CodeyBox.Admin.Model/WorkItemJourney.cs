namespace CodeyBox.Admin.Model;

/// <summary>Whether the audit loop is making progress.</summary>
public enum JourneyConvergence
{
    /// <summary>No audit iteration has run yet for the current attempt.</summary>
    NotYetAuditing,
    /// <summary>Blocking count is shrinking (or new findings stopped) — leave it alone.</summary>
    Converging,
    /// <summary>New findings keep arriving or counts bounce — still moving, not stuck.</summary>
    Cycling,
    /// <summary>
    /// The trailing complete iterations carry an identical non-empty blocking
    /// set — the stuck signal. Needs intervention.
    /// </summary>
    Stuck,
    /// <summary>Last complete iteration has zero blocking findings.</summary>
    Converged,
    /// <summary>Used the whole configured budget without converging.</summary>
    AtBudget,
}

/// <summary>The per-item journey: phases actually visited, the audit/rework
/// cycle with its convergence reading, infra interruptions kept distinct,
/// and where the item sits now.</summary>
public sealed record WorkItemJourney
{
    public required string ItemId { get; init; }

    public required string Title { get; init; }

    /// <summary>Visited phases in canonical pipeline order (loops as loop counts).</summary>
    public IReadOnlyList<JourneyNode> Nodes { get; init; } = [];

    public required JourneyAuditCycle AuditCycle { get; init; }

    /// <summary>Conflict-rework loop visits (merge-phase fallback, post-merge).</summary>
    public required JourneyLoop ConflictLoop { get; init; }

    /// <summary>Upstream-push attempts (host-side, retryable post-success step).</summary>
    public required JourneyLoop UpstreamLoop { get; init; }

    /// <summary>Infra interruptions — never counted as rework iterations.</summary>
    public IReadOnlyList<JourneyInfraEvent> InfraInterruptions { get; init; } = [];

    /// <summary>Current lifecycle state name.</summary>
    public required string CurrentState { get; init; }

    /// <summary>Canonical phase the item is in now (e.g. "audit", "merge").</summary>
    public required string CurrentPhase { get; init; }

    /// <summary>Operator reading of what the item is waiting for (never empty).</summary>
    public required string WaitingFor { get; init; }

    public bool DiffAvailable { get; init; }
}

/// <summary>One visited phase node.</summary>
public sealed record JourneyNode
{
    /// <summary>Canonical key: planning, work, audit, rework, merge,
    /// conflictRework, upstreamPush, delegation, check.</summary>
    public required string Phase { get; init; }

    public required string Label { get; init; }

    /// <summary>How many times the phase ran (loop count; 1 = straight through).</summary>
    public int Visits { get; init; }

    /// <summary>True for the audit↔rework and merge↔conflict loops when revisited.</summary>
    public bool IsLoop { get; init; }

    /// <summary>Distinct agent kinds that ran this phase, in first-seen order.</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary>Summed wall-clock for the phase; null when no timing was recorded.</summary>
    public long? TotalDurationMs { get; init; }

    /// <summary>Latest run outcome ("success", "failure:…", "running", or "unknown").</summary>
    public string Outcome { get; init; } = "unknown";

    /// <summary>True when the item currently sits in this phase.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>
    /// Retained sandbox evidence files for this phase. Empty renders no
    /// sandbox control — never a dead link.
    /// </summary>
    public IReadOnlyList<string> SandboxFiles { get; init; } = [];

    public bool HasSandboxLink => SandboxFiles.Count > 0;
}

/// <summary>The audit↔rework cycle: per-iteration convergence detail plus budget.</summary>
public sealed record JourneyAuditCycle
{
    public IReadOnlyList<JourneyAuditIterationResult> Iterations { get; init; } = [];

    public JourneyConvergence Convergence { get; init; }

    /// <summary>One-line operator reading (names the stuck finding count when stuck).</summary>
    public required string Summary { get; init; }

    /// <summary>Complete verdicts in the current work attempt (infra verdicts excluded).</summary>
    public int AttemptsUsed { get; init; }

    /// <summary>Configured ceiling (item override, else project default, else last row).</summary>
    public int ConfiguredMax { get; init; }

    /// <summary>Budget left; null when the ceiling is unknown.</summary>
    public int? Remaining { get; init; }

    /// <summary>
    /// Blocking ids recurring across the trailing identical run (the stuck
    /// set). Empty unless <see cref="Convergence"/> is <see cref="JourneyConvergence.Stuck"/>.
    /// </summary>
    public IReadOnlyList<string> StuckFindingIds { get; init; } = [];

    /// <summary>Length of the trailing run of identical non-empty blocking sets.</summary>
    public int StuckRunLength { get; init; }
}

/// <summary>Convergence detail for one complete audit iteration.</summary>
public sealed record JourneyAuditIterationResult
{
    public int Iteration { get; init; }

    public int BlockingFindings { get; init; }

    public int NonBlockingFindings { get; init; }

    /// <summary>Ids also present in the previous complete iteration.</summary>
    public IReadOnlyList<string> RecurringIds { get; init; } = [];

    /// <summary>Ids absent from the previous complete iteration (all ids when first).</summary>
    public IReadOnlyList<string> NewIds { get; init; } = [];

    /// <summary>Ids from the previous iteration absent here.</summary>
    public IReadOnlyList<string> ResolvedIds { get; init; } = [];

    public DateTimeOffset RecordedAt { get; init; }

    public string? WorkBranchTip { get; init; }
}

/// <summary>A small retry loop that is not the audit cycle (conflict, upstream).</summary>
public sealed record JourneyLoop
{
    public required string Label { get; init; }

    public int Visits { get; init; }

    public IReadOnlyList<string> Agents { get; init; } = [];

    public long? TotalDurationMs { get; init; }

    public string Outcome { get; init; } = "unknown";

    public bool IsCurrent { get; init; }
}

/// <summary>
/// Pure projection: a single work item's recorded history → its journey
/// graph. A function of <paramref name="snapshot"/> and
/// <paramref name="options"/> only: no I/O, no clock.
/// </summary>
public static class WorkItemJourneyBuilder
{
    /// <summary>
    /// Builds the journey. Infra-shaped rows (incomplete audit verdicts,
    /// <c>failure:infrastructure</c> runs) are listed under
    /// <see cref="WorkItemJourney.InfraInterruptions"/> and never counted as
    /// rework iterations, so an infra retry cannot masquerade as cycling.
    /// Finding-id comparison is exact ordinal match — never substring.
    /// </summary>
    public static WorkItemJourney Build(JourneySnapshot snapshot, AdminModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Item);
        options ??= new AdminModelOptions();

        var item = snapshot.Item;
        var runs = TakeBounded(snapshot.PhaseRuns);
        var timings = SumTimings(TakeBounded(snapshot.Timings));
        var streams = TakeBounded(snapshot.StreamFiles);
        var infra = TakeBounded(snapshot.InfraEvents).OrderBy(e => e.OccurredAt).ToList();

        var completeIterations = SelectCurrentAttempt(TakeBounded(snapshot.AuditIterations));
        var incompleteRows = TakeBounded(snapshot.AuditIterations)
            .Where(r => !IsCompleteStatus(r.Status) && r.Iteration > 0)
            .OrderBy(r => r.RecordedAt)
            .ToList();

        var interruptions = infra
            .Concat(incompleteRows.Select(r => new JourneyInfraEvent
            {
                Phase = "audit",
                Kind = "incomplete-audit",
                Detail = $"Audit iteration {r.Iteration} ended without a complete verdict ({r.Status}).",
                OccurredAt = r.RecordedAt,
            }))
            .Concat(runs
                .Where(r => string.Equals(r.Outcome, "failure:infrastructure", StringComparison.Ordinal))
                .Select(r => new JourneyInfraEvent
                {
                    Phase = CanonicalPhase(r.Phase) ?? r.Phase,
                    Kind = "infrastructure",
                    Detail = $"Phase run failed with infrastructure outcome ({r.Phase}).",
                    SandboxName = r.SandboxName,
                    OccurredAt = r.StartedAt,
                }))
            .OrderBy(e => e.OccurredAt)
            .ToList();

        var currentPhase = CanonicalPhaseForState(item.State);
        var cycle = BuildAuditCycle(item, completeIterations, snapshot.ProjectDefaultMaxIterations, options);
        var nodes = BuildNodes(item, runs, timings, streams, completeIterations.Count > 0, currentPhase);
        var conflictLoop = BuildLoop(
            "Conflict rework", runs, timings, "conflictRework",
            Math.Max(item.ConflictReworkAttempts, 0), currentPhase);
        var upstreamLoop = BuildLoop(
            "Upstream push", runs, timings, "upstreamPush",
            Math.Max(item.UpstreamPushAttempts, 0), currentPhase);

        return new WorkItemJourney
        {
            ItemId = item.Id,
            Title = item.Title ?? string.Empty,
            Nodes = nodes,
            AuditCycle = cycle,
            ConflictLoop = conflictLoop,
            UpstreamLoop = upstreamLoop,
            InfraInterruptions = interruptions,
            CurrentState = item.State ?? string.Empty,
            CurrentPhase = currentPhase,
            WaitingFor = DescribeWaitingFor(item),
            DiffAvailable = snapshot.DiffAvailable,
        };
    }

    // ── Audit cycle ──────────────────────────────────────────────────────

    private static JourneyAuditCycle BuildAuditCycle(
        JourneyItem item,
        IReadOnlyList<JourneyAuditIteration> complete,
        int? projectDefaultMax,
        AdminModelOptions options)
    {
        var ordered = complete.OrderBy(r => r.Iteration).ToList();
        var results = new List<JourneyAuditIterationResult>(ordered.Count);
        HashSet<string>? previous = null;
        foreach (var row in ordered)
        {
            var current = new HashSet<string>(
                (row.BlockingFindingIds ?? []).Where(s => !string.IsNullOrEmpty(s)),
                StringComparer.Ordinal);
            var result = new JourneyAuditIterationResult
            {
                Iteration = row.Iteration,
                BlockingFindings = row.BlockingFindings,
                NonBlockingFindings = row.NonBlockingFindings,
                RecurringIds = previous is null
                    ? []
                    : current.Where(previous.Contains).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                NewIds = previous is null
                    ? current.OrderBy(s => s, StringComparer.Ordinal).ToList()
                    : current.Where(id => !previous.Contains(id)).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                ResolvedIds = previous is null
                    ? []
                    : previous.Where(id => !current.Contains(id)).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                RecordedAt = row.RecordedAt,
                WorkBranchTip = row.WorkBranchTip,
            };
            results.Add(result);
            previous = current;
        }

        var configuredMax = item.AuditMaxIterations
            ?? projectDefaultMax
            ?? ordered.LastOrDefault()?.MaxIterations
            ?? 0;
        var used = ordered.Count;
        int? remaining = configuredMax > 0 ? Math.Max(0, configuredMax - used) : null;

        var (convergence, summary, stuckIds, stuckRun) =
            Classify(results, used, configuredMax, options.JourneyStuckIdenticalIterations);

        return new JourneyAuditCycle
        {
            Iterations = results,
            Convergence = convergence,
            Summary = summary,
            AttemptsUsed = used,
            ConfiguredMax = configuredMax,
            Remaining = remaining,
            StuckFindingIds = stuckIds,
            StuckRunLength = stuckRun,
        };
    }

    private static (JourneyConvergence Verdict, string Summary, IReadOnlyList<string> StuckIds, int StuckRun)
        Classify(
            IReadOnlyList<JourneyAuditIterationResult> results,
            int used,
            int configuredMax,
            int stuckThreshold)
    {
        if (results.Count == 0)
            return (JourneyConvergence.NotYetAuditing, "No audit iteration has completed yet.", [], 0);

        var last = results[^1];
        if (last.BlockingFindings == 0)
            return (JourneyConvergence.Converged,
                $"Converged on iteration {last.Iteration}: no blocking findings.", [], 0);

        var stuckRun = TrailingIdenticalRun(results);
        if (stuckRun >= Math.Max(2, stuckThreshold))
        {
            var ids = last.RecurringIds.Count > 0 ? last.RecurringIds : last.NewIds;
            return (JourneyConvergence.Stuck,
                $"Stuck: the same {ids.Count} blocking finding(s) recurred across the last {stuckRun} iterations.",
                ids, stuckRun);
        }

        if (configuredMax > 0 && used >= configuredMax)
            return (JourneyConvergence.AtBudget,
                $"At budget: {used} of {configuredMax} audit iterations used with {last.BlockingFindings} blocking finding(s) still open.",
                [], 0);

        var previous = results.Count >= 2 ? results[^2] : null;
        if (previous is not null
            && last.BlockingFindings < previous.BlockingFindings
            && last.NewIds.Count == 0)
            return (JourneyConvergence.Converging,
                $"Converging: blocking findings fell {previous.BlockingFindings} → {last.BlockingFindings} with no new findings.",
                [], 0);

        if (results.Count == 1)
            return (JourneyConvergence.Cycling,
                $"Cycling: iteration {last.Iteration} has {last.BlockingFindings} blocking finding(s) open.",
                [], 0);

        return (JourneyConvergence.Cycling,
            $"Cycling: {last.BlockingFindings} blocking finding(s) open " +
            $"({last.NewIds.Count} new, {last.ResolvedIds.Count} resolved since iteration {results[^2].Iteration}).",
            [], 0);
    }

    /// <summary>
    /// Length of the trailing run of complete iterations whose blocking sets
    /// are identical (exact set equality) and non-empty.
    /// </summary>
    private static int TrailingIdenticalRun(IReadOnlyList<JourneyAuditIterationResult> results)
    {
        if (results.Count == 0) return 0;
        var run = 1;
        for (var i = results.Count - 1; i >= 1; i--)
        {
            if (!IsIdenticalToPrevious(results[i])) break;
            run++;
        }
        return run;
    }

    /// <summary>
    /// The iteration's blocking set is identical to its predecessor's:
    /// recurring/new/resolved are computed by exact-match diffing, so no-new
    /// and none-resolved with a non-empty recurring set means set equality.
    /// </summary>
    private static bool IsIdenticalToPrevious(JourneyAuditIterationResult current) =>
        current.RecurringIds.Count > 0 && current.NewIds.Count == 0 && current.ResolvedIds.Count == 0;

    /// <summary>
    /// Keeps only rows from the most recent work attempt (by latest
    /// <c>RecordedAt</c>), complete verdicts with a positive iteration,
    /// ordered oldest first. A resume purges the prior attempt's rows, so
    /// mixing attempts would double-count the budget.
    /// </summary>
    private static IReadOnlyList<JourneyAuditIteration> SelectCurrentAttempt(
        IReadOnlyList<JourneyAuditIteration> rows)
    {
        var complete = rows
            .Where(r => r is not null && r.Iteration > 0 && IsCompleteStatus(r.Status))
            .ToList();
        if (complete.Count == 0) return [];
        var latestKey = complete
            .OrderByDescending(r => r.RecordedAt)
            .First().WorkAttemptKey ?? string.Empty;
        return complete
            .Where(r => string.Equals(r.WorkAttemptKey ?? string.Empty, latestKey, StringComparison.Ordinal))
            .OrderBy(r => r.RecordedAt)
            .ToList();
    }

    private static bool IsCompleteStatus(string? status) =>
        string.Equals(status ?? "complete", "complete", StringComparison.OrdinalIgnoreCase);

    // ── Nodes ────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<(string Phase, string Label)> CanonicalOrder =
    [
        ("planning", "Planning"),
        ("work", "Work"),
        ("audit", "Audit"),
        ("rework", "Rework"),
        ("delegation", "Delegation"),
        ("merge", "Merge"),
        ("conflictRework", "Conflict rework"),
        ("upstreamPush", "Upstream push"),
        ("check", "Check"),
    ];

    private static IReadOnlyList<JourneyNode> BuildNodes(
        JourneyItem item,
        IReadOnlyList<JourneyPhaseRun> runs,
        IReadOnlyDictionary<string, long> timings,
        IReadOnlyList<JourneyStreamFile> streams,
        bool hasAuditIterations,
        string currentPhase)
    {
        var nodes = new List<JourneyNode>(CanonicalOrder.Count);
        foreach (var (phase, label) in CanonicalOrder)
        {
            var phaseRuns = runs.Where(r => CanonicalPhase(r.Phase) == phase).ToList();
            var visits = phaseRuns.Count;
            if (phase == "audit" && visits == 0 && hasAuditIterations)
                visits = Math.Max(visits, 1);
            if (phase == "conflictRework")
                visits = Math.Max(visits, Math.Max(0, item.ConflictReworkAttempts));
            if (phase == "upstreamPush")
                visits = Math.Max(visits, Math.Max(0, item.UpstreamPushAttempts));
            if (phase == "delegation")
                visits = Math.Max(visits, Math.Max(0, item.DelegationAttempts));
            if (visits == 0 && !string.Equals(currentPhase, phase, StringComparison.Ordinal))
                continue;

            var agents = phaseRuns
                .Select(r => r.AgentKind)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var files = streams
                .Where(s => CanonicalPhase(s.Phase) == phase)
                .Select(s => s.FileName)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            timings.TryGetValue(phase, out var duration);

            nodes.Add(new JourneyNode
            {
                Phase = phase,
                Label = label,
                Visits = Math.Max(visits, 1),
                IsLoop = visits > 1 && phase is "audit" or "rework" or "conflictRework",
                Agents = agents,
                TotalDurationMs = timings.ContainsKey(phase) ? duration : null,
                Outcome = LatestOutcome(phaseRuns, phase, currentPhase),
                IsCurrent = string.Equals(currentPhase, phase, StringComparison.Ordinal),
                SandboxFiles = files,
            });
        }
        return nodes;
    }

    private static JourneyLoop BuildLoop(
        string label,
        IReadOnlyList<JourneyPhaseRun> runs,
        IReadOnlyDictionary<string, long> timings,
        string phase,
        int counter,
        string currentPhase)
    {
        var phaseRuns = runs.Where(r => CanonicalPhase(r.Phase) == phase).ToList();
        var visits = Math.Max(phaseRuns.Count, counter);
        var agents = phaseRuns
            .Select(r => r.AgentKind)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        timings.TryGetValue(phase, out var duration);
        return new JourneyLoop
        {
            Label = label,
            Visits = visits,
            Agents = agents,
            TotalDurationMs = timings.ContainsKey(phase) ? duration : null,
            Outcome = LatestOutcome(phaseRuns, phase, currentPhase),
            IsCurrent = string.Equals(currentPhase, phase, StringComparison.Ordinal),
        };
    }

    private static string LatestOutcome(
        IReadOnlyList<JourneyPhaseRun> phaseRuns, string phase, string currentPhase)
    {
        var latest = phaseRuns.OrderByDescending(r => r.StartedAt).FirstOrDefault();
        if (latest?.Outcome is { Length: > 0 } outcome) return outcome;
        if (latest is not null) return "running";
        return string.Equals(currentPhase, phase, StringComparison.Ordinal) ? "running" : "unknown";
    }

    // ── Canonicalization ─────────────────────────────────────────────────

    /// <summary>
    /// Maps orchestrator phase keys onto journey nodes by exact match.
    /// Auditor runs (<c>audit:&lt;name&gt;</c>) fold into the audit node.
    /// Returns null for unrecognised keys so an unknown phase can never
    /// invent or inflate a node — callers drop nulls.
    /// </summary>
    internal static string? CanonicalPhase(string? phase)
    {
        if (string.IsNullOrEmpty(phase)) return null;
        if (phase.StartsWith("audit:", StringComparison.Ordinal)) return "audit";
        return phase switch
        {
            "work" or "mechanical-edit" => "work",
            "audit" => "audit",
            "rework" => "rework",
            "merge" or "rebase-resolver" => "merge",
            "conflict_rework" => "conflictRework",
            "upstream_push" or "upstream" => "upstreamPush",
            "delegation" => "delegation",
            "planning" or "plan" => "planning",
            "check" or "post-act-recheck" => "check",
            _ => null,
        };
    }

    private static string CanonicalPhaseForState(string? state) => state switch
    {
        "Planning" or "PlanReview" or "PlanApproved" => "planning",
        "Queued" or "Working" or "WorkComplete" => "work",
        "Auditing" or "Reworking" or "AuditPassed" or "AuditFailed" => "audit",
        "Merging" or "MergeConflictResolutionFailed" => "merge",
        "ReworkingForConflict" => "conflictRework",
        "Merged" or "UpstreamPushing" or "Done" => "upstreamPush",
        "Delegating" => "delegation",
        "WaitingForQuotaReset" or "WaitingForAgentResume" or "WaitingForTransientRetry"
            or "NeedsOperatorInput" or "Failed" or "Cancelled"
            or "NoActionRequired" or "AbandonedAfterRecoveryAttempts" => "work",
        _ => "work",
    };

    private static string DescribeWaitingFor(JourneyItem item) => item.State switch
    {
        "Queued" => "Queued for pickup.",
        "Working" or "Auditing" or "Merging" or "Planning" or "PlanReview" or "Delegating"
            => $"Running ({item.State}).",
        "WorkComplete" or "AuditPassed" or "PlanApproved" or "Merged"
            => $"Between phases after {item.State}.",
        "Reworking" => "Reworking audit findings.",
        "ReworkingForConflict" => "Resolving merge conflicts.",
        "UpstreamPushing" => "Pushing upstream.",
        "WaitingForQuotaReset" => WaitingForQuota(item),
        "WaitingForTransientRetry" => item.NextTransientRetryAt.HasValue
            ? $"Backing off before a transient retry at {item.NextTransientRetryAt.Value:u}."
            : "Backing off before a transient retry.",
        "WaitingForAgentResume" => string.IsNullOrWhiteSpace(item.AgentPauseTarget)
            ? "Waiting for an operator to resume the agent."
            : $"Waiting for an operator to resume agent {item.AgentPauseTarget}.",
        "NeedsOperatorInput" => "Waiting for operator input.",
        "Done" => "Completed.",
        "NoActionRequired" => "Resolved with no action required.",
        "Cancelled" => "Cancelled.",
        "Failed" or "AuditFailed" or "MergeConflictResolutionFailed" or "AbandonedAfterRecoveryAttempts"
            => $"Terminal ({item.State}).",
        _ => $"In state {item.State}.",
    };

    private static string WaitingForQuota(JourneyItem item) =>
        item.NextQuotaRetryAt.HasValue
            ? $"Waiting for agent quota reset; retry scheduled at {item.NextQuotaRetryAt.Value:u}."
            : "Waiting for agent quota reset.";

    // ── Bounds ───────────────────────────────────────────────────────────

    private static IReadOnlyList<T> TakeBounded<T>(IReadOnlyList<T>? list)
    {
        if (list is null || list.Count == 0) return [];
        return list.Count <= JourneySnapshot.MaxEntries ? list : list.Take(JourneySnapshot.MaxEntries).ToList();
    }

    private static IReadOnlyDictionary<string, long> SumTimings(IReadOnlyList<JourneyPhaseTiming> timings)
    {
        var sums = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var timing in timings)
        {
            if (timing is null || string.IsNullOrEmpty(timing.Phase) || timing.DurationMs < 0)
                continue;
            var key = CanonicalPhase(timing.Phase);
            if (key is null) continue;
            sums[key] = sums.TryGetValue(key, out var current)
                ? checked(current + timing.DurationMs)
                : timing.DurationMs;
        }
        return sums;
    }
}

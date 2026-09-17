namespace CodeyBox.Admin.Model;

/// <summary>
/// Whether the audit findings are converging across attempts.
/// Compared by exact ordinal match on stable finding ids — never substring.
/// Mirrors the journey's <c>Stuck</c> vocabulary in queue-sized form.
/// </summary>
public enum FindingTrend
{
    /// <summary>No complete audit iteration recorded for the current attempt.</summary>
    NoEvidence,
    /// <summary>Every recorded iteration has zero blocking findings.</summary>
    NoBlockingFindings,
    /// <summary>
    /// The trailing iterations carry an identical non-empty blocking set —
    /// the signal that a retry will not help.
    /// </summary>
    Repeating,
    /// <summary>Blocking counts are strictly shrinking — a retry may converge.</summary>
    Shrinking,
    /// <summary>Findings are present but neither repeating nor shrinking.</summary>
    Cycling,
}

/// <summary>One blocking finding with the auditor that raised it.</summary>
public sealed record AttentionFinding
{
    public required string Id { get; init; }

    public required string Auditor { get; init; }

    public string Severity { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
}

/// <summary>
/// Minimal per-item input to the needs-attention queue. Mirrors the fields of
/// <c>GET /workitems</c> plus per-item evidence from existing surfaces
/// (<c>/audit-reports</c> findings, <c>/audit-progress</c> blocking-id sets,
/// <c>/questions</c> open count, <c>/dependents</c> ids). All collections are
/// treated as untrusted input: null-tolerant and length-bounded before buffering.
/// </summary>
public sealed record NeedsAttentionItem
{
    /// <summary>Upper bound on list inputs accepted; entries beyond it are ignored.</summary>
    public const int MaxEntries = 4096;

    public required string Id { get; init; }

    public string Title { get; init; } = string.Empty;

    /// <summary>Orchestrator lifecycle state name (e.g. "Failed", "NeedsOperatorInput").</summary>
    public string State { get; init; } = string.Empty;

    public string ProjectId { get; init; } = string.Empty;

    /// <summary>Informational failure category ("infrastructure", "build", ...).</summary>
    public string? FailureKind { get; init; }

    public string? LastError { get; init; }

    /// <summary>
    /// How many execution attempts have been made (audit iterations run plus
    /// delegation/conflict/upstream turns, summed caller-side).
    /// </summary>
    public int AttemptsMade { get; init; }

    /// <summary>Blocking findings from the latest complete audit iteration, with auditors.</summary>
    public IReadOnlyList<AttentionFinding> BlockingFindings { get; init; } = [];

    /// <summary>
    /// Stable blocking-finding id sets per complete audit iteration, oldest
    /// first. Compared by exact match to compute the trend.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> BlockingHistory { get; init; } = [];

    /// <summary>Ids of items whose <c>DependsOn</c> includes this item.</summary>
    public IReadOnlyList<string> DependantIds { get; init; } = [];

    /// <summary>Open operator-question ids parked on this item (empty when none).</summary>
    public IReadOnlyList<string> OpenQuestions { get; init; } = [];
}

/// <summary>
/// One queue entry: the item, its attention score from
/// <see cref="AttentionRanker"/>, and the inline evidence an operator needs
/// to decide retry / delegate / cancel without leaving the screen.
/// </summary>
public sealed record NeedsAttentionEntry
{
    public required NeedsAttentionItem Item { get; init; }

    /// <summary>Attention score (0–100); the queue is ordered by this, highest first.</summary>
    public required double AttentionScore { get; init; }

    /// <summary>Plain-language account of what happened. Never empty.</summary>
    public required string Headline { get; init; }

    /// <summary>
    /// True when the failure originated outside the agent's reasoning loop
    /// (infrastructure, quota-adjacent waits, host loss) and a retry may
    /// succeed; false when the change itself was rejected (audit
    /// non-convergence, build breakage, exhausted conflict ladder) and an
    /// unchanged retry cannot help.
    /// </summary>
    public required bool IsInfrastructureFailure { get; init; }

    public required FindingTrend Trend { get; init; }

    /// <summary>Operator reading of the trend. Never empty.</summary>
    public required string TrendSummary { get; init; }

    /// <summary>
    /// Non-null only when cancelling would strand dependants. Names them —
    /// the count and identity must be visible before the action is taken.
    /// </summary>
    public string? CancelWarning { get; init; }

    /// <summary>
    /// Explicit retry phase hint for the conflict path. Null means the
    /// retrier's auto-pick applies.
    /// </summary>
    public string? RecommendedRetryFrom { get; init; }
}

/// <summary>
/// The "what needs me?" queue: every item awaiting a human, ordered by the
/// attention score from <see cref="AttentionRanker"/>, with the evidence
/// inline. Pure: the same inputs always produce the same screen. No I/O.
/// </summary>
public static class NeedsAttentionQueue
{
    /// <summary>
    /// Builds the queue. Only items in <see cref="ItemStates.Failed"/> or
    /// <see cref="ItemStates.Parked"/> are included — that covers
    /// <c>Failed</c>, <c>AuditFailed</c>,
    /// <c>MergeConflictResolutionFailed</c>,
    /// <c>AbandonedAfterRecoveryAttempts</c>, <c>NeedsOperatorInput</c>, and
    /// the parked-waiting states. Items without a score default to 0 and sort
    /// last; ties break by id so the order is stable.
    /// </summary>
    public static IReadOnlyList<NeedsAttentionEntry> Build(
        IReadOnlyList<NeedsAttentionItem> items,
        IReadOnlyDictionary<string, double> scoresByItemId,
        AdminModelOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(scoresByItemId);
        options ??= new AdminModelOptions();

        var entries = new List<NeedsAttentionEntry>();
        var seen = 0;
        foreach (var item in items)
        {
            if (item is null || string.IsNullOrEmpty(item.Id))
            {
                continue;
            }
            if (seen >= NeedsAttentionItem.MaxEntries)
            {
                break;
            }
            seen++;
            if (!IsNeedingHuman(item.State))
            {
                continue;
            }
            var score = scoresByItemId.TryGetValue(item.Id, out var s) ? s : 0.0;
            var (headline, isInfrastructure) = FailureDescriber.Describe(item.State, item.FailureKind);
            var trend = FindingTrendAnalyzer.Compute(item.BlockingHistory, options);
            entries.Add(new NeedsAttentionEntry
            {
                Item = item,
                AttentionScore = Math.Clamp(score, 0, 100),
                Headline = headline,
                IsInfrastructureFailure = isInfrastructure,
                Trend = trend,
                TrendSummary = FindingTrendAnalyzer.Summarize(trend, item.BlockingHistory),
                CancelWarning = CancelWarningBuilder.Describe(item.DependantIds),
                RecommendedRetryFrom = string.Equals(item.State, "MergeConflictResolutionFailed", StringComparison.Ordinal)
                    ? "merge"
                    : null,
            });
        }
        entries.Sort(static (left, right) =>
        {
            var order = right.AttentionScore.CompareTo(left.AttentionScore);
            return order != 0
                ? order
                : string.Compare(left.Item.Id, right.Item.Id, StringComparison.Ordinal);
        });
        return entries;
    }

    private static bool IsNeedingHuman(string? state) =>
        state is not null && (ItemStates.Failed.Contains(state) || ItemStates.Parked.Contains(state));
}

/// <summary>
/// Plain-language account of what happened, distinguishing an infrastructure
/// failure (retry may succeed) from a rejected change (unchanged retry cannot
/// help). Keyed off the orchestrator's <c>failureKind</c> taxonomy plus the
/// lifecycle state; unknown shapes fail closed toward "needs triage".
/// </summary>
public static class FailureDescriber
{
    /// <summary>
    /// Returns the headline and whether the failure is infrastructure-side.
    /// The headline is never empty.
    /// </summary>
    public static (string Headline, bool IsInfrastructure) Describe(string? state, string? failureKind)
    {
        if (string.Equals(state, "NeedsOperatorInput", StringComparison.Ordinal))
        {
            return ("Parked: the agent asked a question and is waiting for an operator answer.", false);
        }
        if (string.Equals(state, "WaitingForQuotaReset", StringComparison.Ordinal))
        {
            return ("Parked: waiting for agent quota to reset — infrastructure capacity, not a verdict on the change.", true);
        }
        if (string.Equals(state, "WaitingForAgentResume", StringComparison.Ordinal))
        {
            return ("Parked: its agents are paused by an operator — resume one to release it.", true);
        }
        if (string.Equals(state, "WaitingForTransientRetry", StringComparison.Ordinal))
        {
            return ("Parked: a transient transport failure — backing off before an automatic retry.", true);
        }
        if (string.Equals(state, "AbandonedAfterRecoveryAttempts", StringComparison.Ordinal))
        {
            return ("Infrastructure failure: the hosts kept dying under this item and recovery gave up. The change itself was never judged.", true);
        }
        if (string.Equals(state, "MergeConflictResolutionFailed", StringComparison.Ordinal))
        {
            return ("Rejected change: the merge-conflict ladder (auto-rebase, LLM rerun, focused conflict-rework) is exhausted. A human must resolve the conflict or redirect the change.", false);
        }
        if (string.Equals(state, "AuditFailed", StringComparison.Ordinal))
        {
            return ("Rejected change: the auditors would not converge on this diff. An unchanged retry re-runs the same audit on the same diff.", false);
        }
        if (string.Equals(failureKind, "infrastructure", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureKind, "transient", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureKind, "transient-exhausted", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureKind, "agent_unavailable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureKind, "agent_routing_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return ($"Infrastructure failure (kind '{failureKind}'): the run died outside the agent's reasoning loop — a retry may succeed.", true);
        }
        if (string.Equals(failureKind, "timeout", StringComparison.OrdinalIgnoreCase))
        {
            return ("Infrastructure failure: the run hit its wall-clock budget before finishing — a retry with a larger budget may succeed.", true);
        }
        if (string.Equals(failureKind, "build", StringComparison.OrdinalIgnoreCase))
        {
            return ("Rejected change: the agent's code does not compile or its checks fail. Rework the change, not the retry button.", false);
        }
        if (string.Equals(failureKind, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return ("Rejected change: the agent itself gave up (stuck or internal failure). The input needs rework before any retry.", false);
        }
        if (string.Equals(failureKind, "configuration", StringComparison.OrdinalIgnoreCase))
        {
            return ("Rejected change: the pipeline rejected this item's configuration. Fix the config, then retry.", false);
        }
        if (string.Equals(failureKind, "quota", StringComparison.OrdinalIgnoreCase))
        {
            return ("Parked: per-window quota exhaustion — capacity will return on its own timer.", true);
        }
        if (string.Equals(failureKind, "cancelled", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureKind, "auth_required", StringComparison.OrdinalIgnoreCase))
        {
            return ($"Parked by operator action (kind '{failureKind}'): only a human decision moves this item.", false);
        }
        if (string.Equals(state, "Failed", StringComparison.Ordinal))
        {
            return ("Failed: the run did not finish and the cause is unclassified — triage before retrying.", false);
        }
        return ($"Parked in '{state ?? "unknown"}': cause unclassified — triage before retrying.", false);
    }
}

/// <summary>
/// Computes whether findings are shrinking between attempts or repeating —
/// the signal for whether a retry can help. Compares stable finding-id sets
/// by exact ordinal match, mirroring the journey's stuck verdict.
/// </summary>
public static class FindingTrendAnalyzer
{
    /// <summary>
    /// Computes the trend over per-iteration blocking-id sets, oldest first.
    /// Only the latest work-attempt partition should be passed in; rows from
    /// older attempts would compare findings across different diffs.
    /// </summary>
    public static FindingTrend Compute(
        IReadOnlyList<IReadOnlyList<string>>? blockingHistory,
        AdminModelOptions? options = null)
    {
        options ??= new AdminModelOptions();
        var sets = Normalise(blockingHistory);
        if (sets.Count == 0)
        {
            return FindingTrend.NoEvidence;
        }
        if (sets.All(s => s.Count == 0))
        {
            return FindingTrend.NoBlockingFindings;
        }
        var runLength = Math.Max(2, options.JourneyStuckIdenticalIterations);
        if (TrailingIdenticalNonEmptyRun(sets) >= runLength)
        {
            return FindingTrend.Repeating;
        }
        if (IsStrictlyShrinking(sets))
        {
            return FindingTrend.Shrinking;
        }
        return FindingTrend.Cycling;
    }

    /// <summary>Operator reading of the trend. Never empty.</summary>
    public static string Summarize(
        FindingTrend trend,
        IReadOnlyList<IReadOnlyList<string>>? blockingHistory)
    {
        var sets = Normalise(blockingHistory);
        var latest = sets.Count == 0 ? 0 : sets[^1].Count;
        return trend switch
        {
            FindingTrend.NoEvidence =>
                "No complete audit iteration recorded yet — no finding history to compare.",
            FindingTrend.NoBlockingFindings =>
                "Parked with no blocking findings: nothing in the audit record explains the park — read the park text, then decide.",
            FindingTrend.Repeating =>
                $"Same {latest} blocking finding(s) {TrailingIdenticalNonEmptyRun(sets)} iterations in a row — a retry will not help without rework.",
            FindingTrend.Shrinking =>
                $"Blocking findings are shrinking ({string.Join(" → ", sets.Select(s => s.Count.ToString()))}) — a retry or rework may converge.",
            _ =>
                $"Blocking findings keep changing ({string.Join(" → ", sets.Select(s => s.Count.ToString()))}) — still moving, not stuck.",
        };
    }

    private static List<HashSet<string>> Normalise(IReadOnlyList<IReadOnlyList<string>>? history)
    {
        var sets = new List<HashSet<string>>();
        if (history is null)
        {
            return sets;
        }
        foreach (var row in history)
        {
            if (sets.Count >= NeedsAttentionItem.MaxEntries)
            {
                break;
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (row is not null)
            {
                foreach (var id in row)
                {
                    if (!string.IsNullOrEmpty(id) && ids.Count < NeedsAttentionItem.MaxEntries)
                    {
                        ids.Add(id);
                    }
                }
            }
            sets.Add(ids);
        }
        return sets;
    }

    private static int TrailingIdenticalNonEmptyRun(List<HashSet<string>> sets)
    {
        if (sets.Count == 0 || sets[^1].Count == 0)
        {
            return 0;
        }
        var run = 1;
        for (var i = sets.Count - 1; i > 0; i--)
        {
            if (sets[i].SetEquals(sets[i - 1]))
            {
                run++;
            }
            else
            {
                break;
            }
        }
        return run;
    }

    private static bool IsStrictlyShrinking(List<HashSet<string>> sets)
    {
        if (sets.Count < 2 || sets[^1].Count == 0)
        {
            return false;
        }
        for (var i = 1; i < sets.Count; i++)
        {
            if (sets[i].Count >= sets[i - 1].Count)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>
/// Builds the pre-action cancel warning. Cancelling an item that others
/// depend on strands them — the count and identity must be visible in the
/// confirmation, not discovered afterwards.
/// </summary>
public static class CancelWarningBuilder
{
    private const int MaxNamedDependants = 10;

    /// <summary>Null when there is nothing to warn about (no dependants).</summary>
    public static string? Describe(IReadOnlyList<string>? dependantIds)
    {
        var ids = (dependantIds ?? [])
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return null;
        }
        var named = ids.Take(MaxNamedDependants).ToList();
        var names = string.Join(", ", named);
        var suffix = ids.Count > named.Count
            ? $" (and {ids.Count - named.Count} more)"
            : string.Empty;
        var noun = ids.Count == 1 ? "1 dependant" : $"{ids.Count} dependants";
        return $"Cancelling strands {noun}: {names}{suffix}. Cancel or re-route them first.";
    }
}

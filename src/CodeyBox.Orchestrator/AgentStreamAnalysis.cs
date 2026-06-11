using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public sealed record AgentStreamSummaryRow(
    WorkItemId WorkItemId,
    string FileName,
    string Phase,
    int? Iteration,
    AgentKind AgentKind,
    AgentStreamSummary Summary,
    DateTimeOffset SummarisedAt);

public sealed record AgentStreamAggregate(
    string? WorkItemId,
    long TotalAgentDurationMs,
    int TotalToolCalls,
    IReadOnlyList<AgentStreamToolAggregate> ByTool,
    long ThinkingMs,
    long ExecutingMs,
    int StallCount,
    long LongestStallMs,
    decimal EstimatedUsdTotal);

public sealed record AgentStreamToolAggregate(
    string Tool,
    int Count,
    long TotalDurationMs,
    long MedianMs);

public static class AgentStreamParserSelection
{
    private const int MaxSniffLines = 20;
    private const int MaxSniffLineBytes = 64 * 1024 * 1024;

    /// <summary>
    /// Reads up to <see cref="MaxSniffLines"/> NDJSON lines and dispatches each
    /// to the registered <see cref="IAgentStreamParser"/>s, returning the
    /// <see cref="AgentKind"/> of the first parser that claims a line. The
    /// orchestrator itself never inspects provider-specific JSON properties —
    /// each parser owns its own recognition logic in its provider library.
    /// </summary>
    public static async Task<AgentKind?> SniffKindAsync(
        Stream jsonlFile,
        IEnumerable<IAgentStreamParser> parsers,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        var orderedParsers = parsers as IReadOnlyList<IAgentStreamParser>
            ?? parsers.ToList();
        if (orderedParsers.Count == 0)
            return null;

        var read = 0;
        try
        {
            await foreach (var jsonLine in AgentStreamJsonLineReader.ReadLinesAsync(jsonlFile, MaxSniffLineBytes, ct).ConfigureAwait(false))
            {
                if (read++ >= MaxSniffLines)
                    break;
                var line = jsonLine.Text;
                if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                    foreach (var parser in orderedParsers)
                    {
                        if (parser.TryClaim(doc.RootElement))
                            return parser.Kind;
                    }
                }
                catch (JsonException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        catch (InvalidDataException)
        {
        }

        return null;
    }

    /// <summary>
    /// Resolves the canonical <see cref="AgentKind"/> for a stream file using
    /// (in priority order):
    ///   1. a conclusive sniffed parser kind,
    ///   2. a capture-window-correlated cost row when the sniffed shape is
    ///      shared by wrapper agents (cursor/antigravity),
    ///   3. <see cref="WorkItem.Agent"/> for shared-shape work/rework/merge
    ///      captures when no cost metadata exists,
    ///   4. phase-matched cost metadata when sniffing failed,
    ///   5. <see cref="WorkItem.Agent"/> as the last-resort fallback.
    /// Cost rows are phase/iteration-level metadata; they are not tied to a
    /// specific random capture filename. A conclusive stream sniff therefore
    /// stays authoritative. Cost metadata is only allowed to override sniffing
    /// for known shared shapes, and only when the capture creation timestamp
    /// falls inside one distinct cost window. Only kinds present in
    /// <paramref name="knownKinds"/>
    /// are canonicalised — anything else falls through to "unknown".
    /// Callers should pass the kinds of their registered
    /// <see cref="IAgentStreamParser"/>s so adding a new provider library
    /// is purely additive.
    /// </summary>
    public static AgentKind ResolveKind(
        WorkItem item,
        AgentStreamFile file,
        AgentKind? sniffedKind,
        IReadOnlyList<WorkItemCost> costs,
        IEnumerable<AgentKind> knownKinds)
    {
        ArgumentNullException.ThrowIfNull(knownKinds);
        var known = knownKinds as IReadOnlyCollection<AgentKind> ?? knownKinds.ToList();

        var matchingCosts = MatchingCosts(item, file, costs).ToList();
        var workItemKind = item.Agent.HasValue ? Canonicalize(item.Agent.Value, known) : null;

        if (sniffedKind is not null)
        {
            var sniffed = Canonicalize(sniffedKind.Value, known) ?? sniffedKind.Value;
            if (TryResolveSharedShapeCostOverride(sniffed, matchingCosts, file, known) is { } costOverride)
                return costOverride;

            if (matchingCosts.Count == 0
                && workItemKind is { } itemKindForDispatchedPhase
                && IsDispatchedWorkPhase(file.Phase)
                && CanEmitSniffedShape(itemKindForDispatchedPhase, sniffed))
            {
                return itemKindForDispatchedPhase;
            }

            return sniffed;
        }

        foreach (var cost in matchingCosts)
        {
            if (Canonicalize(cost.AgentKind, known) is { } costKind)
                return costKind;
        }

        if (workItemKind is { } itemKind)
            return itemKind;

        return new AgentKind("unknown");
    }

    private static IEnumerable<WorkItemCost> MatchingCosts(
        WorkItem item,
        AgentStreamFile file,
        IReadOnlyList<WorkItemCost> costs) =>
        costs
            .Where(c => string.Equals(c.WorkItemId, item.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            .Where(c => c.EndedAt >= c.StartedAt)
            .Where(c => PhaseMatches(file.Phase, c.Phase))
            .Where(c => c.Iteration is null || c.Iteration == file.Iteration)
            .OrderByDescending(c => string.Equals(c.Phase, file.Phase, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.Iteration == file.Iteration)
            .ThenByDescending(c => c.StartedAt);

    private static bool IsDispatchedWorkPhase(string phase) =>
        string.Equals(phase, "work", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phase, "rework", StringComparison.OrdinalIgnoreCase)
        || string.Equals(phase, "merge", StringComparison.OrdinalIgnoreCase);

    private static AgentKind? TryResolveSharedShapeCostOverride(
        AgentKind sniffed,
        IReadOnlyList<WorkItemCost> matchingCosts,
        AgentStreamFile file,
        IReadOnlyCollection<AgentKind> known)
    {
        if (!IsSharedShapeSniff(sniffed))
            return null;

        var correlatedKinds = matchingCosts
            .Where(c => CaptureTimestampFallsInCostWindow(file, c))
            .Select(c => Canonicalize(c.AgentKind, known))
            .Where(k => k.HasValue)
            .Select(k => k!.Value)
            .Distinct()
            .ToList();

        return correlatedKinds.Count == 1 && CanEmitSniffedShape(correlatedKinds[0], sniffed)
            ? correlatedKinds[0]
            : null;
    }

    private static bool CaptureTimestampFallsInCostWindow(AgentStreamFile file, WorkItemCost cost)
    {
        var tolerance = TimeSpan.FromSeconds(5);
        return file.CapturedAt >= cost.StartedAt - tolerance
            && file.CapturedAt <= cost.EndedAt + tolerance;
    }

    private static bool IsSharedShapeSniff(AgentKind sniffed) =>
        string.Equals(sniffed.Value, AgentKind.Claude.Value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sniffed.Value, AgentKind.Gemini.Value, StringComparison.OrdinalIgnoreCase);

    private static bool CanEmitSniffedShape(AgentKind candidate, AgentKind sniffed)
    {
        if (string.Equals(candidate.Value, sniffed.Value, StringComparison.OrdinalIgnoreCase))
            return true;

        if (string.Equals(sniffed.Value, AgentKind.Claude.Value, StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(candidate.Value, AgentKind.Cursor.Value, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Value, AgentKind.Antigravity.Value, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(sniffed.Value, AgentKind.Gemini.Value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.Value, AgentKind.Antigravity.Value, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldTreatAsUnsupported(AgentKind kind, AgentStreamSummary summary)
    {
        if (summary.IsUnsupported)
            return true;

        return false;
    }

    public static AgentStreamParserContext? ResolveTimingContext(
        WorkItem item,
        AgentStreamFile file,
        AgentKind kind,
        IReadOnlyList<WorkItemCost> costs)
    {
        var cost = costs
            .Where(c => string.Equals(c.WorkItemId, item.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            .Where(c => c.EndedAt >= c.StartedAt)
            .Where(c => PhaseMatches(file.Phase, c.Phase))
            .Where(c => c.Iteration is null || c.Iteration == file.Iteration)
            .OrderByDescending(c => string.Equals(c.AgentKind, kind.Value, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => string.Equals(c.Phase, file.Phase, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(c => c.Iteration == file.Iteration)
            .ThenByDescending(c => c.StartedAt)
            .FirstOrDefault();

        return cost is null
            ? null
            : new AgentStreamParserContext(cost.StartedAt, cost.EndedAt, file.LineCount, file.SizeBytes);
    }

    public static AgentStreamSummary UnsupportedSummary() => AgentStreamSummary.Unsupported();

    private static AgentKind? Canonicalize(AgentKind kind, IReadOnlyCollection<AgentKind> known) =>
        Canonicalize(kind.Value, known);

    private static AgentKind? Canonicalize(string? value, IReadOnlyCollection<AgentKind> known)
    {
        if (value is null)
            return null;

        foreach (var k in known)
        {
            if (value.Equals(k.Value, StringComparison.OrdinalIgnoreCase))
                return k;
        }

        return null;
    }

    private static bool PhaseMatches(string filePhase, string costPhase) =>
        string.Equals(filePhase, costPhase, StringComparison.OrdinalIgnoreCase)
        || filePhase.StartsWith(costPhase + "-", StringComparison.OrdinalIgnoreCase);
}

public static class AgentStreamAnalytics
{
    public static AgentStreamAggregate Aggregate(string? workItemId, IEnumerable<AgentStreamSummaryRow> rows)
    {
        var materialized = rows.ToList();
        var toolCalls = materialized.SelectMany(r => r.Summary.ToolCalls).ToList();
        var byTool = toolCalls
            .GroupBy(t => t.ToolName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var durations = g.Select(t => ToMs(t.Duration)).Order().ToList();
                return new AgentStreamToolAggregate(
                    g.Key,
                    g.Count(),
                    durations.Sum(),
                    MedianMs(durations));
            })
            .OrderByDescending(t => t.TotalDurationMs)
            .ThenBy(t => t.Tool, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalDurationMs = materialized.Sum(r => ToMs(r.Summary.TotalDuration));
        var executingMs = materialized.Sum(r => UnionToolDurationMs(r.Summary.ToolCalls));
        var longestStallMs = materialized
            .SelectMany(r => r.Summary.Stalls)
            .Select(s => ToMs(s.GapDuration))
            .DefaultIfEmpty(0)
            .Max();

        return new AgentStreamAggregate(
            workItemId,
            totalDurationMs,
            toolCalls.Count,
            byTool,
            Math.Max(0, totalDurationMs - executingMs),
            executingMs,
            materialized.Sum(r => r.Summary.Stalls.Count),
            longestStallMs,
            materialized.Sum(r => r.Summary.EstimatedUsd ?? 0m));
    }

    private static long ToMs(TimeSpan? value) =>
        value.HasValue ? Math.Max(0, (long)Math.Round(value.Value.TotalMilliseconds)) : 0;

    private static long MedianMs(IReadOnlyList<long> sortedDurations)
    {
        if (sortedDurations.Count == 0)
            return 0;

        var middle = sortedDurations.Count / 2;
        if (sortedDurations.Count % 2 == 1)
            return sortedDurations[middle];

        return (long)Math.Round((sortedDurations[middle - 1] + sortedDurations[middle]) / 2.0);
    }

    private static long UnionToolDurationMs(IEnumerable<ToolCallInvocation> toolCalls)
    {
        var materialized = toolCalls.ToList();
        var intervals = materialized
            .Where(HasValidInterval)
            .Select(t => (Start: t.StartedAt!.Value, End: t.EndedAt!.Value))
            .OrderBy(t => t.Start)
            .ToList();

        var durationOnlyMs = materialized
            .Where(t => !HasValidInterval(t))
            .Sum(t => ToMs(t.Duration));

        if (intervals.Count == 0)
            return durationOnlyMs;

        var total = TimeSpan.Zero;
        var currentStart = intervals[0].Start;
        var currentEnd = intervals[0].End;
        foreach (var interval in intervals.Skip(1))
        {
            if (interval.Start <= currentEnd)
            {
                if (interval.End > currentEnd)
                    currentEnd = interval.End;
                continue;
            }

            total += currentEnd - currentStart;
            currentStart = interval.Start;
            currentEnd = interval.End;
        }

        total += currentEnd - currentStart;
        return ToMs(total) + durationOnlyMs;
    }

    private static bool HasValidInterval(ToolCallInvocation toolCall) =>
        toolCall.StartedAt.HasValue
        && toolCall.EndedAt.HasValue
        && toolCall.EndedAt.Value >= toolCall.StartedAt.Value;
}

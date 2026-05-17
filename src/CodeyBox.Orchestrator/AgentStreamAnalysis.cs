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
    /// (in order) the sniffed kind, recorded cost rows, and the work item's
    /// declared agent. Only kinds present in <paramref name="knownKinds"/> are
    /// canonicalised — anything else falls through to "unknown". Callers should
    /// pass the kinds of their registered <see cref="IAgentStreamParser"/>s so
    /// adding a new provider library is purely additive.
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

        if (sniffedKind is not null)
            return Canonicalize(sniffedKind.Value, known) ?? sniffedKind.Value;

        foreach (var cost in costs
                     .Where(c => string.Equals(c.WorkItemId, item.Id.ToString(), StringComparison.OrdinalIgnoreCase))
                     .Where(c => PhaseMatches(file.Phase, c.Phase))
                     .Where(c => c.Iteration is null || c.Iteration == file.Iteration)
                     .OrderByDescending(c => string.Equals(c.Phase, file.Phase, StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(c => c.StartedAt))
        {
            if (Canonicalize(cost.AgentKind, known) is { } costKind)
                return costKind;
        }

        if (item.Agent.HasValue && Canonicalize(item.Agent.Value, known) is { } itemKind)
            return itemKind;

        return new AgentKind("unknown");
    }

    public static bool ShouldTreatAsUnsupported(AgentKind kind, AgentStreamSummary summary)
    {
        if (summary.IsUnsupported || string.Equals(kind.Value, "unknown", StringComparison.OrdinalIgnoreCase))
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

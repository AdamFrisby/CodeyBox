using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using CodeyBox.Core;

namespace CodeyBox.Orchestrator;

public interface IAgentStreamParser
{
    AgentKind Kind { get; }
    Task<AgentStreamSummary> ParseAsync(Stream jsonlFile, CancellationToken ct = default);
}

public sealed record AgentStreamSummary(
    TimeSpan TotalDuration,
    TimeSpan? TimeToFirstToken,
    int InputTokens,
    int OutputTokens,
    int CachedInputTokens,
    decimal? EstimatedUsd,
    IReadOnlyList<ToolCallInvocation> ToolCalls,
    IReadOnlyList<StallEvent> Stalls,
    string? FinalAssistantMessage);

public sealed record ToolCallInvocation(
    string ToolUseId,
    string ToolName,
    string InputSummary,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    TimeSpan? Duration,
    bool? Succeeded,
    int OutputBytes);

public sealed record StallEvent(
    DateTimeOffset DetectedAt,
    TimeSpan GapDuration,
    string? PreviousEventType,
    string? NextEventType,
    string Classification);

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

public sealed class AgentStreamParserOptions
{
    public TimeSpan StallThreshold { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxLineBytes { get; set; } = 1024 * 1024;
    public int MaxJsonDepth { get; set; } = 64;
}

public interface IAgentStreamTimingSource
{
    DateTimeOffset? CapturedAt { get; }
    DateTimeOffset? CompletedAt { get; }
}

public sealed class ClaudeStreamParser : FlexibleAgentStreamParser
{
    public ClaudeStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Claude, options)
    {
    }
}

public sealed class CodexStreamParser : FlexibleAgentStreamParser
{
    public CodexStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Codex, options)
    {
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name") ?? "unknown";
        var timestamp = TryTimestamp(root);
        var starts = new List<ToolBuilder>();
        var results = new List<ToolResultBuilder>();
        var isAssistant = false;
        string? finalText = null;

        if (TryGet(root, out var item, "item"))
        {
            var itemType = FirstString(item, "type", "kind") ?? type;
            isAssistant = string.Equals(FirstString(item, "role"), "assistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "message", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "agent_message", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase);

            if (string.Equals(itemType, "command_execution", StringComparison.OrdinalIgnoreCase))
            {
                var id = FirstString(item, "id", "call_id", "tool_use_id") ?? Guid.NewGuid().ToString("N");
                if (string.Equals(type, "item.started", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(FirstString(item, "status"), "in_progress", StringComparison.OrdinalIgnoreCase))
                {
                    starts.Add(new ToolBuilder(
                        id,
                        CommandToolName(FirstString(item, "command")),
                        InputSummary(item),
                        timestamp));
                }

                if (string.Equals(type, "item.completed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(FirstString(item, "status"), "completed", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new ToolResultBuilder(id, CommandSucceeded(item), OutputBytes(item)));
                }
            }
            else if (string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(itemType, "tool_call", StringComparison.OrdinalIgnoreCase))
            {
                var id = FirstString(item, "call_id", "id", "tool_use_id") ?? Guid.NewGuid().ToString("N");
                var name = FirstString(item, "name", "tool_name") ?? "unknown";
                starts.Add(new ToolBuilder(id, name, InputSummary(item), timestamp));
            }
            else if (string.Equals(itemType, "function_call_output", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(itemType, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                var id = FirstString(item, "call_id", "id", "tool_use_id") ?? "unknown";
                results.Add(new ToolResultBuilder(id, !Bool(item, "is_error", "error"), OutputBytes(item)));
            }

            finalText = FirstString(item, "text", "final_message") ?? finalText;
            ParseContent(item, starts, results, ref finalText);
            ParseUsage(item, out var itemInput, out var itemOutput, out var itemCached);
            var parsed = ParseScalars(root, type, timestamp, starts, results, isAssistant, finalText);
            return parsed with
            {
                InputTokens = parsed.InputTokens ?? itemInput,
                OutputTokens = parsed.OutputTokens ?? itemOutput,
                CachedInputTokens = parsed.CachedInputTokens ?? itemCached,
            };
        }

        return base.ParseEvent(root);
    }

    private static bool? CommandSucceeded(JsonElement item)
    {
        if (!TryGet(item, out var exitCode, "exit_code") || exitCode.ValueKind == JsonValueKind.Null)
            return null;
        return exitCode.TryGetInt32(out var code) ? code == 0 : null;
    }

    private static string CommandToolName(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "command_execution";
        var fileName = Path.GetFileName(command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? command);
        return fileName.Equals("bash", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("sh", StringComparison.OrdinalIgnoreCase)
            ? "Bash"
            : "command_execution";
    }
}

public sealed class GeminiStreamParser : FlexibleAgentStreamParser
{
    public GeminiStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Gemini, options)
    {
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name") ?? "unknown";
        var timestamp = TryTimestamp(root);
        var starts = new List<ToolBuilder>();
        var results = new List<ToolResultBuilder>();
        var isAssistant = string.Equals(FirstString(root, "role"), "model", StringComparison.OrdinalIgnoreCase)
            || string.Equals(FirstString(root, "role"), "assistant", StringComparison.OrdinalIgnoreCase);
        string? finalText = null;

        ParseGeminiPayload(root, starts, results, ref finalText, ref isAssistant, timestamp);

        ParseUsage(root, out var input, out var output, out var cached);
        if (TryGet(root, out var usage, "usageMetadata", "usage_metadata"))
        {
            input ??= FirstInt(usage, "promptTokenCount", "prompt_token_count", "input_tokens", "prompt_tokens");
            output ??= FirstInt(usage, "candidatesTokenCount", "candidates_token_count", "output_tokens", "completion_tokens");
            cached ??= FirstInt(usage, "cachedContentTokenCount", "cached_content_token_count", "cached_input_tokens");
        }

        var parsed = ParseScalars(root, type, timestamp, starts, results, isAssistant, finalText);
        return parsed with
        {
            InputTokens = parsed.InputTokens ?? input,
            OutputTokens = parsed.OutputTokens ?? output,
            CachedInputTokens = parsed.CachedInputTokens ?? cached,
        };
    }

    private static void ParseGeminiPayload(
        JsonElement root,
        List<ToolBuilder> starts,
        List<ToolResultBuilder> results,
        ref string? text,
        ref bool isAssistant,
        DateTimeOffset? timestamp)
    {
        if (TryGet(root, out var functionCall, "functionCall", "function_call", "toolCall", "tool_call"))
        {
            isAssistant = true;
            var id = FirstString(functionCall, "id", "call_id", "name") ?? Guid.NewGuid().ToString("N");
            var name = FirstString(functionCall, "name", "tool_name") ?? "unknown";
            starts.Add(new ToolBuilder(id, name, InputSummary(functionCall), timestamp));
        }

        if (TryGet(root, out var functionResponse, "functionResponse", "function_response", "toolResult", "tool_result"))
        {
            var id = FirstString(functionResponse, "id", "call_id", "name") ?? "unknown";
            results.Add(new ToolResultBuilder(id, !Bool(functionResponse, "is_error", "error"), OutputBytes(functionResponse)));
        }

        if (TryGet(root, out var candidates, "candidates") && candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                isAssistant = true;
                if (TryGet(candidate, out var content, "content"))
                    ParseGeminiPayload(content, starts, results, ref text, ref isAssistant, timestamp);
            }
        }

        if (TryGet(root, out var parts, "parts") && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                ParseGeminiPayload(part, starts, results, ref text, ref isAssistant, timestamp);
                var partText = FirstString(part, "text", "content");
                if (!string.IsNullOrEmpty(partText))
                    text = text is null ? partText : text + partText;
            }
        }

        ParseContent(root, starts, results, ref text);
    }
}

public sealed class UnknownAgentStreamParser : IAgentStreamParser
{
    public AgentKind Kind { get; } = new("unknown");

    public Task<AgentStreamSummary> ParseAsync(Stream jsonlFile, CancellationToken ct = default) =>
        Task.FromResult(new AgentStreamSummary(TimeSpan.Zero, null, 0, 0, 0, null, [], [], null));
}

internal sealed record AgentStreamJsonLine(string Text, long StartOffset, long EndOffset);

internal static class AgentStreamJsonLineReader
{
    public static async IAsyncEnumerable<AgentStreamJsonLine> ReadLinesAsync(
        Stream stream,
        int maxLineBytes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        maxLineBytes = Math.Max(1, maxLineBytes);
        var buffer = new byte[16 * 1024];
        await using var line = new MemoryStream(capacity: Math.Min(maxLineBytes, 16 * 1024));
        long offset = 0;
        long lineStart = 0;
        var discarding = false;

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];
                offset++;
                if (discarding)
                {
                    if (b == (byte)'\n')
                    {
                        discarding = false;
                        line.SetLength(0);
                        lineStart = offset;
                    }
                    continue;
                }

                if (b == (byte)'\n')
                {
                    var text = DecodeLine(line);
                    line.SetLength(0);
                    yield return new AgentStreamJsonLine(text, lineStart, offset);
                    lineStart = offset;
                    continue;
                }

                if (line.Length >= maxLineBytes)
                {
                    discarding = true;
                    line.SetLength(0);
                    continue;
                }

                line.WriteByte(b);
            }
        }

        if (!discarding && line.Length > 0)
            yield return new AgentStreamJsonLine(DecodeLine(line), lineStart, offset);
    }

    private static string DecodeLine(MemoryStream line)
    {
        var span = line.GetBuffer().AsSpan(0, (int)line.Length);
        if (span.Length > 0 && span[^1] == (byte)'\r')
            span = span[..^1];
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];
        return Encoding.UTF8.GetString(span);
    }
}

public static class AgentStreamParserSelection
{
    private const int MaxSniffLines = 20;

    public static async Task<AgentKind?> SniffKindAsync(Stream jsonlFile, CancellationToken ct = default)
    {
        var read = 0;
        await foreach (var jsonLine in AgentStreamJsonLineReader.ReadLinesAsync(jsonlFile, 1024 * 1024, ct).ConfigureAwait(false))
        {
            if (read++ >= MaxSniffLines)
                break;
            var line = jsonLine.Text;
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 });
                if (SniffKind(doc.RootElement) is { } kind)
                    return kind;
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return null;
    }

    public static AgentKind ResolveKind(
        WorkItem item,
        AgentStreamFile file,
        AgentKind? sniffedKind,
        IReadOnlyList<WorkItemCost> costs)
    {
        if (sniffedKind is not null)
            return sniffedKind.Value;

        var candidatePhases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.Phase };
        if (file.Phase.StartsWith("audit-llm-", StringComparison.OrdinalIgnoreCase))
            candidatePhases.Add("audit");

        var matchingKinds = costs
            .Where(c => candidatePhases.Contains(c.Phase)
                && ((!c.Iteration.HasValue && file.Iteration <= 1) || c.Iteration == file.Iteration))
            .Select(c => new AgentKind(c.AgentKind))
            .Distinct()
            .ToList();
        if (matchingKinds.Count == 1)
            return matchingKinds[0];

        return item.Agent ?? new AgentKind("unknown");
    }

    private static AgentKind? SniffKind(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name");
        if (type is not null
            && (type.StartsWith("thread.", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("turn.", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("item.", StringComparison.OrdinalIgnoreCase)))
            return AgentKind.Codex;

        if (TryGet(root, out _, "item"))
            return AgentKind.Codex;

        if (TryGet(root, out _, "usageMetadata", "usage_metadata", "candidates", "functionCall", "function_call"))
            return AgentKind.Gemini;

        if (type is "assistant" or "user" or "result" or "tool_use" or "tool_result")
            return AgentKind.Claude;

        return null;
    }

    private static string? FirstString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static bool TryGet(JsonElement el, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value))
                return true;
        }

        value = default;
        return false;
    }
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
                    durations.Count == 0 ? 0 : durations[durations.Count / 2]);
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

    private static long UnionToolDurationMs(IEnumerable<ToolCallInvocation> toolCalls)
    {
        var intervals = toolCalls
            .Where(t => t.StartedAt.HasValue && t.EndedAt.HasValue && t.EndedAt.Value >= t.StartedAt.Value)
            .Select(t => (Start: t.StartedAt!.Value, End: t.EndedAt!.Value))
            .OrderBy(t => t.Start)
            .ToList();
        if (intervals.Count == 0)
            return 0;

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
        return ToMs(total);
    }
}

public abstract class FlexibleAgentStreamParser : IAgentStreamParser
{
    private readonly AgentStreamParserOptions _options;

    protected FlexibleAgentStreamParser(AgentKind kind, AgentStreamParserOptions? options)
    {
        Kind = kind;
        _options = options ?? new AgentStreamParserOptions();
    }

    public AgentKind Kind { get; }

    public async Task<AgentStreamSummary> ParseAsync(Stream jsonlFile, CancellationToken ct = default)
    {
        var toolStarts = new Dictionary<string, ToolBuilder>(StringComparer.Ordinal);
        var completedTools = new List<ToolCallInvocation>();
        DateTimeOffset? firstTimestamp = null;
        DateTimeOffset? lastTimestamp = null;
        DateTimeOffset? firstAssistantTimestamp = null;
        string? lastEventType = null;
        var stalls = new List<StallEvent>();
        var inputTokens = 0;
        var outputTokens = 0;
        var cachedInputTokens = 0;
        decimal? estimatedUsd = null;
        string? finalText = null;

        var fallbackStart = (jsonlFile as IAgentStreamTimingSource)?.CapturedAt;
        var fallbackEnd = (jsonlFile as IAgentStreamTimingSource)?.CompletedAt;
        var streamLength = TryGetLength(jsonlFile);

        await foreach (var jsonLine in AgentStreamJsonLineReader.ReadLinesAsync(jsonlFile, _options.MaxLineBytes, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var line = jsonLine.Text;
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                continue;

            ParsedEvent parsed;
            try
            {
                using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = _options.MaxJsonDepth });
                parsed = ParseEvent(doc.RootElement);
            }
            catch (JsonException)
            {
                continue;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var timestamp = parsed.Timestamp ?? EstimateTimestamp(fallbackStart, fallbackEnd, streamLength, jsonLine.StartOffset);
            if (timestamp is { } eventTimestamp)
            {
                firstTimestamp ??= eventTimestamp;
                if (lastTimestamp is { } previous
                    && eventTimestamp - previous > _options.StallThreshold
                    && !string.Equals(lastEventType, "result", StringComparison.OrdinalIgnoreCase))
                {
                    stalls.Add(new StallEvent(
                        previous,
                        eventTimestamp - previous,
                        lastEventType,
                        parsed.EventType,
                        ClassifyStall(lastEventType, toolStarts.Count)));
                }

                lastTimestamp = eventTimestamp;
                if (parsed.IsAssistant && firstAssistantTimestamp is null)
                    firstAssistantTimestamp = eventTimestamp;
            }

            lastEventType = parsed.EventType;

            foreach (var tool in parsed.ToolStarts)
                toolStarts[tool.Id] = tool with { StartedAt = timestamp };

            foreach (var result in parsed.ToolResults)
            {
                if (toolStarts.Remove(result.Id, out var started))
                {
                    var duration = started.StartedAt.HasValue && timestamp.HasValue
                        ? timestamp.Value - started.StartedAt.Value
                        : (TimeSpan?)null;
                    if (duration.HasValue && duration.Value < TimeSpan.Zero)
                        duration = null;

                    completedTools.Add(new ToolCallInvocation(
                        started.Id,
                        started.Name,
                        started.InputSummary,
                        started.StartedAt,
                        timestamp,
                        duration,
                        result.Succeeded,
                        result.OutputBytes));
                }
            }

            if (parsed.InputTokens.HasValue) inputTokens = parsed.InputTokens.Value;
            if (parsed.OutputTokens.HasValue) outputTokens = parsed.OutputTokens.Value;
            if (parsed.CachedInputTokens.HasValue) cachedInputTokens = parsed.CachedInputTokens.Value;
            if (parsed.EstimatedUsd.HasValue) estimatedUsd = parsed.EstimatedUsd.Value;
            if (!string.IsNullOrWhiteSpace(parsed.FinalText)) finalText = parsed.FinalText;
        }

        foreach (var unfinished in toolStarts.Values.OrderBy(t => t.StartedAt ?? DateTimeOffset.MaxValue))
        {
            completedTools.Add(new ToolCallInvocation(
                unfinished.Id,
                unfinished.Name,
                unfinished.InputSummary,
                unfinished.StartedAt,
                null,
                null,
                null,
                0));
        }

        var total = firstTimestamp.HasValue && lastTimestamp.HasValue
            ? lastTimestamp.Value - firstTimestamp.Value
            : TimeSpan.Zero;
        var ttft = firstTimestamp.HasValue && firstAssistantTimestamp.HasValue
            ? firstAssistantTimestamp.Value - firstTimestamp.Value
            : (TimeSpan?)null;

        return new AgentStreamSummary(
            total < TimeSpan.Zero ? TimeSpan.Zero : total,
            ttft is { } t && t >= TimeSpan.Zero ? t : null,
            inputTokens,
            outputTokens,
            cachedInputTokens,
            estimatedUsd,
            completedTools
                .OrderByDescending(t => t.Duration ?? TimeSpan.Zero)
                .ThenBy(t => t.StartedAt ?? DateTimeOffset.MaxValue)
                .ToList(),
            stalls,
            finalText);
    }

    private static string ClassifyStall(string? previousEventType, int openToolCount)
    {
        if (openToolCount > 0 || string.Equals(previousEventType, "tool_use", StringComparison.OrdinalIgnoreCase))
            return "tool_execution";
        if (string.Equals(previousEventType, "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(previousEventType, "tool_result", StringComparison.OrdinalIgnoreCase))
            return "llm";
        return "unknown";
    }

    private static long? TryGetLength(Stream stream)
    {
        try
        {
            return stream.Length > 0 ? stream.Length : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? EstimateTimestamp(
        DateTimeOffset? fallbackStart,
        DateTimeOffset? fallbackEnd,
        long? streamLength,
        long lineStartOffset)
    {
        if (!fallbackStart.HasValue || !fallbackEnd.HasValue || !streamLength.HasValue)
            return null;

        var span = fallbackEnd.Value - fallbackStart.Value;
        if (span <= TimeSpan.Zero || streamLength.Value <= 1)
            return null;

        var ratio = Math.Clamp(lineStartOffset / (double)(streamLength.Value - 1), 0d, 1d);
        return fallbackStart.Value + TimeSpan.FromTicks((long)Math.Round(span.Ticks * ratio));
    }

    protected virtual ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name") ?? "unknown";
        var timestamp = TryTimestamp(root);
        var starts = new List<ToolBuilder>();
        var results = new List<ToolResultBuilder>();
        var isAssistant = string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(FirstString(root, "role"), "assistant", StringComparison.OrdinalIgnoreCase);
        string? finalText = null;

        if (TryGet(root, out var message, "message"))
        {
            isAssistant |= string.Equals(FirstString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase);
            ParseContent(message, starts, results, ref finalText);
            ParseUsage(message, out var msgInput, out var msgOutput, out var msgCached);
            var parsed = ParseScalars(root, type, timestamp, starts, results, isAssistant, finalText);
            return parsed with
            {
                InputTokens = parsed.InputTokens ?? msgInput,
                OutputTokens = parsed.OutputTokens ?? msgOutput,
                CachedInputTokens = parsed.CachedInputTokens ?? msgCached,
            };
        }

        ParseContent(root, starts, results, ref finalText);
        return ParseScalars(root, type, timestamp, starts, results, isAssistant, finalText);
    }

    protected static ParsedEvent ParseScalars(
        JsonElement root,
        string type,
        DateTimeOffset? timestamp,
        IReadOnlyList<ToolBuilder> starts,
        IReadOnlyList<ToolResultBuilder> results,
        bool isAssistant,
        string? contentText)
    {
        if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase))
        {
            var id = FirstString(root, "tool_use_id", "call_id", "id") ?? "unknown";
            results = results.Concat([new ToolResultBuilder(id, !Bool(root, "is_error", "error"), OutputBytes(root))]).ToList();
        }

        if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            var id = FirstString(root, "tool_use_id", "call_id", "id") ?? Guid.NewGuid().ToString("N");
            var name = FirstString(root, "name", "tool_name") ?? "unknown";
            starts = starts.Concat([new ToolBuilder(id, name, InputSummary(root), timestamp)]).ToList();
        }

        ParseUsage(root, out var inputTokens, out var outputTokens, out var cachedInputTokens);
        var finalText = FirstString(root, "result", "final", "final_message", "text", "content") ?? contentText;
        var cost = FirstDecimal(root, "total_cost_usd", "cost_usd", "estimated_usd");
        return new ParsedEvent(
            NormalizeType(type, starts, results),
            timestamp,
            isAssistant,
            starts,
            results,
            inputTokens,
            outputTokens,
            cachedInputTokens,
            cost,
            string.Equals(type, "result", StringComparison.OrdinalIgnoreCase) ? finalText : contentText);
    }

    protected static string NormalizeType(string type, IReadOnlyList<ToolBuilder> starts, IReadOnlyList<ToolResultBuilder> results)
    {
        if (starts.Count > 0) return "tool_use";
        if (results.Count > 0) return "tool_result";
        return type;
    }

    protected static void ParseContent(JsonElement root, List<ToolBuilder> starts, List<ToolResultBuilder> results, ref string? text)
    {
        if (!TryGet(root, out var content, "content", "items", "parts"))
            return;

        if (content.ValueKind == JsonValueKind.String)
        {
            text = content.GetString();
            return;
        }

        if (content.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in content.EnumerateArray())
        {
            var itemType = FirstString(item, "type", "kind") ?? "";
            if (string.Equals(itemType, "tool_use", StringComparison.OrdinalIgnoreCase)
                || string.Equals(itemType, "function_call", StringComparison.OrdinalIgnoreCase))
            {
                var id = FirstString(item, "id", "tool_use_id", "call_id") ?? Guid.NewGuid().ToString("N");
                var name = FirstString(item, "name", "tool_name") ?? "unknown";
                starts.Add(new ToolBuilder(id, name, InputSummary(item), null));
            }
            else if (string.Equals(itemType, "tool_result", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(itemType, "function_response", StringComparison.OrdinalIgnoreCase))
            {
                var id = FirstString(item, "tool_use_id", "id", "call_id") ?? "unknown";
                results.Add(new ToolResultBuilder(id, !Bool(item, "is_error", "error"), OutputBytes(item)));
            }
            else if (string.Equals(itemType, "text", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(itemType, "output_text", StringComparison.OrdinalIgnoreCase))
            {
                var part = FirstString(item, "text", "content");
                if (!string.IsNullOrEmpty(part))
                    text = text is null ? part : text + part;
            }
        }
    }

    protected static void ParseUsage(JsonElement root, out int? input, out int? output, out int? cached)
    {
        input = FirstInt(root, "input_tokens", "prompt_tokens");
        output = FirstInt(root, "output_tokens", "completion_tokens");
        cached = FirstInt(root, "cache_creation_input_tokens", "cache_read_input_tokens", "cached_input_tokens", "cached_tokens");
        if (TryGet(root, out var usage, "usage", "token_usage"))
        {
            input ??= FirstInt(usage, "input_tokens", "prompt_tokens");
            output ??= FirstInt(usage, "output_tokens", "completion_tokens");
            cached ??= FirstInt(usage, "cache_creation_input_tokens", "cache_read_input_tokens", "cached_input_tokens", "cached_tokens");
        }
    }

    protected static DateTimeOffset? TryTimestamp(JsonElement root)
    {
        var raw = FirstString(root, "timestamp", "created_at", "time");
        if (raw is not null && DateTimeOffset.TryParse(raw, out var dto))
            return dto;
        var unixMs = FirstLong(root, "timestamp_ms", "created_at_ms");
        if (unixMs.HasValue)
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMs.Value);
        var unixSeconds = FirstLong(root, "created", "timestamp");
        return unixSeconds.HasValue ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) : null;
    }

    protected static string InputSummary(JsonElement el)
    {
        JsonElement input;
        if (!TryGet(el, out input, "input", "arguments", "args"))
            input = el;
        var text = input.ValueKind == JsonValueKind.String ? input.GetString() ?? "" : input.GetRawText();
        text = SecretRedactor.Redact(text).ReplaceLineEndings(" ");
        return text.Length <= 200 ? text : text[..200];
    }

    protected static int OutputBytes(JsonElement el)
    {
        var content = FirstString(el, "content", "output", "aggregated_output", "result") ?? el.GetRawText();
        return Encoding.UTF8.GetByteCount(content);
    }

    protected static bool Bool(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(el, out var value, name))
            {
                if (value.ValueKind == JsonValueKind.True) return true;
                if (value.ValueKind == JsonValueKind.False) return false;
                if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                    return parsed;
            }
        }
        return false;
    }

    protected static string? FirstString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    protected static int? FirstInt(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.TryGetInt32(out var parsed))
                return parsed;
        return null;
    }

    protected static long? FirstLong(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.TryGetInt64(out var parsed))
                return parsed;
        return null;
    }

    protected static decimal? FirstDecimal(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.TryGetDecimal(out var parsed))
                return parsed;
        return null;
    }

    protected static bool TryGet(JsonElement el, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value))
                return true;
        }
        value = default;
        return false;
    }

    protected sealed record ParsedEvent(
        string EventType,
        DateTimeOffset? Timestamp,
        bool IsAssistant,
        IReadOnlyList<ToolBuilder> ToolStarts,
        IReadOnlyList<ToolResultBuilder> ToolResults,
        int? InputTokens,
        int? OutputTokens,
        int? CachedInputTokens,
        decimal? EstimatedUsd,
        string? FinalText);

    protected sealed record ToolBuilder(string Id, string Name, string InputSummary, DateTimeOffset? StartedAt);
    protected sealed record ToolResultBuilder(string Id, bool? Succeeded, int OutputBytes);
}

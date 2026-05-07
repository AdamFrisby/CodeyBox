using System.Text;
using System.Text.Json;
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
    DateTimeOffset StartedAt,
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
}

public sealed class GeminiStreamParser : FlexibleAgentStreamParser
{
    public GeminiStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Gemini, options)
    {
    }
}

public sealed class UnknownAgentStreamParser : IAgentStreamParser
{
    public AgentKind Kind { get; } = new("unknown");

    public Task<AgentStreamSummary> ParseAsync(Stream jsonlFile, CancellationToken ct = default) =>
        Task.FromResult(new AgentStreamSummary(TimeSpan.Zero, null, 0, 0, 0, null, [], [], null));
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
        var executingMs = toolCalls.Sum(t => ToMs(t.Duration));
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
        var events = new List<ParsedEvent>();
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

        using var reader = new StreamReader(jsonlFile, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{'))
                continue;

            ParsedEvent parsed;
            try
            {
                using var doc = JsonDocument.Parse(line);
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

            if (parsed.Timestamp is not { } timestamp)
                timestamp = lastTimestamp?.AddMilliseconds(1) ?? DateTimeOffset.UnixEpoch;

            firstTimestamp ??= timestamp;
            if (lastTimestamp is { } previous && timestamp - previous > _options.StallThreshold && !string.Equals(lastEventType, "result", StringComparison.OrdinalIgnoreCase))
            {
                stalls.Add(new StallEvent(
                    previous,
                    timestamp - previous,
                    lastEventType,
                    parsed.EventType,
                    ClassifyStall(lastEventType, toolStarts.Count)));
            }

            lastTimestamp = timestamp;
            lastEventType = parsed.EventType;
            if (parsed.IsAssistant && firstAssistantTimestamp is null)
                firstAssistantTimestamp = timestamp;

            foreach (var tool in parsed.ToolStarts)
                toolStarts[tool.Id] = tool with { StartedAt = timestamp };

            foreach (var result in parsed.ToolResults)
            {
                if (toolStarts.Remove(result.Id, out var started))
                {
                    completedTools.Add(new ToolCallInvocation(
                        started.Id,
                        started.Name,
                        started.InputSummary,
                        started.StartedAt,
                        timestamp,
                        timestamp - started.StartedAt,
                        result.Succeeded,
                        result.OutputBytes));
                }
            }

            if (parsed.InputTokens.HasValue) inputTokens = parsed.InputTokens.Value;
            if (parsed.OutputTokens.HasValue) outputTokens = parsed.OutputTokens.Value;
            if (parsed.CachedInputTokens.HasValue) cachedInputTokens = parsed.CachedInputTokens.Value;
            if (parsed.EstimatedUsd.HasValue) estimatedUsd = parsed.EstimatedUsd.Value;
            if (!string.IsNullOrWhiteSpace(parsed.FinalText)) finalText = parsed.FinalText;
            events.Add(parsed with { Timestamp = timestamp });
        }

        foreach (var unfinished in toolStarts.Values.OrderBy(t => t.StartedAt))
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
            completedTools.OrderByDescending(t => t.Duration ?? TimeSpan.Zero).ToList(),
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

    private static ParsedEvent ParseEvent(JsonElement root)
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

    private static ParsedEvent ParseScalars(
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
            starts = starts.Concat([new ToolBuilder(id, name, InputSummary(root), timestamp ?? DateTimeOffset.UnixEpoch)]).ToList();
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

    private static string NormalizeType(string type, IReadOnlyList<ToolBuilder> starts, IReadOnlyList<ToolResultBuilder> results)
    {
        if (starts.Count > 0) return "tool_use";
        if (results.Count > 0) return "tool_result";
        return type;
    }

    private static void ParseContent(JsonElement root, List<ToolBuilder> starts, List<ToolResultBuilder> results, ref string? text)
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
                starts.Add(new ToolBuilder(id, name, InputSummary(item), DateTimeOffset.UnixEpoch));
            }
            else if (string.Equals(itemType, "tool_result", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(itemType, "function_response", StringComparison.OrdinalIgnoreCase))
            {
                var id = FirstString(item, "tool_use_id", "id", "call_id") ?? "unknown";
                results.Add(new ToolResultBuilder(id, !Bool(item, "is_error", "error"), OutputBytes(item)));
            }
            else if (string.Equals(itemType, "text", StringComparison.OrdinalIgnoreCase))
            {
                var part = FirstString(item, "text", "content");
                if (!string.IsNullOrEmpty(part))
                    text = text is null ? part : text + part;
            }
        }
    }

    private static void ParseUsage(JsonElement root, out int? input, out int? output, out int? cached)
    {
        input = FirstInt(root, "input_tokens", "prompt_tokens");
        output = FirstInt(root, "output_tokens", "completion_tokens");
        cached = FirstInt(root, "cache_creation_input_tokens", "cached_input_tokens", "cached_tokens");
        if (TryGet(root, out var usage, "usage", "token_usage"))
        {
            input ??= FirstInt(usage, "input_tokens", "prompt_tokens");
            output ??= FirstInt(usage, "output_tokens", "completion_tokens");
            cached ??= FirstInt(usage, "cache_creation_input_tokens", "cached_input_tokens", "cached_tokens");
        }
    }

    private static DateTimeOffset? TryTimestamp(JsonElement root)
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

    private static string InputSummary(JsonElement el)
    {
        JsonElement input;
        if (!TryGet(el, out input, "input", "arguments", "args"))
            input = el;
        var text = input.ValueKind == JsonValueKind.String ? input.GetString() ?? "" : input.GetRawText();
        text = SecretRedactor.Redact(text).ReplaceLineEndings(" ");
        return text.Length <= 200 ? text : text[..200];
    }

    private static int OutputBytes(JsonElement el)
    {
        var content = FirstString(el, "content", "output", "result") ?? el.GetRawText();
        return Encoding.UTF8.GetByteCount(content);
    }

    private static bool Bool(JsonElement el, params string[] names)
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

    private static string? FirstString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static int? FirstInt(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.TryGetInt32(out var parsed))
                return parsed;
        return null;
    }

    private static long? FirstLong(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.TryGetInt64(out var parsed))
                return parsed;
        return null;
    }

    private static decimal? FirstDecimal(JsonElement el, params string[] names)
    {
        foreach (var name in names)
            if (TryGet(el, out var value, name) && value.TryGetDecimal(out var parsed))
                return parsed;
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

    private sealed record ParsedEvent(
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

    private sealed record ToolBuilder(string Id, string Name, string InputSummary, DateTimeOffset StartedAt);
    private sealed record ToolResultBuilder(string Id, bool? Succeeded, int OutputBytes);
}

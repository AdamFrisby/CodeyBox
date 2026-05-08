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

public interface IAgentStreamParserWithContext : IAgentStreamParser
{
    Task<AgentStreamSummary> ParseAsync(
        Stream jsonlFile,
        AgentStreamParserContext? context,
        CancellationToken ct = default);
}

public sealed record AgentStreamParserContext(
    DateTimeOffset InvocationStartedAt,
    DateTimeOffset InvocationEndedAt,
    long? LineCount,
    long? SizeBytes);

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
    public int MaxLineBytes { get; set; } = 64 * 1024 * 1024;
    public int MaxJsonDepth { get; set; } = 64;
    public int MaxEvents { get; set; } = 50_000;
    public int MaxToolCalls { get; set; } = 2_000;
    public int MaxStalls { get; set; } = 2_000;
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
        if (TryGet(root, out var payload, "payload"))
            return ParsePayloadEvent(root, payload, type, timestamp);

        var eventTimestamp = timestamp;
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
                var itemStartedAt = TryTimestamp(item, "started_at", "start_time", "startedAt", "started_at_unix_ms", "started_at_ms");
                var itemEndedAt = TryTimestamp(item, "completed_at", "ended_at", "end_time", "completedAt", "endedAt", "completed_at_unix_ms", "ended_at_unix_ms", "completed_at_ms", "ended_at_ms");
                var itemDuration = FirstDuration(item);
                if (string.Equals(type, "item.started", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(FirstString(item, "status"), "in_progress", StringComparison.OrdinalIgnoreCase))
                {
                    eventTimestamp = itemStartedAt ?? eventTimestamp;
                    starts.Add(new ToolBuilder(
                        id,
                        CommandToolName(FirstString(item, "command")),
                        InputSummary(item),
                        itemStartedAt ?? timestamp));
                }

                if (string.Equals(type, "item.completed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(FirstString(item, "status"), "completed", StringComparison.OrdinalIgnoreCase))
                {
                    eventTimestamp = itemEndedAt ?? eventTimestamp;
                    results.Add(new ToolResultBuilder(id, CommandSucceeded(item), OutputBytes(item), itemEndedAt ?? timestamp, itemDuration));
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
                results.Add(new ToolResultBuilder(id, !Bool(item, "is_error", "error"), OutputBytes(item), timestamp, FirstDuration(item)));
            }

            finalText = FirstString(item, "text", "final_message") ?? finalText;
            ParseContent(item, starts, results, ref finalText);
            ParseUsage(item, out var itemInput, out var itemOutput, out var itemCached);
            var parsed = ParseScalars(root, type, eventTimestamp, starts, results, isAssistant, finalText);
            return parsed with
            {
                InputTokens = parsed.InputTokens ?? itemInput,
                OutputTokens = parsed.OutputTokens ?? itemOutput,
                CachedInputTokens = parsed.CachedInputTokens ?? itemCached,
            };
        }

        return base.ParseEvent(root);
    }

    private static ParsedEvent ParsePayloadEvent(
        JsonElement root,
        JsonElement payload,
        string wrapperType,
        DateTimeOffset? wrapperTimestamp)
    {
        if (TryGet(payload, out var nestedPayload, "payload"))
            payload = nestedPayload;

        if (TryGet(payload, out var nestedItem, "item"))
            payload = nestedItem;

        var payloadType = FirstString(payload, "type", "event", "name", "kind") ?? wrapperType;
        var eventTimestamp = TryTimestamp(payload) ?? wrapperTimestamp;
        var starts = new List<ToolBuilder>();
        var results = new List<ToolResultBuilder>();
        var isAssistant = string.Equals(FirstString(payload, "role"), "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payloadType, "message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payloadType, "agent_message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payloadType, "function_call", StringComparison.OrdinalIgnoreCase);
        string? finalText = FirstString(payload, "text", "message", "final_message");

        if (string.Equals(payloadType, "command_execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payloadType, "execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payloadType, "exec_command_begin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payloadType, "mcp_tool_call_begin", StringComparison.OrdinalIgnoreCase))
        {
            var id = FirstString(payload, "id", "call_id", "tool_use_id", "invocation_id")
                ?? CommandId(payload)
                ?? Guid.NewGuid().ToString("N");
            var startedAt = TryTimestamp(payload, "started_at", "start_time", "startedAt", "started_at_unix_ms", "started_at_ms")
                ?? eventTimestamp;
            eventTimestamp = startedAt ?? eventTimestamp;
            starts.Add(new ToolBuilder(
                id,
                CommandToolName(FirstString(payload, "command", "tool_name", "name")),
                InputSummary(payload),
                startedAt));
        }

        if (string.Equals(payloadType, "command_execution", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payloadType, "exec_command_end", StringComparison.OrdinalIgnoreCase)
            || string.Equals(payloadType, "mcp_tool_call_end", StringComparison.OrdinalIgnoreCase))
        {
            var status = FirstString(payload, "status");
            if (string.Equals(payloadType, "exec_command_end", StringComparison.OrdinalIgnoreCase)
                || string.Equals(payloadType, "mcp_tool_call_end", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
                || TryGet(payload, out _, "completed_at", "ended_at", "completed_at_unix_ms", "ended_at_unix_ms", "duration_ms"))
            {
                var id = FirstString(payload, "id", "call_id", "tool_use_id", "invocation_id")
                    ?? CommandId(payload)
                    ?? "unknown";
                var endedAt = TryTimestamp(payload, "completed_at", "ended_at", "end_time", "completedAt", "endedAt", "completed_at_unix_ms", "ended_at_unix_ms", "completed_at_ms", "ended_at_ms")
                    ?? eventTimestamp;
                eventTimestamp = endedAt ?? eventTimestamp;
                results.Add(new ToolResultBuilder(id, CommandSucceeded(payload), OutputBytes(payload), endedAt, FirstDuration(payload)));
            }
        }

        ParseContent(payload, starts, results, ref finalText);
        var parsed = ParseScalars(payload, payloadType, eventTimestamp, starts, results, isAssistant, finalText);
        var (input, output, cached) = ParseCodexUsage(root, payload);
        var totalDuration = parsed.TotalDuration;
        if (totalDuration is null
            && (string.Equals(payloadType, "turn_complete", StringComparison.OrdinalIgnoreCase)
                || string.Equals(payloadType, "task_complete", StringComparison.OrdinalIgnoreCase)))
        {
            totalDuration = FirstDuration(payload);
        }

        return parsed with
        {
            InputTokens = parsed.InputTokens ?? input,
            OutputTokens = parsed.OutputTokens ?? output,
            CachedInputTokens = parsed.CachedInputTokens ?? cached,
            TotalDuration = totalDuration,
            TimeToFirstToken = parsed.TimeToFirstToken
                ?? FirstDuration(payload, "time_to_first_token_ms", "ttft_ms", "ttft_duration_ms"),
        };
    }

    private static string? CommandId(JsonElement payload)
    {
        if (TryGet(payload, out var parsedCommand, "parsed_cmd", "parsedCommand"))
            return FirstString(parsedCommand, "call_id", "id", "tool_use_id");

        if (TryGet(payload, out var invocation, "invocation"))
            return FirstString(invocation, "call_id", "id", "tool_use_id");

        return null;
    }

    private static (int? Input, int? Output, int? Cached) ParseCodexUsage(params JsonElement[] roots)
    {
        int? input = null;
        int? output = null;
        int? cached = null;
        foreach (var root in roots)
            ParseCodexUsage(root, ref input, ref output, ref cached, depth: 0);
        return (input, output, cached);
    }

    private static void ParseCodexUsage(JsonElement root, ref int? input, ref int? output, ref int? cached, int depth)
    {
        if (depth > 8)
            return;

        ParseUsage(root, out var directInput, out var directOutput, out var directCached);
        input ??= directInput;
        output ??= directOutput;
        cached ??= directCached;

        if (root.ValueKind == JsonValueKind.String)
        {
            var raw = root.GetString();
            if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith('{'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    ParseCodexUsage(doc.RootElement, ref input, ref output, ref cached, depth + 1);
                }
                catch (JsonException)
                {
                }
            }

            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return;

        foreach (var name in new[]
        {
            "total_token_usage", "token_usage", "usage", "token_usage_json", "info", "last_token_usage",
        })
        {
            if (TryGet(root, out var child, name))
                ParseCodexUsage(child, ref input, ref output, ref cached, depth + 1);
        }

        if (input.HasValue && output.HasValue && cached.HasValue)
            return;

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && (property.Name.Contains("usage", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Contains("token", StringComparison.OrdinalIgnoreCase)))
            {
                ParseCodexUsage(property.Value, ref input, ref output, ref cached, depth + 1);
            }
        }
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
            results.Add(new ToolResultBuilder(id, !Bool(functionResponse, "is_error", "error"), OutputBytes(functionResponse), timestamp, FirstDuration(functionResponse)));
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

internal sealed record AgentStreamJsonLine(string Text, long LineNumber, long StartOffset, long EndOffset);

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
        long lineNumber = 0;
        long lineStartOffset = 0;
        long offset = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
                break;

            for (var i = 0; i < read; i++)
            {
                var b = buffer[i];

                if (b == (byte)'\n')
                {
                    var text = DecodeLine(line);
                    line.SetLength(0);
                    yield return new AgentStreamJsonLine(text, lineNumber++, lineStartOffset, offset + 1);
                    offset++;
                    lineStartOffset = offset;
                    continue;
                }

                if (line.Length >= maxLineBytes)
                    throw new InvalidDataException($"Agent stream JSONL line exceeded the configured limit of {maxLineBytes} bytes");

                line.WriteByte(b);
                offset++;
            }
        }

        if (line.Length > 0)
            yield return new AgentStreamJsonLine(DecodeLine(line), lineNumber, lineStartOffset, offset);
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
    private const int MaxSniffLineBytes = 64 * 1024 * 1024;

    public static async Task<AgentKind?> SniffKindAsync(Stream jsonlFile, CancellationToken ct = default)
    {
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
        }
        catch (InvalidDataException)
        {
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
            return Canonicalize(sniffedKind.Value) ?? sniffedKind.Value;

        foreach (var cost in costs
                     .Where(c => string.Equals(c.WorkItemId, item.Id.ToString(), StringComparison.OrdinalIgnoreCase))
                     .Where(c => PhaseMatches(file.Phase, c.Phase))
                     .Where(c => c.Iteration is null || c.Iteration == file.Iteration)
                     .OrderByDescending(c => string.Equals(c.Phase, file.Phase, StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(c => c.StartedAt))
        {
            if (Canonicalize(cost.AgentKind) is { } costKind)
                return costKind;
        }

        if (item.Agent.HasValue && Canonicalize(item.Agent.Value) is { } itemKind)
            return itemKind;

        return new AgentKind("unknown");
    }

    public static bool ShouldTreatAsUnsupported(AgentKind kind, AgentStreamSummary summary)
    {
        if (string.Equals(kind.Value, "unknown", StringComparison.OrdinalIgnoreCase))
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

    public static AgentStreamSummary UnsupportedSummary() =>
        new(TimeSpan.Zero, null, 0, 0, 0, null, [], [], null);

    private static AgentKind? SniffKind(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name");
        if (type is not null
            && (type.StartsWith("thread.", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("turn.", StringComparison.OrdinalIgnoreCase)
                || type.StartsWith("item.", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "response_item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "raw_response_item", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "event_msg", StringComparison.OrdinalIgnoreCase)))
            return AgentKind.Codex;

        if (TryGet(root, out _, "item"))
            return AgentKind.Codex;

        if (TryGet(root, out var payload, "payload")
            && IsCodexPayload(payload))
            return AgentKind.Codex;

        if (TryGet(root, out _, "usageMetadata", "usage_metadata", "candidates", "functionCall", "function_call"))
            return AgentKind.Gemini;

        if (type is "assistant" or "user" or "result" or "tool_use" or "tool_result")
            return AgentKind.Claude;

        return null;
    }

    private static bool IsCodexPayload(JsonElement payload)
    {
        var type = FirstString(payload, "type", "event", "name", "kind");
        if (type is null)
            return false;

        return string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "agent_message", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "token_count", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "turn_complete", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "task_complete", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "exec_command_begin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "exec_command_end", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "command_execution", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("response.", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("conversation.", StringComparison.OrdinalIgnoreCase);
    }

    private static AgentKind? Canonicalize(AgentKind kind) => Canonicalize(kind.Value);

    private static AgentKind? Canonicalize(string? value)
    {
        if (value is null)
            return null;

        if (value.Equals(AgentKind.Claude.Value, StringComparison.OrdinalIgnoreCase))
            return AgentKind.Claude;
        if (value.Equals(AgentKind.Codex.Value, StringComparison.OrdinalIgnoreCase))
            return AgentKind.Codex;
        if (value.Equals(AgentKind.Gemini.Value, StringComparison.OrdinalIgnoreCase))
            return AgentKind.Gemini;

        return null;
    }

    private static bool PhaseMatches(string filePhase, string costPhase) =>
        string.Equals(filePhase, costPhase, StringComparison.OrdinalIgnoreCase)
        || filePhase.StartsWith(costPhase + "-", StringComparison.OrdinalIgnoreCase);

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
        var intervals = toolCalls
            .Where(t => t.StartedAt.HasValue && t.EndedAt.HasValue && t.EndedAt.Value >= t.StartedAt.Value)
            .Select(t => (Start: t.StartedAt!.Value, End: t.EndedAt!.Value))
            .OrderBy(t => t.Start)
            .ToList();
        if (intervals.Count == 0)
            return toolCalls.Sum(t => ToMs(t.Duration));

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

public abstract class FlexibleAgentStreamParser : IAgentStreamParserWithContext
{
    private const int InputSummaryChars = 200;
    private const int InputSummaryUtf8Bytes = 4096;
    private readonly AgentStreamParserOptions _options;

    protected FlexibleAgentStreamParser(AgentKind kind, AgentStreamParserOptions? options)
    {
        Kind = kind;
        _options = options ?? new AgentStreamParserOptions();
    }

    public AgentKind Kind { get; }

    public Task<AgentStreamSummary> ParseAsync(Stream jsonlFile, CancellationToken ct = default) =>
        ParseAsync(jsonlFile, context: null, ct);

    public async Task<AgentStreamSummary> ParseAsync(
        Stream jsonlFile,
        AgentStreamParserContext? context,
        CancellationToken ct = default)
    {
        var toolStarts = new Dictionary<string, ToolBuilder>(StringComparer.Ordinal);
        var completedTools = new List<ToolCallInvocation>();
        DateTimeOffset? firstTimestamp = null;
        DateTimeOffset? lastTimestamp = null;
        DateTimeOffset? firstAssistantTimestamp = null;
        string? lastEventType = null;
        var stalls = new List<StallEvent>();
        var eventCount = 0;
        var inputTokens = 0;
        var outputTokens = 0;
        var cachedInputTokens = 0;
        decimal? estimatedUsd = null;
        string? finalText = null;
        TimeSpan? observedTotalDuration = null;
        TimeSpan? observedTimeToFirstToken = null;

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

            eventCount++;
            var timestamp = parsed.Timestamp ?? ProjectTimestamp(context, jsonLine);

            if (timestamp is { } eventTimestamp)
            {
                firstTimestamp ??= eventTimestamp;
                if (lastTimestamp is { } previous
                    && eventTimestamp - previous > _options.StallThreshold
                    && !string.Equals(lastEventType, "result", StringComparison.OrdinalIgnoreCase)
                    && stalls.Count < _options.MaxStalls)
                {
                    stalls.Add(new StallEvent(
                        eventTimestamp,
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
            {
                if (toolStarts.ContainsKey(tool.Id)
                    || completedTools.Count + toolStarts.Count < _options.MaxToolCalls)
                {
                    toolStarts[tool.Id] = tool with { StartedAt = tool.StartedAt ?? timestamp };
                }
            }

            foreach (var result in parsed.ToolResults)
            {
                if (toolStarts.Remove(result.Id, out var started))
                {
                    var candidateEndedAt = result.EndedAt ?? timestamp;
                    var duration = result.Duration
                        ?? (started.StartedAt.HasValue && candidateEndedAt.HasValue
                            ? candidateEndedAt.Value - started.StartedAt.Value
                            : (TimeSpan?)null);
                    if (duration.HasValue && duration.Value < TimeSpan.Zero)
                        duration = null;
                    var endedAt = result.EndedAt
                        ?? (started.StartedAt.HasValue && duration.HasValue && parsed.Timestamp is null
                            ? started.StartedAt.Value + duration.Value
                            : timestamp);
                    if (endedAt is null && started.StartedAt.HasValue && duration.HasValue)
                        endedAt = started.StartedAt.Value + duration.Value;

                    completedTools.Add(new ToolCallInvocation(
                        started.Id,
                        started.Name,
                        started.InputSummary,
                        started.StartedAt,
                        endedAt,
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
            if (parsed.TotalDuration.HasValue) observedTotalDuration = parsed.TotalDuration.Value;
            if (parsed.TimeToFirstToken.HasValue) observedTimeToFirstToken = parsed.TimeToFirstToken.Value;

            if (eventCount >= _options.MaxEvents)
                break;
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

        var contextDuration = ContextDuration(context);
        var total = observedTotalDuration
            ?? (firstTimestamp.HasValue && lastTimestamp.HasValue
                ? lastTimestamp.Value - firstTimestamp.Value
                : contextDuration ?? TimeSpan.Zero);
        var ttft = observedTimeToFirstToken
            ?? (firstTimestamp.HasValue && firstAssistantTimestamp.HasValue
                ? firstAssistantTimestamp.Value - firstTimestamp.Value
                : (TimeSpan?)null);

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

    private static TimeSpan? ContextDuration(AgentStreamParserContext? context)
    {
        if (context is null || context.InvocationEndedAt < context.InvocationStartedAt)
            return null;
        return context.InvocationEndedAt - context.InvocationStartedAt;
    }

    private static DateTimeOffset? ProjectTimestamp(
        AgentStreamParserContext? context,
        AgentStreamJsonLine jsonLine)
    {
        var duration = ContextDuration(context);
        if (context is null || duration is null)
            return null;

        if (context.LineCount is > 1)
        {
            var denominator = context.LineCount.Value - 1;
            var numerator = Math.Clamp(jsonLine.LineNumber, 0, denominator);
            return context.InvocationStartedAt + Scale(duration.Value, numerator, denominator);
        }

        if (context.SizeBytes is > 0)
        {
            var numerator = Math.Clamp(jsonLine.StartOffset, 0, context.SizeBytes.Value);
            return context.InvocationStartedAt + Scale(duration.Value, numerator, context.SizeBytes.Value);
        }

        return context.InvocationStartedAt;
    }

    private static TimeSpan Scale(TimeSpan duration, long numerator, long denominator)
    {
        if (denominator <= 0 || numerator <= 0)
            return TimeSpan.Zero;
        if (numerator >= denominator)
            return duration;

        return TimeSpan.FromTicks((long)Math.Round(duration.Ticks * (double)numerator / denominator));
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
            results = results.Concat([new ToolResultBuilder(id, !Bool(root, "is_error", "error"), OutputBytes(root), timestamp, FirstDuration(root))]).ToList();
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
        var totalDuration = string.Equals(type, "turn.completed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "result", StringComparison.OrdinalIgnoreCase)
            ? FirstDuration(root)
            : null;
        var ttft = FirstDuration(root, "time_to_first_token_ms", "ttft_ms", "ttft_duration_ms");
        return new ParsedEvent(
            NormalizeType(type, starts, results, isAssistant),
            timestamp,
            isAssistant,
            starts,
            results,
            inputTokens,
            outputTokens,
            cachedInputTokens,
            cost,
            totalDuration,
            ttft,
            string.Equals(type, "result", StringComparison.OrdinalIgnoreCase) ? finalText : contentText);
    }

    protected static string NormalizeType(
        string type,
        IReadOnlyList<ToolBuilder> starts,
        IReadOnlyList<ToolResultBuilder> results,
        bool isAssistant)
    {
        if (starts.Count > 0) return "tool_use";
        if (results.Count > 0) return "tool_result";
        if (isAssistant) return "assistant";
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
                results.Add(new ToolResultBuilder(id, !Bool(item, "is_error", "error"), OutputBytes(item), null, FirstDuration(item)));
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
        cached = FirstCachedInputTokens(root);
        if (TryGet(root, out var usage, "usage", "token_usage"))
        {
            input ??= FirstInt(usage, "input_tokens", "prompt_tokens");
            output ??= FirstInt(usage, "output_tokens", "completion_tokens");
            cached ??= FirstCachedInputTokens(usage);
        }
    }

    private static int? FirstCachedInputTokens(JsonElement root) =>
        FirstInt(root, "cache_read_input_tokens", "cached_input_tokens", "cached_tokens", "cache_creation_input_tokens");

    protected static DateTimeOffset? TryTimestamp(JsonElement root, params string[] preferredNames)
    {
        foreach (var name in preferredNames)
        {
            if (TryParseTimestampProperty(root, name) is { } preferred)
                return preferred;
        }

        foreach (var name in new[]
        {
            "timestamp", "created_at", "time", "started_at", "completed_at", "ended_at",
            "timestamp_ms", "created_at_ms", "started_at_ms", "completed_at_ms", "ended_at_ms",
            "started_at_unix_ms", "completed_at_unix_ms", "ended_at_unix_ms",
            "created", "started_at_unix", "completed_at_unix", "ended_at_unix",
        })
        {
            if (TryParseTimestampProperty(root, name) is { } parsed)
                return parsed;
        }

        return null;
    }

    private static DateTimeOffset? TryParseTimestampProperty(JsonElement root, string name)
    {
        if (!TryGet(root, out var value, name))
            return null;

        if (value.ValueKind == JsonValueKind.String)
        {
            var raw = value.GetString();
            if (raw is not null && DateTimeOffset.TryParse(raw, out var dto))
                return dto;
            if (raw is not null && long.TryParse(raw, out var numeric))
                return TimestampFromNumber(name, numeric);
            return null;
        }

        return value.TryGetInt64(out var parsed) ? TimestampFromNumber(name, parsed) : null;
    }

    private static DateTimeOffset? TimestampFromNumber(string name, long value)
    {
        try
        {
            if (name.EndsWith("_ms", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Ms", StringComparison.Ordinal)
                || value > 10_000_000_000)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value);
            }

            return DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    protected static TimeSpan? FirstDuration(JsonElement el)
        => FirstDuration(el,
            "duration_ms", "elapsed_ms", "wall_time_ms", "runtime_ms",
            "duration_seconds", "elapsed_seconds", "wall_time_seconds", "runtime_seconds",
            "duration", "elapsed", "wall_time");

    protected static TimeSpan? FirstDuration(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGet(el, out var value, name))
                continue;

            if (value.TryGetDouble(out var numeric) && numeric >= 0)
            {
                return name.EndsWith("_seconds", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("Seconds", StringComparison.Ordinal)
                    ? TimeSpan.FromSeconds(numeric)
                    : TimeSpan.FromMilliseconds(numeric);
            }

            if (value.ValueKind != JsonValueKind.String)
                continue;
            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (TimeSpan.TryParse(raw, out var parsed) && parsed >= TimeSpan.Zero)
                return parsed;
            if (raw.EndsWith("ms", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(raw[..^2], out var ms)
                && ms >= 0)
                return TimeSpan.FromMilliseconds(ms);
            if (raw.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(raw[..^1], out var seconds)
                && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);
        }

        if (FirstString(el, "aggregated_output", "output") is { } output)
        {
            var marker = "Wall time:";
            var index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                var tail = output[(index + marker.Length)..].TrimStart();
                var end = tail.IndexOfAny(['\r', '\n']);
                var line = end >= 0 ? tail[..end] : tail;
                line = line.Replace("seconds", "s", StringComparison.OrdinalIgnoreCase)
                    .Replace("second", "s", StringComparison.OrdinalIgnoreCase)
                    .Trim();
                if (line.EndsWith("s", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(line[..^1].Trim(), out var seconds)
                    && seconds >= 0)
                    return TimeSpan.FromSeconds(seconds);
            }
        }

        return null;
    }

    protected static string InputSummary(JsonElement el)
    {
        JsonElement input;
        if (!TryGet(el, out input, "input", "arguments", "args"))
            input = el;
        var text = RedactInputSummary(input).ReplaceLineEndings(" ");
        return text.Length <= InputSummaryChars ? text : text[..InputSummaryChars];
    }

    private static string RedactInputSummary(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.String)
            return RedactStringInput(input.GetString() ?? "");

        using var stream = new CappedMemoryStream(InputSummaryUtf8Bytes);
        try
        {
            using var writer = new Utf8JsonWriter(stream);
            WriteRedactedJsonValue(writer, input, redactValue: false);
            writer.Flush();
        }
        catch (InputSummaryTruncatedException)
        {
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string RedactStringInput(string value)
    {
        var trimmed = value.AsSpan().TrimStart();
        if (trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '['))
        {
            try
            {
                using var doc = JsonDocument.Parse(value);
                return RedactInputSummary(doc.RootElement);
            }
            catch (JsonException)
            {
            }
        }

        return SensitiveDataRedactionEnricher.RedactText(value);
    }

    private static void WriteRedactedJsonValue(Utf8JsonWriter writer, JsonElement value, bool redactValue)
    {
        if (redactValue)
        {
            writer.WriteStringValue("***");
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedactedJsonValue(writer, property.Value, IsSensitiveInputKey(property.Name));
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteRedactedJsonValue(writer, item, redactValue: false);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(RedactStringInput(value.GetString() ?? ""));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitiveInputKey(string key) =>
        SensitiveKeyFragments.Any(f => key.Contains(f, StringComparison.OrdinalIgnoreCase))
        || SensitiveKeyFragments.Any(f => NormalizeKey(key).Contains(NormalizeKey(f), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeKey(string key)
    {
        Span<char> buffer = key.Length <= 256 ? stackalloc char[key.Length] : new char[key.Length];
        var written = 0;
        foreach (var ch in key)
        {
            if (char.IsLetterOrDigit(ch))
                buffer[written++] = char.ToLowerInvariant(ch);
        }

        return new string(buffer[..written]);
    }

    private static readonly HashSet<string> SensitiveKeyFragments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Token", "Secret", "Password", "Authorization", "ApiKey", "AuthJson", "Credential",
    };

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
        TimeSpan? TotalDuration,
        TimeSpan? TimeToFirstToken,
        string? FinalText);

    protected sealed record ToolBuilder(string Id, string Name, string InputSummary, DateTimeOffset? StartedAt);
    protected sealed record ToolResultBuilder(
        string Id,
        bool? Succeeded,
        int OutputBytes,
        DateTimeOffset? EndedAt,
        TimeSpan? Duration);

    private sealed class InputSummaryTruncatedException : Exception
    {
    }

    private sealed class CappedMemoryStream : MemoryStream
    {
        private readonly int _maxBytes;

        public CappedMemoryStream(int maxBytes)
            : base(Math.Max(1, maxBytes))
        {
            _maxBytes = Math.Max(1, maxBytes);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var remaining = _maxBytes - (int)Length;
            if (remaining <= 0)
                throw new InputSummaryTruncatedException();

            var allowed = Math.Min(count, remaining);
            base.Write(buffer, offset, allowed);
            if (allowed < count)
                throw new InputSummaryTruncatedException();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var remaining = _maxBytes - (int)Length;
            if (remaining <= 0)
                throw new InputSummaryTruncatedException();

            var allowed = Math.Min(buffer.Length, remaining);
            var chunk = buffer[..allowed].ToArray();
            base.Write(chunk, 0, chunk.Length);
            if (allowed < buffer.Length)
                throw new InputSummaryTruncatedException();
        }
    }
}

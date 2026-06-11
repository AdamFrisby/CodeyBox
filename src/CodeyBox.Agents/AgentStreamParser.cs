using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using CodeyBox.Core;

namespace CodeyBox.Agents;

public interface IAgentStreamParser
{
    AgentKind Kind { get; }
    Task<AgentStreamSummary> ParseAsync(Stream jsonlFile, CancellationToken ct = default);

    /// <summary>
    /// Returns true when the parser recognises <paramref name="line"/> as one of
    /// its own provider-specific NDJSON event shapes. Used by the orchestrator's
    /// stream-kind sniffer to pick a parser without itself knowing the per-provider
    /// JSON vocabulary. Default returns false so unknown / catch-all parsers do not
    /// claim arbitrary lines.
    /// </summary>
    bool TryClaim(System.Text.Json.JsonElement line) => false;

    /// <summary>
    /// Returns true when this parser's agent is known to emit the on-wire NDJSON
    /// shape of <paramref name="sniffed"/>. Wrapper agents (cursor proxies
    /// claude; antigravity proxies claude/gemini) override this so the
    /// orchestrator can attribute a sniffed-by-shape stream to the dispatched
    /// agent kind without itself encoding the provider compatibility matrix.
    /// Default: a parser only claims its own kind.
    /// </summary>
    bool CanEmitShapeOf(AgentKind sniffed) =>
        string.Equals(sniffed.Value, Kind.Value, StringComparison.OrdinalIgnoreCase);
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
    string? FinalAssistantMessage)
{
    public bool IsUnsupported { get; init; }

    public static AgentStreamSummary Unsupported() =>
        new(TimeSpan.Zero, null, 0, 0, 0, null, [], [], null) { IsUnsupported = true };
}

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

public sealed class AgentStreamParserOptions
{
    public TimeSpan StallThreshold { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxLineBytes { get; set; } = 64 * 1024 * 1024;
    public int MaxJsonDepth { get; set; } = 64;
    public int MaxEvents { get; set; } = 50_000;
    public int MaxToolCalls { get; set; } = 2_000;
    public int MaxStalls { get; set; } = 2_000;
}

public sealed class UnknownAgentStreamParser : IAgentStreamParserWithContext
{
    public AgentKind Kind { get; } = new("unknown");

    public Task<AgentStreamSummary> ParseAsync(System.IO.Stream jsonlFile, CancellationToken ct = default) =>
        ParseAsync(jsonlFile, context: null, ct);

    public async Task<AgentStreamSummary> ParseAsync(
        System.IO.Stream jsonlFile,
        AgentStreamParserContext? context,
        CancellationToken ct = default)
    {
        var duration = context is not null && context.InvocationEndedAt >= context.InvocationStartedAt
            ? context.InvocationEndedAt - context.InvocationStartedAt
            : TimeSpan.Zero;

        long lineCount = 0;
        long byteCount = 0;
        var errorLineCount = 0;
        var lastLines = new System.Collections.Generic.List<string>();

        try
        {
            using var reader = new System.IO.StreamReader(jsonlFile, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            string? line;
            while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            {
                lineCount++;
                byteCount += Encoding.UTF8.GetByteCount(line) + 1;

                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (LooksLikeError(line))
                        errorLineCount++;
                    lastLines.Add(line);
                    if (lastLines.Count > 10)
                        lastLines.RemoveAt(0);
                }
            }
        }
        catch (IOException)
        {
            // Narrow, explicitly-recoverable read failure: an in-flight capture
            // file may have been truncated, rotated, or briefly contended.
            // Persist the partial tail we already collected so the
            // observability path is never blind — but do NOT swallow
            // arbitrary exceptions (OperationCanceledException flows up,
            // UnauthorizedAccessException / OutOfMemoryException / etc. must
            // surface and let the sweep fail/retry rather than silently
            // producing a "successful" summary from a real bug.
        }
        catch (System.Text.DecoderFallbackException)
        {
            // Malformed UTF-8 in a plaintext agent stream — agy / opencode may
            // emit terminal escapes or partial bytes when killed mid-write.
            // Same recovery shape as the IO branch above.
        }

        // Prefer the caller-supplied accounting (which sees the full on-disk
        // file size) over what we managed to read before any swallowed error.
        var finalLineCount = context?.LineCount ?? lineCount;
        var finalByteCount = context?.SizeBytes ?? byteCount;
        var summary = BuildSummary(finalLineCount, finalByteCount, errorLineCount, lastLines);

        return new AgentStreamSummary(
            TotalDuration: duration,
            TimeToFirstToken: null,
            InputTokens: 0,
            OutputTokens: 0,
            CachedInputTokens: 0,
            EstimatedUsd: null,
            ToolCalls: Array.Empty<ToolCallInvocation>(),
            Stalls: Array.Empty<StallEvent>(),
            FinalAssistantMessage: summary)
        {
            IsUnsupported = false
        };
    }

    private static bool LooksLikeError(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        return line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)
            || line.Contains("panic", StringComparison.OrdinalIgnoreCase)
            || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
            || line.Contains("traceback", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSummary(long lineCount, long byteCount, int errorLineCount, IReadOnlyList<string> tail)
    {
        var header = $"[plaintext-fallback lines={lineCount} bytes={byteCount} errors={errorLineCount}]";
        if (tail.Count == 0) return header;
        return header + "\n" + string.Join("\n", tail);
    }
}

public sealed record AgentStreamJsonLine(string Text, long LineNumber, long StartOffset, long EndOffset);

public static class AgentStreamJsonLineReader
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

    /// <summary>
    /// Provider-specific NDJSON-event-shape recognition. Concrete provider
    /// parsers override this so the orchestrator's sniffer can dispatch by
    /// asking "does any registered parser claim this line?" without needing
    /// to know each provider's JSON vocabulary.
    /// </summary>
    public virtual bool TryClaim(JsonElement line) => false;

    /// <summary>
    /// Provider-specific shape compatibility. Wrapper agents
    /// (cursor proxies claude; antigravity proxies claude/gemini) override
    /// this so the orchestrator's resolver does not encode the cross-provider
    /// compatibility matrix. Default: only the parser's own kind.
    /// </summary>
    public virtual bool CanEmitShapeOf(AgentKind sniffed) =>
        string.Equals(sniffed.Value, Kind.Value, StringComparison.OrdinalIgnoreCase);

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
        var recognizedEventCount = 0;
        var projectedTimestampCount = 0;

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
            if (parsed.IsRecognized)
                recognizedEventCount++;
            var timestamp = parsed.Timestamp ?? ProjectTimestamp(context, jsonLine);
            if (parsed.Timestamp is null && timestamp.HasValue)
                projectedTimestampCount++;

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

        if (recognizedEventCount == 0)
            return AgentStreamSummary.Unsupported();

        var total = observedTotalDuration
            ?? (projectedTimestampCount > 0 && context is not null && context.InvocationEndedAt >= context.InvocationStartedAt
                ? context.InvocationEndedAt - context.InvocationStartedAt
                : firstTimestamp.HasValue && lastTimestamp.HasValue
                ? lastTimestamp.Value - firstTimestamp.Value
                : TimeSpan.Zero);
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

    private static DateTimeOffset? ProjectTimestamp(AgentStreamParserContext? context, AgentStreamJsonLine line)
    {
        if (context is null || context.InvocationEndedAt < context.InvocationStartedAt)
            return null;

        var duration = context.InvocationEndedAt - context.InvocationStartedAt;
        if (duration == TimeSpan.Zero)
            return context.InvocationStartedAt;

        double ratio;
        if (context.LineCount is > 1)
        {
            ratio = line.LineNumber / (double)(context.LineCount.Value - 1);
        }
        else if (context.SizeBytes is > 0)
        {
            ratio = line.StartOffset / (double)context.SizeBytes.Value;
        }
        else
        {
            return null;
        }

        ratio = Math.Clamp(ratio, 0d, 1d);
        return context.InvocationStartedAt + TimeSpan.FromTicks((long)Math.Round(duration.Ticks * ratio));
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
        var recognized = IsRecognizedStreamEvent(
            type,
            starts,
            results,
            isAssistant,
            inputTokens,
            outputTokens,
            cachedInputTokens,
            cost,
            totalDuration,
            ttft);
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
            string.Equals(type, "result", StringComparison.OrdinalIgnoreCase) ? finalText : contentText,
            recognized);
    }

    private static bool IsRecognizedStreamEvent(
        string type,
        IReadOnlyList<ToolBuilder> starts,
        IReadOnlyList<ToolResultBuilder> results,
        bool isAssistant,
        int? inputTokens,
        int? outputTokens,
        int? cachedInputTokens,
        decimal? cost,
        TimeSpan? totalDuration,
        TimeSpan? ttft)
    {
        if (starts.Count > 0
            || results.Count > 0
            || isAssistant
            || inputTokens.HasValue
            || outputTokens.HasValue
            || cachedInputTokens.HasValue
            || cost.HasValue
            || totalDuration.HasValue
            || ttft.HasValue)
        {
            return true;
        }

        return KnownStreamEventTypes.Contains(type);
    }

    private static readonly HashSet<string> KnownStreamEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "system",
        "assistant",
        "user",
        "result",
        "tool_use",
        "tool_result",
        "function_call",
        "function_call_output",
        "response",
        "response_item",
        "raw_response_item",
        "event_msg",
        "thread.started",
        "thread.completed",
        "turn.started",
        "turn.completed",
        "turn.failed",
        "turn_complete",
        "task_complete",
        "item.started",
        "item.completed",
        "message",
        "agent_message",
        "token_count",
        "command_execution",
        "execution",
        "exec_command_begin",
        "exec_command_end",
        "mcp_tool_call_begin",
        "mcp_tool_call_end",
    };

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
        string? FinalText,
        bool IsRecognized);

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

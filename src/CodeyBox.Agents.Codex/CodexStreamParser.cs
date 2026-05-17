using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Codex;

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

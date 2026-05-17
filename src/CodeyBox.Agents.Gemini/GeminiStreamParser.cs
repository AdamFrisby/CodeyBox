using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Gemini;

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

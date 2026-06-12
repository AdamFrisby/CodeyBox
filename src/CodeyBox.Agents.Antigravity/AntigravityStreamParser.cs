using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Antigravity;

public sealed class AntigravityStreamParser : FlexibleAgentStreamParser
{
    public AntigravityStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Antigravity, options)
    {
    }

    /// <summary>
    /// Antigravity (agy) emits NDJSON events shape-compatible with the
    /// gateway model selected — claude-* gateway models produce literal
    /// Anthropic stream-json, and gemini-* gateway models produce Gemini
    /// stream-json. No antigravity-only marker exists in the on-wire shape
    /// we can use to distinguish a real Claude run from an agy-via-claude
    /// run, so this parser deliberately does not claim by shape. The
    /// authoritative "this run was dispatched as agy" signal lives on the
    /// cost row and the work item's <see cref="WorkItem.Agent"/>;
    /// <see cref="AgentStreamParserSelection.ResolveKind"/> uses those for
    /// kind attribution. Claiming shared shapes here would mis-attribute
    /// real Claude streams as agent_kind=antigravity.
    /// </summary>
    public override bool TryClaim(JsonElement line) => false;

    /// <summary>
    /// Antigravity is a multi-model gateway: claude-* gateway models emit
    /// literal Anthropic stream-json and gemini-* gateway models emit literal
    /// Gemini stream-json. A Claude- or Gemini-sniffed stream may therefore
    /// have been produced by a dispatched agy run; declare both shapes here
    /// so <see cref="AgentStreamParserSelection.ResolveKind"/> can attribute
    /// such streams to antigravity when the work item / cost row says so.
    /// </summary>
    public override bool CanEmitShapeOf(AgentKind sniffed) =>
        string.Equals(sniffed.Value, AgentKind.Antigravity.Value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sniffed.Value, AgentKind.Claude.Value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sniffed.Value, AgentKind.Gemini.Value, StringComparison.OrdinalIgnoreCase);

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name") ?? "unknown";
        if (string.Equals(type, "codeybox.stderr", StringComparison.OrdinalIgnoreCase))
            return base.ParseEvent(root);

        var timestamp = TryTimestamp(root);
        var starts = new List<ToolBuilder>();
        var results = new List<ToolResultBuilder>();
        var isAssistant = string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(FirstString(root, "role"), "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(FirstString(root, "role"), "model", StringComparison.OrdinalIgnoreCase);

        string? finalText = null;
        if (IsGeminiPayload(root))
        {
            ParseGeminiPayload(root, starts, results, ref finalText, ref isAssistant, timestamp);
        }
        else if (TryGet(root, out var message, "message"))
        {
            isAssistant |= string.Equals(FirstString(message, "role"), "assistant", StringComparison.OrdinalIgnoreCase);
            ParseContent(message, starts, results, ref finalText);
        }
        else
        {
            ParseContent(root, starts, results, ref finalText);
        }

        int? input = null;
        int? output = null;
        int? cached = null;

        if (TryGet(root, out var usage, "usage"))
        {
            var freshInput = ReadInt(usage, "input_tokens");
            var cacheCreation = ReadInt(usage, "cache_creation_input_tokens");
            var cacheRead = ReadInt(usage, "cache_read_input_tokens");

            if (freshInput == 0 && cacheCreation == 0 && cacheRead == 0)
            {
                var promptTotal = ReadInt(usage, "prompt_tokens", "promptTokenCount");
                cacheRead = ReadInt(usage, "cached_input_tokens", "cachedInputTokenCount");
                freshInput = TokenUsageAccounting.FreshInputTokens(promptTotal, cacheRead);
            }

            input = freshInput + cacheCreation;
            output = ReadInt(usage, "output_tokens", "candidatesTokenCount", "completion_tokens");
            cached = cacheRead;
        }

        if (TryGet(root, out var usageMetadata, "usageMetadata", "usage_metadata"))
        {
            var promptTotal = ReadInt(usageMetadata, "promptTokenCount", "prompt_token_count", "input_tokens", "prompt_tokens");
            var cacheRead = ReadInt(usageMetadata, "cachedContentTokenCount", "cached_content_token_count", "cached_input_tokens");
            input = TokenUsageAccounting.FreshInputTokens(promptTotal, cacheRead);
            output = ReadInt(usageMetadata, "candidatesTokenCount", "candidates_token_count", "output_tokens", "completion_tokens");
            cached = cacheRead;
        }

        var parsed = ParseScalars(root, type, timestamp, starts, results, isAssistant, finalText);
        return parsed with
        {
            InputTokens = input ?? parsed.InputTokens,
            OutputTokens = output ?? parsed.OutputTokens,
            CachedInputTokens = cached ?? parsed.CachedInputTokens,
        };
    }

    private static bool IsGeminiPayload(JsonElement root) =>
        TryGet(root, out _, "usageMetadata", "usage_metadata", "candidates", "functionCall", "function_call", "toolCall", "tool_call");

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

    private static int ReadInt(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.TryGetProperty(name, out var prop) && prop.TryGetInt32(out var v))
                return v;
        }
        return 0;
    }
}

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
    /// Google Antigravity CLI emits Claude-shaped or Gemini-shaped NDJSON events depending on
    /// the gateway model (Gemini-backed vs. Claude-backed gateway model).
    /// </summary>
    public override bool TryClaim(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;

        // Both shapes carry a top-level "type" string
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (type is not ("assistant" or "user" or "result" or "tool_use" or "tool_result"))
            return false;

        // Antigravity-specific indicators: presence of "model" at the top level or inside "message"
        if (line.TryGetProperty("model", out _))
            return true;

        if (line.TryGetProperty("message", out var msg)
            && msg.ValueKind == JsonValueKind.Object
            && msg.TryGetProperty("model", out _))
            return true;

        // Or specific usage fields
        if (line.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("cache_creation_input_tokens", out _)
                || usage.TryGetProperty("cache_read_input_tokens", out _)
                || usage.TryGetProperty("cached_input_tokens", out _))
                return true;
        }

        return false;
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name") ?? "unknown";
        var timestamp = TryTimestamp(root);
        var starts = new List<ToolBuilder>();
        var results = new List<ToolResultBuilder>();
        var isAssistant = string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase)
            || string.Equals(FirstString(root, "role"), "assistant", StringComparison.OrdinalIgnoreCase);

        // Parse content using base ParseContent, checking if message property is present
        string? finalText = null;
        if (TryGet(root, out var message, "message"))
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

        if (type == "result" && root.TryGetProperty("usage", out var usage))
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

        var parsed = ParseScalars(root, type, timestamp, starts, results, isAssistant, finalText);
        return parsed with
        {
            InputTokens = input ?? parsed.InputTokens,
            OutputTokens = output ?? parsed.OutputTokens,
            CachedInputTokens = cached ?? parsed.CachedInputTokens,
        };
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

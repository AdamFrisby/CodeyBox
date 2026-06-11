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

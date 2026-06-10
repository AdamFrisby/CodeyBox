using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Cursor;

public sealed class CursorStreamParser : FlexibleAgentStreamParser
{
    public CursorStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Cursor, options)
    {
    }

    /// <summary>
    /// Cursor CLI emits NDJSON in the same system/user/assistant/result shape as Claude
    /// when --output-format stream-json --stream-partial-output are set.
    /// </summary>
    public override bool TryClaim(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;
        var type = typeProp.GetString();
        return type is "assistant" or "user" or "result" or "tool_use" or "tool_result";
    }
}

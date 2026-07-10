using System.Text.Json;

namespace CodeyBox.Agents;

/// <summary>
/// Provider-owned NDJSON event recognizers used by stream parsers and by
/// functional probes that must validate a candidate stream before capture is
/// enabled. Keeping the vocabulary here prevents runners and orchestrator code
/// from growing their own divergent provider-shape lists.
/// </summary>
public static class AgentStreamEventShapes
{
    public static bool IsClaudeStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        return type is "assistant" or "user" or "result" or "tool_use" or "tool_result";
    }

    public static bool IsGeminiStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        return line.TryGetProperty("usageMetadata", out _)
            || line.TryGetProperty("usage_metadata", out _)
            || line.TryGetProperty("candidates", out _)
            || line.TryGetProperty("functionCall", out _)
            || line.TryGetProperty("function_call", out _);
    }
}

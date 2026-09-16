using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Vibe;

/// <summary>
/// Stream parser for vibe's <c>--output streaming</c> lines (verified against
/// vibe 2.25.4 live frames).
///
/// <para><b>Claim vocabulary.</b> Each line is one programmatic history entry:
/// <c>{"id":…,"sessionId":…,"turnId":…,"createdAt":…,"updatedAt":…,
/// "generationStatus":"completed","type":"message"|"effect"|"reasoning"|"callback",
/// "role":…,"content":[{…}],…}</c>. Assistant turns surface as
/// <c>type: "message"</c> with a top-level <c>content</c> array of
/// <c>{type: "text", text: …}</c> blocks; tool calls surface as
/// <c>type: "effect"</c> with <c>detail.toolName</c> (e.g.
/// <c>write_file</c>). The claim requires the vibe frame shape, not just the
/// <c>type</c> word: the line must carry string <c>sessionId</c> +
/// <c>turnId</c> alongside a known entry type. No other registered parser
/// claims these shapes (Claude claims
/// <c>assistant/user/result/tool_use/tool_result</c>; Codex claims dotted
/// <c>thread./turn./item.</c> prefixes; Gemini keys on
/// <c>usageMetadata</c>; Goose requires a nested <c>message</c> object with
/// <c>role</c> + array <c>content</c> — vibe message frames carry
/// <c>content</c> top-level with no nested <c>message</c> envelope; Pi claims
/// underscore lifecycle verbs). A foreign <c>{"type":"message"}</c> line
/// without the session envelope is never stolen.</para>
///
/// <para><b>Usage mapping.</b> Programmatic history entries carry no token
/// counts (verified: success, tool-use, and provider-failure runs alike emit
/// no usage object anywhere in <c>--output streaming</c>; totals live only
/// in the in-process <c>AgentStatsSnapshot</c>, which programmatic mode never
/// prints). There is therefore nothing to supplement the base usage parse
/// with — cost attribution reports unknown (see
/// <see cref="VibeCostExtractor"/>).</para>
/// </summary>
public sealed class VibeStreamParser : FlexibleAgentStreamParser
{
    public VibeStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Vibe, options)
    {
    }

    private static readonly HashSet<string> VibeEntryTypes = new(StringComparer.Ordinal)
    {
        "message", "effect", "reasoning", "callback",
    };

    public override bool TryClaim(JsonElement line)
        => IsVibeStreamJsonEvent(line);

    internal static bool IsVibeStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (string.IsNullOrEmpty(type) || !VibeEntryTypes.Contains(type))
            return false;

        // Require the programmatic history-entry envelope: every live frame
        // carries string sessionId + turnId. A foreign {"type":"message"}
        // line without them is never misattributed.
        if (!line.TryGetProperty("sessionId", out var sessionId)
            || sessionId.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(sessionId.GetString()))
            return false;
        if (!line.TryGetProperty("turnId", out var turnId)
            || turnId.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(turnId.GetString()))
            return false;

        return true;
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        // Timestamps ride on camelCase createdAt/updatedAt (epoch millis);
        // the base only knows snake_case names, so prefer vibe's explicitly.
        var timestamp = TryTimestamp(root, "createdAt", "updatedAt");

        var parsed = base.ParseEvent(root);

        // Tool calls surface as effect frames (detail.toolName, e.g.
        // write_file), which the base content walk does not map to tool
        // starts. Fold them in so tool-call telemetry sees vibe runs.
        if (parsed.ToolStarts.Count == 0
            && root.TryGetProperty("type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String
            && string.Equals(typeProp.GetString(), "effect", StringComparison.Ordinal)
            && ExtractEffectTool(root) is { } tool)
        {
            parsed = parsed with { ToolStarts = [tool] };
        }

        if (timestamp.HasValue && !parsed.Timestamp.HasValue)
            parsed = parsed with { Timestamp = timestamp };

        return parsed;
    }

    private static ToolBuilder? ExtractEffectTool(JsonElement root)
    {
        string? name = null;
        if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object)
            name = FirstString(detail, "toolName", "tool_name", "name");
        name ??= FirstString(root, "title", "name");
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var id = FirstString(root, "id") ?? Guid.NewGuid().ToString("N");
        return new ToolBuilder(id, name!, InputSummary(root), TryTimestamp(root, "createdAt", "updatedAt"));
    }
}

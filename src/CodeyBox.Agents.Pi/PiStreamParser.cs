using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Pi;

/// <summary>
/// Stream parser for pi's <c>--mode json</c> event lines.
///
/// <para><b>Claim vocabulary (verified against pi 0.85.1 live frames).</b> The
/// session header <c>{"type":"session","version":3,"id":…,"cwd":…}</c> and the
/// underscore-style lifecycle verbs (<c>agent_start</c>, <c>turn_start</c>,
/// <c>message_start</c>, <c>message_update</c>, <c>message_end</c>,
/// <c>turn_end</c>, <c>agent_end</c>, <c>agent_settled</c>) are pi-specific:
/// no other registered parser claims them (Claude claims
/// <c>assistant/user/result/tool_use/tool_result</c>; Codex claims dotted
/// <c>thread./turn./item.</c> prefixes; Gemini keys on
/// <c>usageMetadata</c>). The claim additionally requires the header's numeric
/// <c>version</c> + string <c>cwd</c> on <c>type: "session"</c> so a foreign
/// line that merely reuses the word "session" is never stolen.</para>
///
/// <para><b>Usage mapping.</b> The shared <c>ParseUsage</c> only knows
/// <c>input_tokens/prompt_tokens</c>-style names; pi reports
/// <c>usage:{input, output, cacheRead, cacheWrite, totalTokens}</c>, so this
/// parser supplements the base parse with pi's names (base results win when
/// both are present). <c>cacheWrite</c> has no cost-bucket in
/// <see cref="AgentCostSnapshot"/> and is ignored for token accounting —
/// same treatment as the other providers.</para>
/// </summary>
public sealed class PiStreamParser : FlexibleAgentStreamParser
{
    public PiStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Pi, options)
    {
    }

    private static readonly HashSet<string> PiLifecycleTypes = new(StringComparer.Ordinal)
    {
        "session",
        "agent_start",
        "agent_end",
        "agent_settled",
        "turn_start",
        "turn_end",
        "message_start",
        "message_update",
        "message_end",
        "queue_update",
        "compaction_start",
        "compaction_end",
    };

    public override bool TryClaim(JsonElement line)
        => IsPiStreamJsonEvent(line);

    internal static bool IsPiStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (string.IsNullOrEmpty(type) || !PiLifecycleTypes.Contains(type))
            return false;

        // The bare lifecycle verbs are already pi-unique, but the session
        // header gets a shape check too: require the numeric version and cwd
        // the live header carries, so a foreign {"type":"session"} line is
        // never misattributed.
        if (string.Equals(type, "session", StringComparison.Ordinal))
        {
            var hasVersion = line.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.Number;
            var hasCwd = line.TryGetProperty("cwd", out var cwd)
                && cwd.ValueKind == JsonValueKind.String;
            return hasVersion && hasCwd;
        }

        return true;
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var parsed = base.ParseEvent(root);

        // Supplement (never override) the base usage parse with pi's field
        // names. The usage object rides on the nested assistant message;
        // top-level turn/agent frames repeat it via "message".
        var message = root;
        if (TryGet(root, out var nested, "message") && nested.ValueKind == JsonValueKind.Object)
            message = nested;

        if (TryGet(message, out var usage, "usage") && usage.ValueKind == JsonValueKind.Object)
        {
            var input = parsed.InputTokens ?? FirstNullableInt(usage, "input");
            var output = parsed.OutputTokens ?? FirstNullableInt(usage, "output");
            var cached = parsed.CachedInputTokens ?? FirstNullableInt(usage, "cacheRead");
            if (input != parsed.InputTokens || output != parsed.OutputTokens || cached != parsed.CachedInputTokens)
                parsed = parsed with { InputTokens = input, OutputTokens = output, CachedInputTokens = cached };
        }

        return parsed;
    }

    private static int? FirstNullableInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) && n >= 0)
            return n;
        return null;
    }
}

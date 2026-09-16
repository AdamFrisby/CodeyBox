using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Goose;

/// <summary>
/// Stream parser for goose's <c>--output-format stream-json</c> lines
/// (verified against goose 1.50.1 live frames).
///
/// <para><b>Claim vocabulary.</b> Each line is
/// <c>{"type":"message","message":{"role":…,"content":[{…}],"metadata":…}}</c>
/// for per-chunk assistant/user traffic, terminated by a single
/// <c>{"type":"complete","total_tokens":…,"input_tokens":…,
/// "output_tokens":…,"cache_read_input_tokens":…,
/// "cache_write_input_tokens":…,"cost_usd":…}</c> totals frame. The claim
/// requires the goose frame shape, not just the <c>type</c> word: message
/// frames must carry a nested <c>message</c> object with a string
/// <c>role</c> and an array <c>content</c>; complete frames must carry a
/// numeric <c>total_tokens</c>. No other registered parser claims these
/// shapes (Claude claims <c>assistant/user/result/tool_use/tool_result</c>;
/// Codex claims dotted <c>thread./turn./item.</c> prefixes; Gemini keys on
/// <c>usageMetadata</c>; Pi claims underscore lifecycle verbs). The human
/// session banner (<c>__( O)&gt; …</c>) is not JSON and is never claimed —
/// the base line splitter skips non-JSON lines before parsers run.</para>
///
/// <para><b>Usage mapping.</b> The shared <c>ParseUsage</c> only knows
/// <c>input_tokens/prompt_tokens</c>-style names; goose reports
/// snake_case totals (<c>input_tokens</c> on the complete frame, but nested
/// per-message usage is absent — chunks carry no counts), so this parser
/// supplements the base parse with goose's names on the complete frame
/// (base results win when both are present). <c>cache_write_input_tokens</c>
/// has no cost-bucket in <see cref="AgentCostSnapshot"/> and is ignored for
/// token accounting — same treatment as the other providers.</para>
/// </summary>
public sealed class GooseStreamParser : FlexibleAgentStreamParser
{
    public GooseStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Goose, options)
    {
    }

    public override bool TryClaim(JsonElement line)
        => IsGooseStreamJsonEvent(line);

    internal static bool IsGooseStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (string.Equals(type, "message", StringComparison.Ordinal))
        {
            // Require the nested assistant/user envelope: a foreign
            // {"type":"message"} line without it is never stolen.
            if (!line.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object)
                return false;
            if (!message.TryGetProperty("role", out var role)
                || role.ValueKind != JsonValueKind.String)
                return false;
            if (!message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
                return false;
            return true;
        }

        if (string.Equals(type, "complete", StringComparison.Ordinal))
        {
            // The terminal totals frame always carries numeric total_tokens.
            return line.TryGetProperty("total_tokens", out var total)
                && total.ValueKind == JsonValueKind.Number;
        }

        return false;
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var parsed = base.ParseEvent(root);

        // Supplement (never override) the base usage parse with goose's
        // complete-frame totals. Message chunks carry no counts; the single
        // terminal frame is the run total.
        if (root.TryGetProperty("type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String
            && string.Equals(typeProp.GetString(), "complete", StringComparison.Ordinal))
        {
            var input = parsed.InputTokens ?? FirstNonNegativeInt(root, "input_tokens");
            var output = parsed.OutputTokens ?? FirstNonNegativeInt(root, "output_tokens");
            var cached = parsed.CachedInputTokens ?? FirstNonNegativeInt(root, "cache_read_input_tokens");
            if (input != parsed.InputTokens || output != parsed.OutputTokens || cached != parsed.CachedInputTokens)
                parsed = parsed with { InputTokens = input, OutputTokens = output, CachedInputTokens = cached };
        }

        return parsed;
    }

    private static int? FirstNonNegativeInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) && n >= 0)
            return n;
        return null;
    }
}

using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Qwen;

/// <summary>
/// Stream parser for qwen's <c>--output-format stream-json</c> event lines.
///
/// <para><b>Claim policy (verified against qwen 0.24.0 live frames).</b>
/// Qwen's wire vocabulary is Claude-shaped (<c>system</c> /
/// <c>assistant</c> / <c>result</c> with <c>session_id</c>, <c>uuid</c>,
/// <c>duration_ms</c>, <c>num_turns</c>), so bare type matching would steal
/// real Claude streams depending on registration order. Every claim rule
/// therefore requires a qwen-only marker on top of the type:</para>
/// <list type="bullet">
/// <item><description><c>system</c> — requires <c>qwen_code_version</c> or
/// snake-case <c>permission_mode</c> (Claude's init frame has neither; it
/// uses camel-case <c>permissionMode</c>).</description></item>
/// <item><description><c>assistant</c> — requires a nested
/// <c>message.usage.total_tokens</c> counter (Claude's usage object never
/// carries <c>total_tokens</c>). Zero-usage thinking frames omit it and go
/// unclaimed; sniffing resolves on the leading <c>system/init</c> line, and
/// <c>ParseAsync</c> parses unclaimed lines normally.</description></item>
/// <item><description><c>result</c> — requires the <c>stats</c> object, the
/// <c>permission_denials</c> array, or
/// <c>subtype: "error_during_execution"</c> (none of which Claude emits).
/// The shared <c>duration_ms</c> / <c>num_turns</c> / <c>is_error</c> fields
/// are deliberately NOT discriminators.</description></item>
/// <item><description><c>stream_event</c> — qwen's partial/goal envelope;
/// no other registered CLI emits this type, so it is claimed bare.</description></item>
/// </list>
///
/// <para>Usage accounting needs no supplement: the base parser already reads
/// qwen's field names (<c>input_tokens</c> / <c>output_tokens</c> /
/// <c>cache_read_input_tokens</c>) at the top level, under <c>usage</c>,
/// and under the <c>message</c> envelope; the result frame's <c>result</c>
/// string becomes the final text; <c>duration_ms</c> feeds the observed
/// duration. Qwen emits no tool_use/tool_result envelope in this mode, so
/// the tool-call list stays empty rather than fabricated.</para>
/// </summary>
public sealed class QwenStreamParser : FlexibleAgentStreamParser
{
    public QwenStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Qwen, options)
    {
    }

    public override bool TryClaim(JsonElement line) =>
        IsQwenStreamJsonEvent(line);

    internal static bool IsQwenStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (string.Equals(type, "stream_event", StringComparison.Ordinal))
            return true;

        if (string.Equals(type, "system", StringComparison.Ordinal))
        {
            // qwen_code_version is the rock-solid marker; permission_mode
            // (snake_case) is the backup — Claude uses permissionMode.
            return HasProperty(line, "qwen_code_version")
                || HasProperty(line, "permission_mode");
        }

        if (string.Equals(type, "assistant", StringComparison.Ordinal))
        {
            // message.usage.total_tokens is qwen-only: Claude's usage
            // object never carries total_tokens.
            return line.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("total_tokens", out var total)
                && total.ValueKind == JsonValueKind.Number;
        }

        if (string.Equals(type, "result", StringComparison.Ordinal))
        {
            if (HasProperty(line, "stats") || HasProperty(line, "permission_denials"))
                return true;
            return line.TryGetProperty("subtype", out var subtype)
                && subtype.ValueKind == JsonValueKind.String
                && string.Equals(subtype.GetString(), "error_during_execution", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool HasProperty(JsonElement line, string name) =>
        line.TryGetProperty(name, out _);
}

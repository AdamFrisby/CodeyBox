using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Autohand;

/// <summary>
/// Parser for autohand's <c>--output-format stream-json</c> NDJSON events
/// (verified against autohand-cli 0.9.7 bare headless runs).
///
/// <para>Observed frame vocabulary (bare mode emits no <c>init</c>/
/// <c>message</c> frames at all — only these):</para>
/// <list type="bullet">
/// <item><c>{"type":"tool_start","toolId":…,"toolName":…,"toolArgs":{…}}</c> —
/// mapped to a tool start.</item>
/// <item><c>{"type":"tool_end","toolId":…,"toolName":…,"toolSuccess":bool,…}</c> —
/// mapped to a tool result.</item>
/// <item><c>{"type":"file_modified","filePath":…,"changeType":…}</c> —
/// file-change signal (no token/tool semantics; recognised so the summary
/// counts the line as understood).</item>
/// <item><c>{"type":"result","content":"…"}</c> — final assistant text
/// (the <c>content</c> string, not a <c>result</c> field).</item>
/// <item><c>{"type":"error","message":"…"}</c> — terminal failure; parsed for
/// the summary but deliberately NOT claimed by <see cref="TryClaim"/> (see
/// below).</item>
/// </list>
///
/// <para><b>Claim discipline.</b> <c>type: "result"</c> overlaps Claude (and
/// Claude-shaped wrappers), which the sniffer resolves first-claim-wins in
/// registration order — so the claim requires a string <c>content</c> AND the
/// absence of <c>subtype</c> (Claude's result frames always carry one). Bare
/// <c>type: "error"</c> with a string <c>message</c> has no
/// autohand-unique marker, so it is never claimed: misattributing another
/// agent's error line as autohand would corrupt stream-file attribution,
/// while the runner's own failure path lifts the message through
/// <see cref="AutohandTerminalDiagnoser"/> independently of sniffing. Real
/// autohand streams open with <c>tool_start</c>/<c>tool_end</c>/
/// <c>file_modified</c> lines, which ARE distinctive, so an error-only
/// capture sniffing as unknown is the honest outcome.</para>
/// </summary>
public sealed class AutohandStreamParser : FlexibleAgentStreamParser
{
    public AutohandStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Autohand, options)
    {
    }

    public override bool TryClaim(JsonElement line) =>
        IsAutohandStreamJsonEvent(line);

    internal static bool IsAutohandStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object) return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        switch (type)
        {
            case "tool_start":
                // toolId + toolName strings; no other registered CLI emits
                // this pair (verified against the parser set).
                return HasString(line, "toolId") && HasString(line, "toolName");
            case "tool_end":
                return HasString(line, "toolId") && HasString(line, "toolName");
            case "file_modified":
                return HasString(line, "filePath") && HasString(line, "changeType");
            case "result":
                // String content, no subtype: Claude's result frames always
                // carry subtype, so this guard keeps the claim from stealing
                // Claude (and Claude-shaped wrapper) result lines.
                return HasString(line, "content")
                    && !line.TryGetProperty("subtype", out _);
            default:
                return false;
        }
    }

    private static bool HasString(JsonElement line, string name) =>
        line.TryGetProperty(name, out var prop)
        && prop.ValueKind == JsonValueKind.String
        && !string.IsNullOrEmpty(prop.GetString());

    /// <summary>
    /// Maps autohand's tool/result vocabulary onto the shared summary model
    /// on top of the base parse (which already lifts <c>result.content</c>
    /// into final text via its <c>content</c> fallback and reads generic
    /// usage keys). The documented <c>messageType: "usage"</c> frame
    /// (<c>promptTokens</c>/<c>completionTokens</c>) is honoured when present
    /// even though bare 0.9.7 streams were observed without any usage frame —
    /// the headless-mode docs describe it as part of the stream contract.
    /// </summary>
    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type");
        var timestamp = TryTimestamp(root);

        if (string.Equals(type, "tool_start", StringComparison.OrdinalIgnoreCase)
            && FirstString(root, "toolName") is { } startName
            && FirstString(root, "toolId") is { } startId)
        {
            var starts = new List<ToolBuilder>
            {
                new(startId, startName, InputSummary(root), timestamp),
            };
            return ParseScalars(
                root, type!, timestamp, starts, [], isAssistant: true, contentText: null);
        }

        if (string.Equals(type, "tool_end", StringComparison.OrdinalIgnoreCase)
            && FirstString(root, "toolId") is { } endId)
        {
            var succeeded = root.TryGetProperty("toolSuccess", out var successProp)
                && successProp.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? successProp.GetBoolean()
                : (bool?)null;
            var results = new List<ToolResultBuilder>
            {
                new(endId, succeeded, OutputBytes(root), timestamp, FirstDuration(root)),
            };
            return ParseScalars(
                root, type!, timestamp, [], results, isAssistant: false, contentText: null);
        }

        if (string.Equals(type, "message", StringComparison.OrdinalIgnoreCase)
            && string.Equals(FirstString(root, "messageType"), "usage", StringComparison.OrdinalIgnoreCase))
        {
            var input = FirstInt(root, "promptTokens", "prompt_tokens", "input_tokens", "inputTokens");
            var output = FirstInt(root, "completionTokens", "completion_tokens", "output_tokens", "outputTokens");
            var parsed = base.ParseEvent(root);
            return parsed with
            {
                InputTokens = parsed.InputTokens ?? input,
                OutputTokens = parsed.OutputTokens ?? output,
            };
        }

        return base.ParseEvent(root);
    }
}

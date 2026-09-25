using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.DotNetOpencode;

/// <summary>
/// Stream parser for dotnet-opencode's <c>run --format json</c> event lines.
///
/// <para><b>Claim vocabulary (verified live against
/// 0.1.0-ci.20260905083303.33955573552.1).</b> Every event is an envelope
/// <c>{"type":…, "timestamp":…, "sessionID":"ses_…", …}</c> with the type in
/// <c>step_start</c>, <c>text</c>, <c>reasoning</c>, <c>tool_use</c>,
/// <c>step_finish</c>, <c>error</c> (the full set the CLI's run-output layer
/// documents; live frames captured <c>step_start</c> with
/// <c>part.type: "step-start"</c> and <c>Vogen ses_/prt_/msg_</c> ids, plus
/// <c>error</c> frames). No other registered parser claims these verbs
/// (Claude claims <c>assistant/user/result</c>; Codex claims dotted
/// <c>thread./turn./item.</c>; Pi claims <c>agent_/turn_/message_</c>). The
/// claim additionally requires a non-empty string <c>sessionID</c> so a
/// foreign line that merely reuses one of these words is never stolen. The
/// <c>codeybox.stderr</c> envelope the base class folds sandbox stderr into
/// is claimed by the base implementation, not here.</para>
///
/// <para><b>Standalone log pollution.</b> <c>--standalone</c> mode interleaves
/// ASP.NET hosting logs (<c>info: Microsoft.Hosting.Lifetime…</c>) with the
/// event lines on stdout. Those lines are not JSON objects and never reach
/// <see cref="TryClaim"/>; per-line framing in the base parser drops them the
/// same way.</para>
///
/// <para><b>Usage mapping.</b> The shared <c>ParseUsage</c> only knows
/// <c>input_tokens/prompt_tokens</c>-style names; dotnet-opencode reports the
/// server token vocabulary <c>part.tokens:{input, output, reasoning,
/// cache:{read, write}}</c> (field names verified live via
/// <c>stats --json</c> against the same server build), so this parser
/// supplements the base parse with those names (base results win when both
/// are present). <c>cache.write</c> and <c>reasoning</c> have no cost-bucket
/// in <see cref="AgentCostSnapshot"/> and are ignored for token accounting —
/// same treatment as the other providers.</para>
/// </summary>
public sealed class DotNetOpencodeStreamParser : FlexibleAgentStreamParser
{
    public DotNetOpencodeStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.DotNetOpencode, options)
    {
    }

    private static readonly HashSet<string> EventTypes = new(StringComparer.Ordinal)
    {
        "step_start",
        "text",
        "reasoning",
        "tool_use",
        "tool_result",
        "step_finish",
        "error",
    };

    public override bool TryClaim(JsonElement line)
        => IsDotNetOpencodeStreamJsonEvent(line);

    internal static bool IsDotNetOpencodeStreamJsonEvent(JsonElement line)
    {
        if (line.ValueKind != JsonValueKind.Object)
            return false;
        if (!line.TryGetProperty("type", out var typeProp)
            || typeProp.ValueKind != JsonValueKind.String)
            return false;

        var type = typeProp.GetString();
        if (string.IsNullOrEmpty(type) || !EventTypes.Contains(type))
            return false;

        // Every CLI event carries the session id it belongs to; require it so
        // a foreign {"type":"text"} / {"type":"error"} line is never stolen.
        return line.TryGetProperty("sessionID", out var sessionId)
            && sessionId.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(sessionId.GetString());
    }

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var type = FirstString(root, "type", "event", "name") ?? "unknown";
        if (CliAgentRunnerBase.TryReadStderrEnvelope(root, out _))
            return base.ParseEvent(root);

        var parsed = base.ParseEvent(root);
        var isValidFrame = IsDotNetOpencodeStreamJsonEvent(root);

        var isAssistant = parsed.IsAssistant;
        var starts = parsed.ToolStarts.ToList();
        var results = parsed.ToolResults.ToList();
        var finalText = parsed.FinalText;
        var input = parsed.InputTokens;
        var output = parsed.OutputTokens;
        var cached = parsed.CachedInputTokens;
        var timestamp = parsed.Timestamp ?? TryTimestamp(root);

        if (TryGet(root, out var part, "part") && part.ValueKind == JsonValueKind.Object)
        {
            var partType = FirstString(part, "type", "kind");

            // Extract assistant text / content from part.
            var text = FirstString(part, "text", "content");
            if (!string.IsNullOrEmpty(text))
            {
                isAssistant = true;
                finalText = finalText is null ? text : finalText + text;
            }

            // Extract tool use / tool result from part.
            if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "tool_use", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "tool-use", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "tool_call", StringComparison.OrdinalIgnoreCase)
                || string.Equals(partType, "tool-call", StringComparison.OrdinalIgnoreCase))
            {
                isAssistant = true;
                var toolId = FirstString(part, "call_id", "tool_use_id", "id") ?? Guid.NewGuid().ToString("N");
                var toolName = FirstString(part, "name", "tool_name", "tool") ?? "unknown";
                starts = [new ToolBuilder(toolId, toolName, InputSummary(part), timestamp)];
            }
            else if (string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(partType, "tool_result", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(partType, "tool-result", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(partType, "tool_response", StringComparison.OrdinalIgnoreCase))
            {
                var toolId = FirstString(part, "tool_use_id", "call_id", "id") ?? "unknown";
                results = [new ToolResultBuilder(toolId, !Bool(part, "is_error", "error"), OutputBytes(part), timestamp, FirstDuration(part))];
            }

            // Supplement (never override) the base usage parse with the server
            // token vocabulary, which rides on the step_finish part object.
            if (TryGet(part, out var tokens, "tokens") && tokens.ValueKind == JsonValueKind.Object)
            {
                input ??= FirstNullableInt(tokens, "input");
                output ??= FirstNullableInt(tokens, "output");
                if (cached is null && TryGet(tokens, out var cache, "cache") && cache.ValueKind == JsonValueKind.Object)
                    cached = FirstNullableInt(cache, "read");
            }
        }

        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            isAssistant = true;
        }

        if (TryGet(root, out var error, "error") && error.ValueKind == JsonValueKind.Object)
        {
            var msg = FirstString(error, "message");
            if (!string.IsNullOrEmpty(msg))
                finalText ??= msg;
        }

        var isRecognized = parsed.IsRecognized
            || isValidFrame
            || input.HasValue
            || output.HasValue
            || cached.HasValue
            || starts.Count > 0
            || results.Count > 0
            || isAssistant;

        return parsed with
        {
            IsAssistant = isAssistant,
            ToolStarts = starts,
            ToolResults = results,
            FinalText = finalText,
            InputTokens = input,
            OutputTokens = output,
            CachedInputTokens = cached,
            IsRecognized = isRecognized,
            EventType = NormalizeType(type, starts, results, isAssistant),
        };
    }

    private static int? FirstNullableInt(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var n) && n >= 0)
            return n;
        return null;
    }
}

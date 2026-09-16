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
        var parsed = base.ParseEvent(root);

        // Supplement (never override) the base usage parse with the server
        // token vocabulary, which rides on the step_finish part object.
        if (TryGet(root, out var part, "part") && part.ValueKind == JsonValueKind.Object
            && TryGet(part, out var tokens, "tokens") && tokens.ValueKind == JsonValueKind.Object)
        {
            var input = parsed.InputTokens ?? FirstNullableInt(tokens, "input");
            var output = parsed.OutputTokens ?? FirstNullableInt(tokens, "output");
            int? cached = parsed.CachedInputTokens;
            if (cached is null && TryGet(tokens, out var cache, "cache") && cache.ValueKind == JsonValueKind.Object)
                cached = FirstNullableInt(cache, "read");
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

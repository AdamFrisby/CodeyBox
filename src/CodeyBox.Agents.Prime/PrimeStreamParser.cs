using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// Stream parser for prime-agent's <c>-p --mode json</c> event lines.
///
/// <para><b>Claim policy (verified against prime-agent 0.9.5 live
/// frames).</b> Prime emits the pi wire shape byte-for-byte: the session
/// header <c>{"type":"session","version":3,"id":…,"cwd":…}</c> (plus an
/// additive <c>rlmDepth</c>), the same underscore-style lifecycle verbs
/// (<c>agent_start</c>, <c>turn_start</c>, <c>message_start</c>,
/// <c>message_update</c>, <c>message_end</c>, <c>turn_end</c>,
/// <c>agent_end</c>), cumulative
/// <c>usage:{input, output, cacheRead, cacheWrite, totalTokens}</c>, and the
/// dispatch model as <c>message.model</c>. Additive prime-only details
/// (<c>rlmDepth</c>, <c>harness_digest</c> messages, <c>auth_stale</c> /
/// <c>auto_retry_*</c> events) are envelope decoration, not reliable
/// discriminators — the CLIs share lineage and pi could gain them. So this
/// parser deliberately does NOT claim by shape (like the cursor parser over
/// the Claude shape): claiming would steal real pi streams depending on
/// registration order. Attribution is delegated to
/// <c>AgentStreamParserSelection.ResolveKind</c>, which uses the work item's
/// declared agent and cost rows — the authoritative prime signal at
/// orchestration time.</para>
/// </summary>
public sealed class PrimeStreamParser : FlexibleAgentStreamParser
{
    public PrimeStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Prime, options)
    {
    }

    /// <summary>
    /// Never claims: the on-wire vocabulary is byte-identical to pi's (see
    /// the class doc), and <see cref="PiStreamParser"/> owns that shape.
    /// Declaring <see cref="CanEmitShapeOf"/> instead keeps the
    /// parser-shape compatibility matrix on the parser itself rather than
    /// the orchestrator's resolver.
    /// </summary>
    public override bool TryClaim(JsonElement line) => false;

    /// <summary>
    /// Prime speaks the literal pi shape, so a pi-sniffed stream may have
    /// been produced by a dispatched prime run. Declaring both shapes here
    /// lets <c>AgentStreamParserSelection.ResolveKind</c> attribute such
    /// streams to prime when the work item / cost row says so.
    /// </summary>
    public override bool CanEmitShapeOf(AgentKind sniffed) =>
        string.Equals(sniffed.Value, AgentKind.Prime.Value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sniffed.Value, AgentKind.Pi.Value, StringComparison.OrdinalIgnoreCase);

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var parsed = base.ParseEvent(root);

        // Supplement (never override) the base usage parse with the
        // pi-family field names via the shared helper — the same call the pi
        // parser makes, so the two can never diverge on one wire shape.
        var message = PiShapeParsing.MessageEnvelope(root);

        if (message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var input = parsed.InputTokens ?? PiShapeParsing.TryReadUsageCounter(usage, "input");
            var output = parsed.OutputTokens ?? PiShapeParsing.TryReadUsageCounter(usage, "output");
            var cached = parsed.CachedInputTokens ?? PiShapeParsing.TryReadUsageCounter(usage, "cacheRead");
            if (input != parsed.InputTokens || output != parsed.OutputTokens || cached != parsed.CachedInputTokens)
                parsed = parsed with { InputTokens = input, OutputTokens = output, CachedInputTokens = cached };
        }

        return parsed;
    }
}

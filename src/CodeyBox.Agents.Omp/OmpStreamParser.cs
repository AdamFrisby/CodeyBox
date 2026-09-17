using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Omp;

/// <summary>
/// Stream parser for omp's <c>-p --mode json</c> event lines.
///
/// <para><b>Claim policy (verified against omp 18.2.2 live frames).</b> OMP
/// emits the pi wire shape: the session header
/// <c>{"type":"session","version":3,"id":…,"cwd":…}</c>, the same
/// underscore-style lifecycle verbs (<c>agent_start</c>, <c>turn_start</c>,
/// <c>message_start</c>, <c>message_update</c>, <c>message_end</c>,
/// <c>turn_end</c>, <c>agent_end</c>), cumulative
/// <c>usage:{input, output, cacheRead, cacheWrite, totalTokens}</c> (plus an
/// additive <c>reasoningTokens</c> with no cost bucket), and the dispatch
/// model as <c>message.model</c>. The CLIs share lineage (omp is the oh-my-pi
/// fork of pi) and the vocabulary is byte-identical, so this parser
/// deliberately does NOT claim by shape (like the prime parser over the
/// same shape): claiming would steal real pi streams depending on
/// registration order. Attribution is delegated to
/// <c>AgentStreamParserSelection.ResolveKind</c>, which uses the work item's
/// declared agent and cost rows — the authoritative omp signal at
/// orchestration time.</para>
/// </summary>
public sealed class OmpStreamParser : FlexibleAgentStreamParser
{
    public OmpStreamParser(AgentStreamParserOptions? options = null)
        : base(AgentKind.Omp, options)
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
    /// OMP speaks the literal pi shape, so a pi-sniffed stream may have been
    /// produced by a dispatched omp run. Declaring both shapes here lets
    /// <c>AgentStreamParserSelection.ResolveKind</c> attribute such streams
    /// to omp when the work item / cost row says so.
    /// </summary>
    public override bool CanEmitShapeOf(AgentKind sniffed) =>
        string.Equals(sniffed.Value, AgentKind.Omp.Value, StringComparison.OrdinalIgnoreCase)
        || string.Equals(sniffed.Value, AgentKind.Pi.Value, StringComparison.OrdinalIgnoreCase);

    protected override ParsedEvent ParseEvent(JsonElement root)
    {
        var parsed = base.ParseEvent(root);

        // Supplement (never override) the base usage parse with the
        // pi-family field names via the shared helper — the same call the pi
        // and prime parsers make, so the three can never diverge on one wire
        // shape. reasoningTokens has no cost bucket and is ignored, same as
        // cacheWrite.
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

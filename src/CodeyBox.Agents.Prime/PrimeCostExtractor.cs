using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Prime;

/// <summary>
/// Best-effort token-count extractor for prime-agent's <c>-p --mode
/// json</c> event stream.
///
/// <para>Every assistant <c>message_end</c> frame embeds the
/// provider-reported cumulative <c>message.usage</c> object
/// (<c>{input, output, cacheRead, cacheWrite, totalTokens, cost:{…}}</c> —
/// verified against prime-agent 0.9.5 live frames: a real OpenRouter run
/// reported <c>{input:1185, output:104, cacheRead:4352, …}</c> while error
/// runs report all zeros). The extractor scans every JSON line of
/// stdout/stderr and keeps the latest non-zero usage object as the run
/// total. The dispatch model id rides alongside on the same frames as
/// <c>message.model</c> (reported verbatim, e.g.
/// <c>nvidia/nemotron-3.5-lightning:free</c>) and is recorded for per-model
/// rate lookup. Frame scanning lives in
/// <see cref="PiShapeParsing.ScanLatestUsage"/> (shared with the pi
/// extractor — both CLIs speak the same wire shape).</para>
///
/// <para>No <see cref="DefaultPricing"/> is shipped: prime fronts many
/// providers with unrelated per-token economics, so no single fallback rate
/// is honest. Per-model rates ship in <c>agent-pricing-defaults.json</c> or
/// as operator overrides under <c>CodeyBox:AgentPricing</c>, keyed by the
/// model id this extractor records. A stream with no usage frame yields null
/// (unknown) — never a zero that looks like data.</para>
/// </summary>
public sealed class PrimeCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Prime;

    public ModelRateConfig? DefaultPricing { get; } = null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        try
        {
            var fromStdout = ScanStream(agentStdout);
            var fromStderr = ScanStream(agentStderr);
            // Prefer the stream with the larger reported total — stderr may
            // carry a truncated retry while stdout holds the full session.
            if (fromStdout is null) return fromStderr;
            if (fromStderr is null) return fromStdout;
            var stdoutTotal = fromStdout.InputTokens + fromStdout.CachedInputTokens + fromStdout.OutputTokens;
            var stderrTotal = fromStderr.InputTokens + fromStderr.CachedInputTokens + fromStderr.OutputTokens;
            return stderrTotal > stdoutTotal ? fromStderr : fromStdout;
        }
        catch (Exception)
        {
            // Contract: implementations must never throw.
            return null;
        }
    }

    private static AgentCostSnapshot? ScanStream(string? text)
    {
        var found = PiShapeParsing.ScanLatestUsage(text);
        if (found is not { } usage)
            return null;
        return new AgentCostSnapshot(usage.Input, usage.Cached, usage.Output, usage.ModelId);
    }
}

using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Omp;

/// <summary>
/// Best-effort token-count extractor for omp's <c>-p --mode json</c> event
/// stream.
///
/// <para>Every assistant <c>message_end</c> / <c>turn_end</c> frame embeds
/// the provider-reported cumulative <c>message.usage</c> object
/// (<c>{input, output, cacheRead, cacheWrite, totalTokens, reasoningTokens,
/// cost:{…}}</c> — field names verified against omp 18.2.2 live frames).
/// The extractor scans every JSON line of stdout/stderr and keeps the LATEST
/// usage object: usage is cumulative per session, so the last frame is the
/// run total (verified: the success frame's <c>totalTokens</c> equals
/// <c>input + output</c> with zero cache activity on that run, and a
/// cache-active run reported <c>cacheRead</c> as the cached-input bucket).
/// The dispatch model id rides alongside on the same frames as
/// <c>message.model</c> (the bare provider-catalog id — omp strips the
/// <c>openrouter/</c> qualifier) and is recorded for per-model rate lookup.
/// Frame scanning itself lives in <see cref="PiShapeParsing.ScanLatestUsage"/>
/// (shared with the pi/prime extractors — all three CLIs speak the same
/// wire shape). <c>reasoningTokens</c> and <c>cacheWrite</c> have no bucket
/// in <see cref="AgentCostSnapshot"/> and are ignored for token accounting
/// — same treatment as the other providers.</para>
///
/// <para>No <see cref="DefaultPricing"/> is shipped: omp fronts ~60
/// providers with unrelated per-token economics, so no single fallback rate
/// is honest. Per-model rates ship in <c>agent-pricing-defaults.json</c> or
/// as operator overrides under <c>CodeyBox:AgentPricing</c>, keyed
/// <c>omp/&lt;model-id&gt;</c> by the bare id this extractor records (the
/// shipped free-tier member bills $0 via an explicit zero-rate bucket).
/// Unrated models cost $0 with a startup warning (see
/// <c>AgentCostCalculator.ValidateAtStartup</c>) — mirroring the
/// Cursor/Copilot subscription-path posture. Error runs report all-zero
/// usage and yield null (unknown), never a zero that looks like data.</para>
/// </summary>
public sealed class OmpCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Omp;

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

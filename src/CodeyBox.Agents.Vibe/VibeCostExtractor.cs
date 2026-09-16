using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Vibe;

/// <summary>
/// Cost extractor for vibe's <c>--output streaming</c> event stream.
///
/// <para>Programmatic history entries carry no token counts (verified against
/// vibe 2.25.4 live frames: success, tool-use, and provider-failure runs
/// alike emit no usage object anywhere in the stream — per-session totals
/// live only in the in-process <c>AgentStatsSnapshot</c>, which
/// programmatic mode never prints). Returning <c>null</c> from
/// <see cref="TryExtract"/> means the pipeline records a zero-token,
/// zero-cost row whose timestamps still feed <c>usageTotal.elapsedMs</c> for
/// time-spent visibility — the honest signal, rather than a fabricated
/// default that looks like data. If a future vibe release prints usage,
/// parse it here.</para>
///
/// <para><see cref="DefaultPricing"/> is null because cost is unknown — the
/// model bills to the operator's own provider account (OpenRouter list
/// prices for the shipped member), so a per-million-token fallback rate
/// would be misleading. Callers treat that as $0 and the elapsed-time
/// fallback gives the operator the only meaningful signal — mirroring the
/// Cursor subscription-path posture.</para>
/// </summary>
public sealed class VibeCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Vibe;

    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
        => null;
}

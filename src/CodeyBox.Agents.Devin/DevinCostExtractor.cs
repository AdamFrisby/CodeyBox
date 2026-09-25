using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Cost extractor for devin CLI output. Always returns <c>null</c>.
///
/// <para>The ACP-mode dispatch stream DOES carry token counters — the shim's
/// <c>devin.acp</c> envelopes wrap <c>turn_complete</c> usage objects and
/// cumulative <c>usage_update</c> <c>_meta</c> counters — but they are
/// deliberately NOT promoted to extracted usage. Envelope payloads are
/// agent-influenceable telemetry (<see cref="DevinAcpEnvelope.IsEnvelope"/>
/// bounds the claim: a same-uid, sudo-capable in-VM writer can inject
/// envelope lines via <c>/proc/&lt;pid&gt;/fd</c>, and a tool subprocess can
/// write the shim's ACP wire pipe). A forged trailing
/// <c>turn_complete</c>/<c>usage_update</c> with zeroed counters would
/// otherwise record a <c>has_extracted_token_usage = 1</c> row that settles
/// the paid-quota escrow at zero (<c>QuotaReservationLedger.Complete</c>
/// releases the reservation when observed usage is ~0) and drags the
/// burn-estimate samples (<c>GetAvgTokensPerItemAsync</c>) down, loosening
/// the rate-aware dispatch gate. Returning <c>null</c> keeps every devin
/// cost row on the elapsed fallback: the reservation estimate is retained,
/// exactly as it was before the ACP switch.</para>
///
/// <para><see cref="DefaultPricing"/> is null because cost is unknown —
/// consumption is billed as ACUs on the operator's Devin subscription, so a
/// per-million-token rate would be misleading. Callers treat that as $0 and
/// the elapsed-time fallback gives the operator the only meaningful
/// signal.</para>
/// </summary>
public sealed class DevinCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Devin;

    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
        => null;
}

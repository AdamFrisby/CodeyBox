using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Cost extractor for devin ACP-mode output.
///
/// <para>The dispatch stream carries the shim's <c>devin.acp</c> envelopes
/// (scanned via <see cref="DevinAcpEnvelope"/>, the shared reader);
/// <c>turn_complete</c> wraps the ACP <c>session/prompt</c> response whose
/// <c>usage</c> object reports the turn's token totals (verified against
/// devin 3000.11.1: <c>inputTokens</c>/<c>outputTokens</c>/
/// <c>totalTokens</c>). The <c>usage_update</c> envelopes'
/// <c>_meta["cognition.ai/*"]</c> bags are cumulative per turn, so the
/// LAST tick's counters win — supplying buckets the terminal usage object
/// lacks (e.g. cached input). A stream with neither yields <c>null</c> so
/// the pipeline still records a zero-token row whose timestamps feed
/// <c>usageTotal.elapsedMs</c>. The recorded counters are shim-reported
/// telemetry — <see cref="DevinAcpEnvelope.IsEnvelope"/> bounds the claim:
/// a same-uid in-VM writer can still inject envelope lines, so the numbers
/// are usage signal reconcilable against Devin's server-side accounting,
/// not authoritative billing.</para>
///
/// <para><see cref="DefaultPricing"/> is null because cost is unknown —
/// consumption is billed as ACUs on the operator's Devin subscription, so a
/// per-million-token rate would be misleading. Callers treat that as $0 and
/// the elapsed-time fallback gives the operator the only meaningful
/// signal. <see cref="AgentCostSnapshot.ModelId"/> is left null so the
/// recorder's dispatch-model fallback wins; the ACP stream only carries the
/// display label (<c>SWE-2 High</c>), not the configured model id.</para>
/// </summary>
public sealed class DevinCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Devin;

    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        int? inputTokens = null, outputTokens = null, cachedInputTokens = null;
        var sawUsage = false;

        foreach (var envelope in DevinAcpEnvelope.Enumerate(agentStdout))
        {
            var root = envelope.Root;
            JsonElement usageBag;
            if (envelope.Event == DevinAcpEnvelope.EventTurnComplete
                && DevinAcpEnvelope.TryGetTurnUsage(root, out var usage))
            {
                // The terminal envelope's totals win over every
                // intermediate usage_update tick (it arrives last).
                usageBag = usage;
            }
            else if (envelope.Event == DevinAcpEnvelope.EventSessionUpdate
                     && DevinAcpEnvelope.TryGetUsageUpdateMeta(root, out var meta))
            {
                // The _meta counters are cumulative over the turn, so the
                // last tick carries the totals — same last-wins policy as
                // the terminal envelope for every bucket.
                usageBag = meta;
            }
            else
            {
                continue;
            }

            var reading = DevinAcpEnvelope.ReadUsage(usageBag);
            inputTokens = reading.Input ?? inputTokens;
            outputTokens = reading.Output ?? outputTokens;
            cachedInputTokens = reading.CachedInput ?? cachedInputTokens;
            sawUsage = true;
        }

        return sawUsage
            ? new AgentCostSnapshot(inputTokens ?? 0, cachedInputTokens ?? 0, outputTokens ?? 0, ModelId: null)
            : null;
    }
}

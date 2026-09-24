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
/// <c>totalTokens</c>). The last <c>usage_update</c> envelope's
/// <c>_meta["cognition.ai/cachedReadTokens"]</c> supplies the cached-input
/// bucket. A stream with neither yields <c>null</c> so the pipeline still
/// records a zero-token row whose timestamps feed
/// <c>usageTotal.elapsedMs</c>.</para>
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
            if (envelope.Event == DevinAcpEnvelope.EventTurnComplete
                && root.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                // The terminal envelope's totals win over every
                // intermediate usage_update tick.
                var turn = DevinAcpEnvelope.ReadUsage(usage);
                inputTokens = turn.Input ?? inputTokens;
                outputTokens = turn.Output ?? outputTokens;
                cachedInputTokens = turn.CachedInput ?? cachedInputTokens;
                sawUsage = true;
            }
            else if (envelope.Event == DevinAcpEnvelope.EventSessionUpdate
                     && TryGetUsageUpdateMeta(root, out var meta))
            {
                var tick = DevinAcpEnvelope.ReadUsage(meta);
                inputTokens ??= tick.Input;
                outputTokens ??= tick.Output;
                cachedInputTokens = tick.CachedInput ?? cachedInputTokens;
                sawUsage = true;
            }
        }

        return sawUsage
            ? new AgentCostSnapshot(inputTokens ?? 0, cachedInputTokens ?? 0, outputTokens ?? 0, ModelId: null)
            : null;
    }

    private static bool TryGetUsageUpdateMeta(JsonElement root, out JsonElement meta)
    {
        meta = default;
        return root.TryGetProperty("update", out var update)
            && update.ValueKind == JsonValueKind.Object
            && update.TryGetProperty("sessionUpdate", out var sessionUpdate)
            && sessionUpdate.ValueKind == JsonValueKind.String
            && sessionUpdate.GetString() == DevinAcpEnvelope.UpdateKindUsageUpdate
            && update.TryGetProperty("_meta", out meta)
            && meta.ValueKind == JsonValueKind.Object;
    }
}

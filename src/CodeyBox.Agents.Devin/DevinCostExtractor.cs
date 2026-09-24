using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Devin;

/// <summary>
/// Cost extractor for devin ACP-mode output.
///
/// <para>The dispatch stream carries the shim's <c>devin.acp</c> envelopes;
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
        if (string.IsNullOrWhiteSpace(agentStdout))
            return null;

        int? inputTokens = null, outputTokens = null, cachedInputTokens = null;
        var sawUsage = false;

        foreach (var rawLine in agentStdout.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] != '{')
                continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeEl)
                    || typeEl.ValueKind != JsonValueKind.String
                    || typeEl.GetString() != DevinAcpOutcome.EnvelopeType
                    || !root.TryGetProperty("event", out var eventEl)
                    || eventEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (eventEl.GetString() == "turn_complete"
                    && root.TryGetProperty("usage", out var usage)
                    && usage.ValueKind == JsonValueKind.Object)
                {
                    // The terminal envelope's totals win over every
                    // intermediate usage_update tick.
                    inputTokens = IntOf(usage, "inputTokens") ?? inputTokens;
                    outputTokens = IntOf(usage, "outputTokens") ?? outputTokens;
                    sawUsage = true;
                }
                else if (eventEl.GetString() == "session_update"
                         && root.TryGetProperty("update", out var update)
                         && update.ValueKind == JsonValueKind.Object
                         && update.TryGetProperty("sessionUpdate", out var su)
                         && su.ValueKind == JsonValueKind.String
                         && su.GetString() == "usage_update"
                         && update.TryGetProperty("_meta", out var meta)
                         && meta.ValueKind == JsonValueKind.Object)
                {
                    inputTokens ??= IntOf(meta, "cognition.ai/inputTokens");
                    outputTokens ??= IntOf(meta, "cognition.ai/outputTokens");
                    cachedInputTokens = IntOf(meta, "cognition.ai/cachedReadTokens")
                        ?? IntOf(meta, "cognition.ai/cached_input_tokens")
                        ?? cachedInputTokens;
                    sawUsage = true;
                }
            }
        }

        return sawUsage
            ? new AgentCostSnapshot(inputTokens ?? 0, cachedInputTokens ?? 0, outputTokens ?? 0, ModelId: null)
            : null;
    }

    private static int? IntOf(JsonElement el, string name)
        => el.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
}

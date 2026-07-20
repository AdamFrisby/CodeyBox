using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.Crock;

/// <summary>
/// Extracts token counts from CrockCode CLI output for cost-attribution rows.
///
/// <para>CrockCode submits work to Anthropic's Message Batches API; the
/// terminal <c>crock status</c> output carries an Anthropic usage envelope
/// (<c>input_tokens</c> / <c>cache_creation_input_tokens</c> /
/// <c>cache_read_input_tokens</c> / <c>output_tokens</c>) — the same shape
/// <see cref="CodeyBox.Agents.Claude.ClaudeCostExtractor"/> consumes. This
/// extractor recognises that shape verbatim and also accepts a human-readable
/// footer that mirrors Claude's <c>"$cost (X input, Y output, Z cached tokens)"</c>
/// summary line — both shapes have been observed in CrockCode preview builds,
/// so we try the NDJSON path first and fall back to the footer pattern.</para>
///
/// <para><b>Pricing.</b> The per-model rates in
/// <c>agent-pricing-defaults.json</c> under the <c>crock</c> bucket are the
/// post-batch-discount effective rates (half of the on-demand
/// <c>/v1/messages</c> rate, since Anthropic applies the ~50% batch discount
/// at billing time), and the unknown-model fallback lives in that file's
/// <c>DefaultRates.crock</c> entry. This extractor holds NO compiled rate — the
/// pricing config is the single source of truth. Cache-write tokens are folded
/// into fresh input at the base rate, so the resulting spend is a conservative
/// estimate, not exact billing (see the <c>crock</c> note in that file).</para>
/// </summary>
public sealed class CrockCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.Crock;

    /// <summary>
    /// No compiled pricing fallback. All crock rates — including the
    /// unknown-model default — live only in <c>agent-pricing-defaults.json</c>
    /// (the <c>crock</c> per-model bucket plus its <c>DefaultRates.crock</c>
    /// entry), so the hot-reloadable pricing config is the single source of
    /// truth and cannot drift from a stale compiled literal. A model with no
    /// configured rate is declined (priced at zero) rather than charged off an
    /// in-source constant.
    /// </summary>
    public ModelRateConfig? DefaultPricing => null;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        if (string.IsNullOrWhiteSpace(agentStdout) && string.IsNullOrWhiteSpace(agentStderr))
            return null;

        var ndJson = TryParseNdJson(agentStdout);
        if (ndJson is not null) return ndJson;

        return AnthropicUsageParsing.TryParseHumanReadable(agentStdout)
            ?? AnthropicUsageParsing.TryParseHumanReadable(agentStderr);
    }

    private static AgentCostSnapshot? TryParseNdJson(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        // Prefer the typed `type:"result"` envelope (Anthropic's terminal
        // billable totals); fall back to the bare `usage` shape only when
        // no result envelope is seen. ClaudeCostExtractor uses the same
        // ordering precisely because a per-message partial `usage` line
        // emitted AFTER the result envelope would otherwise clobber the
        // final totals.
        int resultInput = 0, resultOutput = 0, resultCached = 0;
        bool sawResult = false;
        int bareInput = 0, bareOutput = 0, bareCached = 0;
        bool sawBareUsage = false;
        string? modelId = null;

        foreach (var line in stdout.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var isResult = root.TryGetProperty("type", out var typeProp)
                    && typeProp.ValueKind == JsonValueKind.String
                    && string.Equals(typeProp.GetString(), "result", StringComparison.Ordinal);

                if (isResult && root.TryGetProperty("usage", out var typedUsage))
                {
                    AnthropicUsageParsing.ExtractUsageCounts(typedUsage, out var input, out var output, out var cached);
                    resultInput = input;
                    resultOutput = output;
                    resultCached = cached;
                    sawResult = true;
                }
                else if (!isResult
                    && root.TryGetProperty("usage", out var bareUsage)
                    && bareUsage.ValueKind == JsonValueKind.Object)
                {
                    AnthropicUsageParsing.ExtractUsageCounts(bareUsage, out var input, out var output, out var cached);
                    bareInput = input;
                    bareOutput = output;
                    bareCached = cached;
                    sawBareUsage = true;
                }

                if (modelId is null && root.TryGetProperty("model", out var topModel)
                    && topModel.ValueKind == JsonValueKind.String)
                {
                    var raw = topModel.GetString();
                    modelId = raw is { Length: > 128 } ? raw[..128] : raw;
                }
                else if (modelId is null && root.TryGetProperty("message", out var msg)
                    && msg.ValueKind == JsonValueKind.Object
                    && msg.TryGetProperty("model", out var nestedModel)
                    && nestedModel.ValueKind == JsonValueKind.String)
                {
                    var raw = nestedModel.GetString();
                    modelId = raw is { Length: > 128 } ? raw[..128] : raw;
                }
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }

        if (sawResult)
            return new AgentCostSnapshot(resultInput, resultCached, resultOutput, modelId);
        if (sawBareUsage)
            return new AgentCostSnapshot(bareInput, bareCached, bareOutput, modelId);
        return null;
    }
}

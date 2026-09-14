using System.Text.Json;
using CodeyBox.Agents;
using CodeyBox.Core;

namespace CodeyBox.Agents.CavemanCode;

/// <summary>
/// Best-effort token-count extractor for caveman-code CLI output.
///
/// <para>Plain-text <c>-p</c> runs emit only the final assistant prose, so a
/// normal dispatch yields no counts and this returns null (zero-cost row,
/// same as opencode when its CLI stays quiet). What IS parsed is
/// machine-shaped JSON only — never prose:</para>
/// <list type="bullet">
/// <item>caveman camelCase <c>usage</c> objects
/// (<c>inputTokens</c>/<c>outputTokens</c>), the field names the CLI's own
/// exec event translator reads (shipped <c>dist/modes/exec/event-stream.js</c>,
/// 0.65.2), at <c>root.usage</c> or <c>root.message.usage</c>;</item>
/// <item>Anthropic snake_case envelopes via the shared
/// <see cref="AnthropicUsageParsing.ExtractUsageCounts"/> (same shape the
/// Claude extractor consumes);</item>
/// <item>OpenAI <c>prompt_tokens</c>/<c>completion_tokens</c> objects
/// including <c>prompt_tokens_details.cached_tokens</c>.</item>
/// </list>
///
/// <para>Prose token mentions (e.g. an agent writing "10 input tokens" in its
/// answer) are deliberately NOT matched: with no usage footer in text mode,
/// any such match could only come from the model's own words and would
/// fabricate spend. Totals accumulate across usage-bearing lines, matching
/// the CLI's per-assistant-message cost events (each
/// <c>message.assistant</c> carries its own turn cost; the session total is
/// their sum). Counters are clamped non-negative with saturating adds —
/// agent stdout is a less-trusted input to budget gates.</para>
///
/// <para>No <see cref="DefaultPricing"/> is shipped: caveman-code fronts many
/// providers with different per-token economics. Per-model rates ship in
/// <c>agent-pricing-defaults.json</c> under the <c>caveman</c> bucket (both
/// bare <c>gpt-5.5</c> and provider-prefixed <c>openai/gpt-5.5</c> key forms,
/// since the CLI reports either depending on how <c>--model</c> was given)
/// and stay hot-reloadable under <c>CodeyBox:AgentPricing</c>.</para>
/// </summary>
public sealed class CavemanCodeCostExtractor : IAgentCostExtractor
{
    public AgentKind Kind => AgentKind.CavemanCode;

    public ModelRateConfig? DefaultPricing { get; } = null;

    private const int MaxModelIdLength = 128;

    public AgentCostSnapshot? TryExtract(string? agentStdout, string? agentStderr)
    {
        if (string.IsNullOrWhiteSpace(agentStdout) && string.IsNullOrWhiteSpace(agentStderr))
            return null;

        var fromStdout = TryParseJsonLines(agentStdout);
        var fromStderr = TryParseJsonLines(agentStderr);
        if (fromStdout is null) return fromStderr;
        if (fromStderr is null) return fromStdout;
        return new AgentCostSnapshot(
            SaturatingAdd(fromStdout.InputTokens, fromStderr.InputTokens),
            SaturatingAdd(fromStdout.CachedInputTokens, fromStderr.CachedInputTokens),
            SaturatingAdd(fromStdout.OutputTokens, fromStderr.OutputTokens),
            fromStdout.ModelId ?? fromStderr.ModelId);
    }

    private static AgentCostSnapshot? TryParseJsonLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Fast reject: without a usage key no line can carry counts, and
        // full JSON parsing of multi-MB prose is wasted work.
        if (text.IndexOf("usage", StringComparison.OrdinalIgnoreCase) < 0
            && text.IndexOf("inputTokens", StringComparison.Ordinal) < 0)
            return null;

        long input = 0, cached = 0, output = 0;
        var sawUsage = false;
        string? modelId = null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var usage = FindUsageObject(root);
                if (usage is null) continue;

                if (TryExtractCounts(usage.Value, out var lineInput, out var lineCached, out var lineOutput)
                    && (lineInput > 0 || lineCached > 0 || lineOutput > 0))
                {
                    input = Math.Min((long)int.MaxValue, input + lineInput);
                    cached = Math.Min((long)int.MaxValue, cached + lineCached);
                    output = Math.Min((long)int.MaxValue, output + lineOutput);
                    sawUsage = true;
                }

                modelId ??= ReadModelId(root);
            }
        }

        if (!sawUsage) return null;
        return new AgentCostSnapshot((int)input, (int)cached, (int)output, modelId);
    }

    private static JsonElement? FindUsageObject(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var direct)
            && direct.ValueKind == JsonValueKind.Object)
            return direct;

        // message_end-style envelope: { ..., message: { role, usage: {...} } }.
        if (root.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("usage", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
            return nested;

        return null;
    }

    private static bool TryExtractCounts(JsonElement usage, out int input, out int cached, out int output)
    {
        input = 0;
        cached = 0;
        output = 0;

        // caveman camelCase shape (field names read by the CLI's own exec
        // event translator). Only the verified inputTokens/outputTokens are
        // read here; no cached camelCase field has been observed, so none is
        // guessed.
        if (usage.TryGetProperty("inputTokens", out _)
            || usage.TryGetProperty("outputTokens", out _))
        {
            input = ReadNonNegative(usage, "inputTokens");
            output = ReadNonNegative(usage, "outputTokens");
            return true;
        }

        // Anthropic snake_case shape (shared parser: folds cache-creation
        // into fresh input, keeps cache-read separate).
        if (usage.TryGetProperty("input_tokens", out _)
            || usage.TryGetProperty("output_tokens", out _))
        {
            AnthropicUsageParsing.ExtractUsageCounts(usage, out input, out output, out cached);
            return true;
        }

        // OpenAI shape.
        if (usage.TryGetProperty("prompt_tokens", out _)
            || usage.TryGetProperty("completion_tokens", out _))
        {
            var totalInput = ReadNonNegative(usage, "prompt_tokens");
            output = ReadNonNegative(usage, "completion_tokens");
            cached = 0;
            if (usage.TryGetProperty("prompt_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                cached = ReadNonNegative(details, "cached_tokens");
            }

            input = TokenUsageAccounting.FreshInputTokens(totalInput, cached);
            return true;
        }

        return false;
    }

    private static int ReadNonNegative(JsonElement obj, string propertyName)
        => obj.TryGetProperty(propertyName, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var v)
            && v > 0
                ? v
                : 0;

    private static string? ReadModelId(JsonElement root)
    {
        if (root.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.String)
        {
            var raw = model.GetString();
            if (!string.IsNullOrEmpty(raw))
                return raw.Length > MaxModelIdLength ? raw[..MaxModelIdLength] : raw;
        }

        return null;
    }

    private static int SaturatingAdd(int a, int b)
    {
        var sum = (long)a + b;
        return sum > int.MaxValue ? int.MaxValue : (int)sum;
    }
}
